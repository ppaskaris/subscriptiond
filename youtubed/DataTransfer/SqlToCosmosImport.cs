using Dapper;
using Microsoft.Azure.Cosmos;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using youtubed.Domain;
using youtubed.Persistence.Cosmos;
using youtubed.Services;

namespace youtubed.DataTransfer
{
    internal enum SqlToCosmosImportMode
    {
        Validate,
        Import,
        Reconcile
    }

    internal enum SqlToCosmosImportError
    {
        FirstImportConfirmationRequired,
        RerunConfirmationRequired,
        TargetContainsShareLinks,
        TargetDoesNotMatchInterruptedImport,
        ReconciliationContainsShareLinks,
        ReconciliationListMismatch,
        ReconciliationChannelMismatch,
        DuplicateDeterministicId,
        TargetConfigurationInvalid
    }

    internal sealed class SqlToCosmosImportOperationException : Exception
    {
        public SqlToCosmosImportOperationException(SqlToCosmosImportError error)
            : base(GetMessage(error))
        {
            Error = error;
        }

        public SqlToCosmosImportError Error { get; }

        private static string GetMessage(SqlToCosmosImportError error)
        {
            return error switch
            {
                SqlToCosmosImportError.FirstImportConfirmationRequired =>
                    "The verified-empty first import requires --confirm-empty-target.",
                SqlToCosmosImportError.RerunConfirmationRequired =>
                    "A non-empty target requires --confirm-pre-cutover-rerun and must remain offline.",
                SqlToCosmosImportError.TargetContainsShareLinks =>
                    "The target contains share links; discard it and restart with a fresh empty migration target.",
                SqlToCosmosImportError.TargetDoesNotMatchInterruptedImport =>
                    "The target is not a matching interrupted-import subset; discard it and restart with a fresh empty migration target.",
                SqlToCosmosImportError.ReconciliationContainsShareLinks =>
                    "Reconciliation found target share links; do not cut over.",
                SqlToCosmosImportError.ReconciliationListMismatch =>
                    "Reconciliation found list differences; do not cut over.",
                SqlToCosmosImportError.ReconciliationChannelMismatch =>
                    "Reconciliation found channel differences; do not cut over.",
                SqlToCosmosImportError.DuplicateDeterministicId =>
                    "The source or target contains a duplicate deterministic ID; do not continue the migration.",
                SqlToCosmosImportError.TargetConfigurationInvalid =>
                    "The target configuration is invalid; verify the database, 1,000 RU/s shared throughput, and all three container policies.",
                _ => throw new ArgumentOutOfRangeException(nameof(error))
            };
        }
    }

    internal sealed record SqlImportList(
        Guid Id,
        byte[] Token,
        string Title,
        decimal PlaybackRate,
        DateTimeOffset ExpiredAfter,
        DateOnly? ExpirationRenewedOn);

    internal sealed record SqlImportChannel(
        string Id,
        string Url,
        string Title,
        string Thumbnail,
        string PlaylistId,
        DateTimeOffset StaleAfter,
        ChannelStatus Status,
        ChannelStatusReason StatusReason,
        DateTimeOffset? StatusUpdatedAt);

    internal static class SqlToCosmosImportMapper
    {
        public static CosmosListDocument ToDocument(
            SqlImportList row,
            IEnumerable<string> channelIds,
            DateTimeOffset importedAt)
        {
            ArgumentNullException.ThrowIfNull(row);
            return CosmosDocumentMapper.ToDocument(
                new SubscriptionList
                {
                    Id = row.Id,
                    Token = row.Token.ToArray(),
                    Title = row.Title,
                    PlaybackRate = row.PlaybackRate,
                    ExpiredAfter = row.ExpiredAfter,
                    ExpirationRenewedOn = row.ExpirationRenewedOn
                },
                channelIds,
                importedAt);
        }

        public static CosmosChannelDocument ToDocument(
            SqlImportChannel row,
            IEnumerable<ChannelVideo> videos)
        {
            ArgumentNullException.ThrowIfNull(row);
            return CosmosDocumentMapper.ToDocument(new Channel
            {
                Id = row.Id,
                Url = row.Url,
                Title = row.Title,
                Thumbnail = row.Thumbnail,
                PlaylistId = row.PlaylistId,
                StaleAfter = row.StaleAfter,
                Status = row.Status,
                StatusReason = row.StatusReason,
                StatusUpdatedAt = row.StatusUpdatedAt,
                Videos = videos?.ToArray() ?? Array.Empty<ChannelVideo>()
            });
        }
    }

