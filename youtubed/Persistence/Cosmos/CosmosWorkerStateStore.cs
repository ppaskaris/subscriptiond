using Microsoft.Azure.Cosmos;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using youtubed.Domain;
using youtubed.Services;

namespace youtubed.Persistence.Cosmos
{
    public sealed class CosmosWorkerStateStore : IWorkerStateStore
    {
        private const int MaxWriteAttempts = 2;

        private readonly Container _system;
        private readonly IAppClock _clock;

        public CosmosWorkerStateStore(Container system, IAppClock clock)
        {
            _system = system;
            _clock = clock;
        }

        public async Task<WorkerState> GetOrCreateAsync(CancellationToken cancellationToken)
        {
            var document = await GetOrCreateDocumentAsync(cancellationToken);
            return CosmosDocumentMapper.ToWorkerState(document);
        }

        public Task ForceChannelRefreshAsync(CancellationToken cancellationToken)
        {
            return UpdateAsync(
                document =>
                {
                    document.NextChannelRefreshAt = DateTimeOffset.MinValue;
                    document.ChannelRefreshForceCount++;
                    return true;
                },
                cancellationToken);
        }

        public Task ForceConsistencyRecoveryAsync(CancellationToken cancellationToken)
        {
            return UpdateAsync(
                document =>
                {
                    document.NextConsistencyRecoveryAt = DateTimeOffset.MinValue;
                    document.ConsistencyRecoveryForceCount++;
                    return true;
                },
                cancellationToken);
        }

        public Task CompleteChannelRefreshPassAsync(
            DateTimeOffset? observedNextChannelRefreshAt,
            long observedChannelRefreshForceCount,
            DateTimeOffset? nextChannelRefreshAt,
            CancellationToken cancellationToken)
        {
            return UpdateAsync(
                document =>
                {
                    if (document.NextChannelRefreshAt != observedNextChannelRefreshAt
                        || document.ChannelRefreshForceCount != observedChannelRefreshForceCount)
                    {
                        return false;
                    }

                    document.NextChannelRefreshAt = nextChannelRefreshAt;
                    return true;
                },
                cancellationToken);
        }

        public Task CompletePurgeAsync(
            DateTimeOffset nextPurgeAt,
            CancellationToken cancellationToken)
        {
            return UpdateAsync(
                document =>
                {
                    document.NextPurgeAt = nextPurgeAt;
                    return true;
                },
                cancellationToken);
        }

        public Task CompleteConsistencyRecoveryPassAsync(
            DateTimeOffset observedNextConsistencyRecoveryAt,
            long observedConsistencyRecoveryForceCount,
            DateTimeOffset nextConsistencyRecoveryAt,
            CancellationToken cancellationToken)
        {
            return UpdateAsync(
                document =>
                {
                    if (document.NextConsistencyRecoveryAt != observedNextConsistencyRecoveryAt
                        || document.ConsistencyRecoveryForceCount != observedConsistencyRecoveryForceCount)
                    {
                        return false;
                    }

                    document.NextConsistencyRecoveryAt = nextConsistencyRecoveryAt;
                    return true;
                },
                cancellationToken);
        }

        private static PartitionKey SchedulerPartitionKey =>
            new PartitionKey(CosmosWorkerStateDocument.SchedulerId);

        private async Task UpdateAsync(
            Func<CosmosWorkerStateDocument, bool> update,
            CancellationToken cancellationToken)
        {
            var document = await GetOrCreateDocumentAsync(cancellationToken);

            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                if (!update(document))
                {
                    return;
                }

                try
                {
                    await _system.ReplaceItemAsync(
                        document,
                        document.Id,
                        SchedulerPartitionKey,
                        new ItemRequestOptions { IfMatchEtag = document.ETag },
                        cancellationToken);
                    return;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                    document = await ReadAsync(cancellationToken);
                }
            }
        }

        private async Task<CosmosWorkerStateDocument> GetOrCreateDocumentAsync(
            CancellationToken cancellationToken)
        {
            var document = await ReadAsync(cancellationToken);
            if (document != null)
            {
                return document;
            }

            var now = _clock.UtcNow;
            document = new CosmosWorkerStateDocument
            {
                NextChannelRefreshAt = now,
                ChannelRefreshForceCount = 0,
                NextPurgeAt = now,
                NextConsistencyRecoveryAt = now,
                ConsistencyRecoveryForceCount = 0
            };

            try
            {
                var response = await _system.CreateItemAsync(
                    document,
                    SchedulerPartitionKey,
                    cancellationToken: cancellationToken);
                return response.Resource;
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
            {
                return await ReadAsync(cancellationToken);
            }
        }

        private async Task<CosmosWorkerStateDocument> ReadAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                var response = await _system.ReadItemAsync<CosmosWorkerStateDocument>(
                    CosmosWorkerStateDocument.SchedulerId,
                    SchedulerPartitionKey,
                    cancellationToken: cancellationToken);
                return response.Resource;
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }
    }
}