    internal interface ISqlToCosmosImportSource
    {
        IAsyncEnumerable<CosmosListDocument> ReadListsAsync(
            DateTimeOffset importedAt,
            int batchSize,
            CancellationToken cancellationToken);
        IAsyncEnumerable<CosmosChannelDocument> ReadChannelsAsync(
            DateTimeOffset importedAt,
            int batchSize,
            CancellationToken cancellationToken);
    }

    internal interface ISqlToCosmosImportTarget
    {
        IAsyncEnumerable<CosmosListDocument> ReadListsAsync(
            int batchSize,
            CancellationToken cancellationToken);
        IAsyncEnumerable<CosmosChannelDocument> ReadChannelsAsync(
            int batchSize,
            CancellationToken cancellationToken);
        Task<int> CountShareLinksAsync(CancellationToken cancellationToken);
        Task UpsertListAsync(CosmosListDocument document, CancellationToken cancellationToken);
        Task UpsertChannelAsync(CosmosChannelDocument document, CancellationToken cancellationToken);
    }

    internal sealed record SqlToCosmosImportOptions(
        SqlToCosmosImportMode Mode,
        int BatchSize,
        bool ConfirmEmptyTarget,
        bool ConfirmPreCutoverRerun);

    internal sealed record SqlToCosmosImportResult(
        int ListCount,
        int ChannelCount,
        string ReconciliationHash);

    internal sealed record SqlToCosmosTargetMetrics(
        int RequestCount,
        double RequestCharge,
        int SurfacedThrottleCount);

    internal sealed class SqlToCosmosImportService
    {
        private readonly ISqlToCosmosImportSource _source;
        private readonly ISqlToCosmosImportTarget _target;
        private readonly TextWriter _output;
        private readonly IAppClock _clock;

        public SqlToCosmosImportService(
            ISqlToCosmosImportSource source,
            ISqlToCosmosImportTarget target,
            TextWriter output,
            IAppClock clock)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _output = output ?? throw new ArgumentNullException(nameof(output));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public async Task<SqlToCosmosImportResult> RunAsync(
            SqlToCosmosImportOptions options,
            DateTimeOffset importedAt,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (options.BatchSize < 1 || options.BatchSize > 100)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "The SQL-to-Cosmos batch size must be from 1 through 100.");
            }
            cancellationToken.ThrowIfCancellationRequested();

            var expectedLists = await ReadByIdAsync(
                _source.ReadListsAsync(importedAt, options.BatchSize, cancellationToken),
                document => document.Id,
                NormalizeList,
                cancellationToken);
            var expectedChannels = await ReadByIdAsync(
                _source.ReadChannelsAsync(importedAt, options.BatchSize, cancellationToken),
                document => document.Id,
                NormalizeChannel,
                cancellationToken);

            ValidateDomainShapes(expectedLists.Values, expectedChannels.Values);
            var hash = CreateReconciliationHash(expectedLists.Values, expectedChannels.Values);

            if (options.Mode == SqlToCosmosImportMode.Import)
            {
                await ImportAsync(options, expectedLists, expectedChannels, cancellationToken);
            }
            else if (options.Mode == SqlToCosmosImportMode.Reconcile)
            {
                await ReconcileAsync(
                    expectedLists,
                    expectedChannels,
                    options.BatchSize,
                    cancellationToken);
            }

            await _output.WriteLineAsync(
                $"Mode={options.Mode.ToString().ToLowerInvariant()} Lists={expectedLists.Count.ToString(CultureInfo.InvariantCulture)} Channels={expectedChannels.Count.ToString(CultureInfo.InvariantCulture)} ReconciliationHash={hash}");
            return new SqlToCosmosImportResult(expectedLists.Count, expectedChannels.Count, hash);
        }

        private async Task ImportAsync(
            SqlToCosmosImportOptions options,
            IReadOnlyDictionary<string, CosmosListDocument> expectedLists,
            IReadOnlyDictionary<string, CosmosChannelDocument> expectedChannels,
            CancellationToken cancellationToken)
        {
            var actualLists = await ReadByIdAsync(
                _target.ReadListsAsync(options.BatchSize, cancellationToken),
                document => document.Id,
                NormalizeList,
                cancellationToken);
            var actualChannels = await ReadByIdAsync(
                _target.ReadChannelsAsync(options.BatchSize, cancellationToken),
                document => document.Id,
                NormalizeChannel,
                cancellationToken);
            var shareLinkCount = await _target.CountShareLinksAsync(cancellationToken);
            var empty = actualLists.Count == 0 && actualChannels.Count == 0 && shareLinkCount == 0;

            if (empty && !options.ConfirmEmptyTarget)
            {
                throw new SqlToCosmosImportOperationException(
                    SqlToCosmosImportError.FirstImportConfirmationRequired);
            }

            if (!empty)
            {
                if (!options.ConfirmPreCutoverRerun)
                {
                    throw new SqlToCosmosImportOperationException(
                        SqlToCosmosImportError.RerunConfirmationRequired);
                }
                if (shareLinkCount != 0)
                {
                    throw new SqlToCosmosImportOperationException(
                        SqlToCosmosImportError.TargetContainsShareLinks);
                }

                EnsureTargetIsImportedSubset(actualLists, expectedLists, ListDocumentsEqual);
                EnsureTargetIsImportedSubset(actualChannels, expectedChannels, ChannelDocumentsEqual);
            }

            foreach (var document in expectedChannels.Values.OrderBy(value => value.Id, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _target.UpsertChannelAsync(document, cancellationToken);
            }
            foreach (var document in expectedLists.Values.OrderBy(value => value.Id, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                document.Ttl = CosmosDocumentMapper.GetTtlSeconds(
                    document.ExpiredAfter,
                    _clock.UtcNow);
                await _target.UpsertListAsync(document, cancellationToken);
            }
        }

        private async Task ReconcileAsync(
            IReadOnlyDictionary<string, CosmosListDocument> expectedLists,
            IReadOnlyDictionary<string, CosmosChannelDocument> expectedChannels,
            int batchSize,
            CancellationToken cancellationToken)
        {
            var actualLists = await ReadByIdAsync(
                _target.ReadListsAsync(batchSize, cancellationToken),
                document => document.Id,
                NormalizeList,
                cancellationToken);
            var actualChannels = await ReadByIdAsync(
                _target.ReadChannelsAsync(batchSize, cancellationToken),
                document => document.Id,
                NormalizeChannel,
                cancellationToken);
            var shareLinkCount = await _target.CountShareLinksAsync(cancellationToken);

            if (shareLinkCount != 0)
            {
                throw new SqlToCosmosImportOperationException(
                    SqlToCosmosImportError.ReconciliationContainsShareLinks);
            }
            if (!DictionariesEqual(expectedLists, actualLists, ListDocumentsEqual))
            {
                throw new SqlToCosmosImportOperationException(
                    SqlToCosmosImportError.ReconciliationListMismatch);
            }
            if (!DictionariesEqual(expectedChannels, actualChannels, ChannelDocumentsEqual))
            {
                throw new SqlToCosmosImportOperationException(
                    SqlToCosmosImportError.ReconciliationChannelMismatch);
            }

            ValidateDomainShapes(actualLists.Values, actualChannels.Values);
        }

        private static async Task<Dictionary<string, T>> ReadByIdAsync<T>(
            IAsyncEnumerable<T> values,
            Func<T, string> getId,
            Action<T> normalize,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, T>(StringComparer.Ordinal);
            await foreach (var value in values.WithCancellation(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                normalize(value);
                CosmosSystemTextJsonSerializer.Instance.GetSerializedUtf8Size(value);
                if (!result.TryAdd(getId(value), value))
                {
                    throw new SqlToCosmosImportOperationException(
                        SqlToCosmosImportError.DuplicateDeterministicId);
                }
            }
            return result;
        }

        private static void EnsureTargetIsImportedSubset<T>(
            IReadOnlyDictionary<string, T> actual,
            IReadOnlyDictionary<string, T> expected,
            Func<T, T, bool> equals)
        {
            foreach (var (id, document) in actual)
            {
                if (!expected.TryGetValue(id, out var expectedDocument)
                    || !equals(expectedDocument, document))
                {
                    throw new SqlToCosmosImportOperationException(
                        SqlToCosmosImportError.TargetDoesNotMatchInterruptedImport);
                }
            }
        }

        private static bool DictionariesEqual<T>(
            IReadOnlyDictionary<string, T> expected,
            IReadOnlyDictionary<string, T> actual,
            Func<T, T, bool> equals)
        {
            return expected.Count == actual.Count
                && expected.All(pair => actual.TryGetValue(pair.Key, out var value)
                    && equals(pair.Value, value));
        }

        private static bool ListDocumentsEqual(
            CosmosListDocument left,
            CosmosListDocument right)
        {
            return string.Equals(left.Id, right.Id, StringComparison.Ordinal)
                && left.Token != null
                && right.Token != null
                && left.Token.SequenceEqual(right.Token)
                && string.Equals(left.Title, right.Title, StringComparison.Ordinal)
                && left.PlaybackRate == right.PlaybackRate
                && left.ExpiredAfter == right.ExpiredAfter
                && left.ExpirationRenewedOn == right.ExpirationRenewedOn
                && left.ChannelIds != null
                && right.ChannelIds != null
                && left.ChannelIds.SequenceEqual(right.ChannelIds, StringComparer.Ordinal);
        }

        private static bool ChannelDocumentsEqual(
            CosmosChannelDocument left,
            CosmosChannelDocument right)
        {
            return string.Equals(left.Id, right.Id, StringComparison.Ordinal)
                && string.Equals(left.Url, right.Url, StringComparison.Ordinal)
                && string.Equals(left.Title, right.Title, StringComparison.Ordinal)
                && string.Equals(left.Thumbnail, right.Thumbnail, StringComparison.Ordinal)
                && string.Equals(left.PlaylistId, right.PlaylistId, StringComparison.Ordinal)
                && left.StaleAfter == right.StaleAfter
                && string.Equals(left.Status, right.Status, StringComparison.Ordinal)
                && string.Equals(left.StatusReason, right.StatusReason, StringComparison.Ordinal)
                && left.StatusUpdatedAt == right.StatusUpdatedAt
                && left.Videos.SequenceEqual(right.Videos, CosmosVideoDocumentComparer.Instance);
        }

        private static void NormalizeList(CosmosListDocument document)
        {
            document.ETag = null;
            document.Ttl = 1;
            document.ChannelIds = (document.ChannelIds ?? Array.Empty<string>())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
        }

        private static void NormalizeChannel(CosmosChannelDocument document)
        {
            document.ETag = null;
            document.Videos ??= Array.Empty<CosmosVideoDocument>();
        }

        private static void ValidateDomainShapes(
            IEnumerable<CosmosListDocument> lists,
            IEnumerable<CosmosChannelDocument> channels)
        {
            foreach (var list in lists)
            {
                CosmosDocumentMapper.ToSubscriptionList(list);
                CosmosDocumentMapper.ToChannelIds(list);
            }
            foreach (var channel in channels)
            {
                CosmosDocumentMapper.ToChannel(channel);
            }
        }

        private static string CreateReconciliationHash(
            IEnumerable<CosmosListDocument> lists,
            IEnumerable<CosmosChannelDocument> channels)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var document in lists.OrderBy(value => value.Id, StringComparer.Ordinal))
            {
                AppendHash(hash, "list", document);
            }
            foreach (var document in channels.OrderBy(value => value.Id, StringComparer.Ordinal))
            {
                AppendHash(hash, "channel", document);
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        private static void AppendHash<T>(IncrementalHash hash, string kind, T document)
        {
            var value = kind + "\n" + CosmosSystemTextJsonSerializer.Instance.SerializeToString(document) + "\n";
            hash.AppendData(Encoding.UTF8.GetBytes(value));
        }

        private sealed class CosmosVideoDocumentComparer : IEqualityComparer<CosmosVideoDocument>
        {
            public static readonly CosmosVideoDocumentComparer Instance = new();

            public bool Equals(CosmosVideoDocument left, CosmosVideoDocument right)
            {
                if (ReferenceEquals(left, right))
                {
                    return true;
                }
                if (left is null || right is null)
                {
                    return false;
                }
                return string.Equals(left.Id, right.Id, StringComparison.Ordinal)
                    && string.Equals(left.Title, right.Title, StringComparison.Ordinal)
                    && left.DurationTicks == right.DurationTicks
                    && left.PublishedAt == right.PublishedAt
                    && string.Equals(left.Thumbnail, right.Thumbnail, StringComparison.Ordinal);
            }

            public int GetHashCode(CosmosVideoDocument value)
            {
                return value?.Id == null ? 0 : StringComparer.Ordinal.GetHashCode(value.Id);
            }
        }
    }

    internal sealed class SqlImportSource : ISqlToCosmosImportSource
    {
        private readonly string _connectionString;

        public SqlImportSource(string connectionString)
        {
            _connectionString = connectionString;
        }

        public async IAsyncEnumerable<CosmosListDocument> ReadListsAsync(
            DateTimeOffset importedAt,
            int batchSize,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            Guid? afterId = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rows = (await connection.QueryAsync<SqlImportList>(new CommandDefinition(
                    @"SELECT TOP (@batchSize) Id, Token, Title, PlaybackRate, ExpiredAfter, ExpirationRenewedOn
                      FROM [List]
                      WHERE ExpiredAfter > @importedAt
                        AND (@afterId IS NULL OR Id > @afterId)
                      ORDER BY Id;",
                    new { batchSize, importedAt, afterId },
                    cancellationToken: cancellationToken))).AsList();
                if (rows.Count == 0)
                {
                    yield break;
                }

                var ids = rows.Select(row => row.Id).ToArray();
                var memberships = (await connection.QueryAsync<(Guid ListId, string ChannelId)>(new CommandDefinition(
                    @"SELECT ListId, ChannelId
                      FROM ListChannel
                      WHERE ListId IN @ids
                      ORDER BY ListId, ChannelId;",
                    new { ids },
                    cancellationToken: cancellationToken)))
                    .ToLookup(row => row.ListId, row => row.ChannelId);
                foreach (var row in rows)
                {
                    yield return SqlToCosmosImportMapper.ToDocument(row, memberships[row.Id], importedAt);
                }

                afterId = rows[^1].Id;
            }
        }

        public async IAsyncEnumerable<CosmosChannelDocument> ReadChannelsAsync(
            DateTimeOffset importedAt,
            int batchSize,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            string afterId = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rows = (await connection.QueryAsync<SqlImportChannel>(new CommandDefinition(
                    @"SELECT TOP (@batchSize)
                          c.Id, c.Url, c.Title, c.Thumbnail, c.PlaylistId, c.StaleAfter,
                          c.Status, c.StatusReason, c.StatusUpdatedAt
                      FROM Channel c
                      WHERE (@afterId IS NULL OR c.Id > @afterId)
                        AND EXISTS (
                            SELECT 1
                            FROM ListChannel lc
                            INNER JOIN [List] l ON l.Id = lc.ListId
                            WHERE lc.ChannelId = c.Id AND l.ExpiredAfter > @importedAt)
                      ORDER BY c.Id;",
                    new { batchSize, importedAt, afterId },
                    cancellationToken: cancellationToken))).AsList();
                if (rows.Count == 0)
                {
                    yield break;
                }

                var ids = rows.Select(row => row.Id).ToArray();
                var videos = (await connection.QueryAsync<ChannelVideo>(new CommandDefinition(
                    @"WITH ranked AS (
                          SELECT ChannelId, Id AS VideoId, Title, Duration, PublishedAt,
                                 Thumbnail AS ThumbnailUrl,
                                 ROW_NUMBER() OVER (
                                     PARTITION BY ChannelId
                                     ORDER BY PublishedAt DESC, Id ASC) AS rowNumber
                          FROM ChannelVideo
                          WHERE ChannelId IN @ids
                      )
                      SELECT VideoId, ChannelId, Title, Duration, PublishedAt, ThumbnailUrl
                      FROM ranked
                      WHERE rowNumber <= @maximumVideos
                      ORDER BY ChannelId, PublishedAt DESC, VideoId ASC;",
                    new { ids, maximumVideos = CosmosDocumentMapper.MaximumVideos },
                    cancellationToken: cancellationToken)))
                    .ToLookup(video => video.ChannelId, StringComparer.Ordinal);
                foreach (var row in rows)
                {
                    yield return SqlToCosmosImportMapper.ToDocument(row, videos[row.Id]);
                }

                afterId = rows[^1].Id;
            }
        }
    }

    internal sealed class CosmosImportTarget : ISqlToCosmosImportTarget
    {
        private readonly CosmosPersistenceContext _context;
        private readonly object _metricsSync = new();
        private int _requestCount;
        private double _requestCharge;
        private int _surfacedThrottleCount;

        public CosmosImportTarget(CosmosPersistenceContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public SqlToCosmosTargetMetrics Metrics
        {
            get
            {
                lock (_metricsSync)
                {
                    return new(
                        _requestCount,
                        _requestCharge,
                        _surfacedThrottleCount);
                }
            }
        }

        public IAsyncEnumerable<CosmosListDocument> ReadListsAsync(
            int batchSize,
            CancellationToken cancellationToken) => ReadAsync<CosmosListDocument>(
                _context.Lists,
                batchSize,
                cancellationToken);

        public IAsyncEnumerable<CosmosChannelDocument> ReadChannelsAsync(
            int batchSize,
            CancellationToken cancellationToken) => ReadAsync<CosmosChannelDocument>(
                _context.Channels,
                batchSize,
                cancellationToken);

        public async Task<int> CountShareLinksAsync(CancellationToken cancellationToken)
        {
            using var iterator = _context.ShareLinks.GetItemQueryIterator<int>(
                new QueryDefinition("SELECT VALUE COUNT(1) FROM c"));
            var count = 0;
            while (iterator.HasMoreResults)
            {
                try
                {
                    var response = await iterator.ReadNextAsync(cancellationToken);
                    Record(response.RequestCharge);
                    count += response.Single();
                }
                catch (CosmosException exception) when (RecordSurfacedThrottle(exception))
                {
                    throw;
                }
            }
            return count;
        }

        public async Task UpsertListAsync(
            CosmosListDocument document,
            CancellationToken cancellationToken)
        {
            try
            {
                var response = await _context.Lists.UpsertItemAsync(
                    document,
                    new PartitionKey(document.Id),
                    cancellationToken: cancellationToken);
                Record(response.RequestCharge);
            }
            catch (CosmosException exception) when (RecordSurfacedThrottle(exception))
            {
                throw;
            }
        }

        public async Task UpsertChannelAsync(
            CosmosChannelDocument document,
            CancellationToken cancellationToken)
        {
            try
            {
                var response = await _context.Channels.UpsertItemAsync(
                    document,
                    new PartitionKey(document.Id),
                    cancellationToken: cancellationToken);
                Record(response.RequestCharge);
            }
            catch (CosmosException exception) when (RecordSurfacedThrottle(exception))
            {
                throw;
            }
        }

        private async IAsyncEnumerable<T> ReadAsync<T>(
            Container container,
            int batchSize,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var iterator = container.GetItemQueryIterator<T>(
                new QueryDefinition("SELECT * FROM c ORDER BY c.id"),
                requestOptions: new QueryRequestOptions { MaxItemCount = batchSize });
            while (iterator.HasMoreResults)
            {
                FeedResponse<T> response;
                try
                {
                    response = await iterator.ReadNextAsync(cancellationToken);
                    Record(response.RequestCharge);
                }
                catch (CosmosException exception) when (RecordSurfacedThrottle(exception))
                {
                    throw;
                }

                foreach (var document in response)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return document;
                }
            }
        }

        private void Record(double requestCharge)
        {
            lock (_metricsSync)
            {
                _requestCount++;
                _requestCharge += requestCharge;
            }
        }

        private bool RecordSurfacedThrottle(CosmosException exception)
        {
            if (exception.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                lock (_metricsSync)
                {
                    _surfacedThrottleCount++;
                }
            }

            return true;
        }
    }
}
