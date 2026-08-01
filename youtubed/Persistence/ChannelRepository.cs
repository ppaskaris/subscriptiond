using Dapper;
using Microsoft.Data.SqlClient.Server;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using youtubed.Data;
using youtubed.Domain;

namespace youtubed.Persistence
{
    public class ChannelRepository : IChannelRepository
    {
        private static readonly SqlMetaData[] VideoMetaData = new[]
        {
            new SqlMetaData("ChannelId", SqlDbType.NVarChar, 50),
            new SqlMetaData("Id", SqlDbType.NVarChar, 50),
            new SqlMetaData("Title", SqlDbType.NVarChar, 100),
            new SqlMetaData("Duration", SqlDbType.BigInt),
            new SqlMetaData("PublishedAt", SqlDbType.DateTimeOffset),
            new SqlMetaData("Thumbnail", SqlDbType.NVarChar, 2000),
        };

        private readonly IConnectionFactory _connectionFactory;

        public ChannelRepository(IConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task<Channel> GetByIdAsync(string id)
        {
            using var connection = _connectionFactory.CreateConnection();
            return await connection.QueryFirstOrDefaultAsync<Channel>(
                @"
                SELECT Id, Url, Title, Thumbnail, PlaylistId, Status, StatusReason, StatusUpdatedAt
                FROM Channel
                WHERE Id = @id;
                ",
                new { id });
        }

        public async Task SaveDiscoveredChannelAsync(Channel channel, DateTimeOffset staleAfter)
        {
            using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);

            var parameters = new
            {
                id = channel.Id,
                url = channel.Url,
                title = channel.Title,
                thumbnail = channel.Thumbnail,
                playlistId = channel.PlaylistId,
                status = ChannelStatus.Active,
                statusReason = ChannelStatusReason.None,
                staleAfter
            };

            // Rediscovery should only make an existing channel eligible again.
            var updated = await connection.ExecuteAsync(
                @"
                UPDATE Channel WITH (UPDLOCK, HOLDLOCK)
                SET StaleAfter = @staleAfter,
                    Status = @status,
                    StatusReason = @statusReason,
                    StatusUpdatedAt = NULL
                WHERE Id = @id;
                ",
                parameters,
                transaction);

            if (updated == 0)
            {
                await connection.ExecuteAsync(
                    @"
                    INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, Status, StatusReason, StatusUpdatedAt)
                    SELECT @id, @url, @title, @thumbnail, @playlistId, @staleAfter, @status, @statusReason, NULL
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM Channel WITH (UPDLOCK, HOLDLOCK)
                        WHERE Id = @id
                    );
                    ",
                    parameters,
                    transaction);

                await connection.ExecuteAsync(
                    @"
                    UPDATE Channel
                    SET StaleAfter = @staleAfter
                    WHERE Id = @id;
                    ",
                    parameters,
                    transaction);
            }

            transaction.Commit();
        }

        public async Task<IReadOnlyList<StaleChannelReference>> GetStaleLookaheadAsync(
            DateTimeOffset now,
            int take,
            CancellationToken cancellationToken)
        {
            using var connection = _connectionFactory.CreateConnection();
            var channels = await connection.QueryAsync<StaleChannelReference>(
                new CommandDefinition(
                    @"
                    SELECT TOP (@take) Id, StaleAfter
                    FROM Channel
                    WHERE StaleAfter <= @now
                      AND Status = @status
                      AND EXISTS(SELECT * FROM ListChannel WHERE ListChannel.ChannelId = Channel.Id)
                    ORDER BY StaleAfter ASC,
                             Id ASC;
                    ",
                    new { now, take, status = ChannelStatus.Active },
                    cancellationToken: cancellationToken));

            return channels.AsList();
        }

        public async Task<DateTimeOffset?> GetNextActiveSubscribedRefreshAtAsync(
            CancellationToken cancellationToken)
        {
            using var connection = _connectionFactory.CreateConnection();
            return await connection.ExecuteScalarAsync<DateTimeOffset?>(
                new CommandDefinition(
                    @"
                    SELECT TOP (1) StaleAfter
                    FROM Channel
                    WHERE Status = @status
                      AND EXISTS(SELECT * FROM ListChannel WHERE ListChannel.ChannelId = Channel.Id)
                    ORDER BY StaleAfter ASC,
                             Id ASC;
                    ",
                    new { status = ChannelStatus.Active },
                    cancellationToken: cancellationToken));
        }

        public async Task<IReadOnlyList<Channel>> GetBatchAsync(
            IReadOnlyCollection<string> channelIds,
            CancellationToken cancellationToken)
        {
            if (channelIds.Count == 0)
            {
                return Array.Empty<Channel>();
            }

            using var connection = _connectionFactory.CreateConnection();
            using var query = await connection.QueryMultipleAsync(
                new CommandDefinition(
                    @"
                    SELECT Id,
                           Url,
                           Title,
                           Thumbnail,
                           PlaylistId,
                           StaleAfter,
                           Status,
                           StatusReason,
                           StatusUpdatedAt
                    FROM Channel
                    WHERE Id IN @channelIds;

                    SELECT ChannelId, ListId
                    FROM ListChannel
                    WHERE ChannelId IN @channelIds
                    ORDER BY ChannelId ASC,
                             ListId ASC;

                    SELECT ChannelId,
                           Id AS VideoId,
                           Title,
                           Duration,
                           PublishedAt,
                           Thumbnail AS ThumbnailUrl
                    FROM ChannelVideo
                    WHERE ChannelId IN @channelIds;
                    ",
                    new { channelIds },
                    cancellationToken: cancellationToken));

            var channelsById = (await query.ReadAsync<Channel>())
                .ToDictionary(channel => channel.Id, StringComparer.Ordinal);
            var subscriptions = (await query.ReadAsync<ChannelSubscriptionRow>())
                .ToLookup(row => row.ChannelId, row => row.ListId, StringComparer.Ordinal);
            var videos = (await query.ReadAsync<ChannelVideo>())
                .ToLookup(video => video.ChannelId, StringComparer.Ordinal);

            foreach (var channel in channelsById.Values)
            {
                channel.SubscribedListIds = subscriptions[channel.Id].ToList();
                channel.SubscriptionCount = channel.SubscribedListIds.Count;
                channel.Videos = videos[channel.Id].ToList();
            }

            return channelIds
                .Where(channelsById.ContainsKey)
                .Select(id => channelsById[id])
                .ToList();
        }

        public async Task SaveRefreshResultsAsync(
            IReadOnlyCollection<ChannelRefreshResult> results,
            CancellationToken cancellationToken)
        {
            if (results.Count == 0)
            {
                return;
            }

            using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();

            foreach (var result in results)
            {
                var channel = result.Channel;
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        @"
                        UPDATE Channel
                        SET Url = @url,
                            Title = @title,
                            Thumbnail = @thumbnail,
                            PlaylistId = @playlistId,
                            StaleAfter = @staleAfter,
                            Status = @status,
                            StatusReason = @statusReason,
                            StatusUpdatedAt = @statusUpdatedAt
                        WHERE Id = @id;
                        ",
                        new
                        {
                            id = channel.Id,
                            url = channel.Url,
                            title = channel.Title,
                            thumbnail = channel.Thumbnail,
                            playlistId = channel.PlaylistId,
                            staleAfter = channel.StaleAfter,
                            status = channel.Status,
                            statusReason = channel.StatusReason,
                            statusUpdatedAt = channel.StatusUpdatedAt
                        },
                        transaction,
                        cancellationToken: cancellationToken));

                if (!result.VideosRefreshed)
                {
                    continue;
                }

                if (channel.Videos.Any())
                {
                    var videoTable = channel.Videos
                        .Select(CreateVideoDataRecord)
                        .AsTableValuedParameter("ChannelVideoType");

                    await connection.ExecuteAsync(
                        new CommandDefinition(
                            @"
                            UPDATE target
                            SET Title = source.Title,
                                Duration = source.Duration,
                                PublishedAt = source.PublishedAt,
                                Thumbnail = source.Thumbnail
                            FROM ChannelVideo target
                            INNER JOIN @videoTable source
                                ON source.ChannelId = target.ChannelId
                               AND source.Id = target.Id;

                            INSERT INTO ChannelVideo (ChannelId, Id, Title, Duration, PublishedAt, Thumbnail)
                            SELECT source.ChannelId,
                                   source.Id,
                                   source.Title,
                                   source.Duration,
                                   source.PublishedAt,
                                   source.Thumbnail
                            FROM @videoTable source
                            WHERE NOT EXISTS (
                                SELECT 1
                                FROM ChannelVideo target WITH (UPDLOCK, HOLDLOCK)
                                WHERE target.ChannelId = source.ChannelId
                                  AND target.Id = source.Id
                            );

                            DELETE target
                            FROM ChannelVideo target
                            WHERE target.ChannelId = @channelId
                              AND target.PublishedAt < @earliestPublishedAt
                              AND NOT EXISTS (
                                  SELECT 1
                                  FROM @videoTable source
                                  WHERE source.ChannelId = target.ChannelId
                                    AND source.Id = target.Id
                              );
                            ",
                            new
                            {
                                channelId = channel.Id,
                                earliestPublishedAt = result.EarliestPublishedAt.GetValueOrDefault(DateTimeOffset.MinValue),
                                videoTable
                            },
                            transaction,
                            cancellationToken: cancellationToken));
                }
                else
                {
                    await connection.ExecuteAsync(
                        new CommandDefinition(
                            @"
                            DELETE FROM ChannelVideo
                            WHERE ChannelId = @channelId
                              AND PublishedAt < @earliestPublishedAt;
                            ",
                            new
                            {
                                channelId = channel.Id,
                                earliestPublishedAt = result.EarliestPublishedAt.GetValueOrDefault(DateTimeOffset.MinValue)
                            },
                            transaction,
                            cancellationToken: cancellationToken));
                }
            }

            transaction.Commit();
        }

        public async Task<int> RemoveOrphanChannelsAsync(DateTimeOffset now)
        {
            using var connection = _connectionFactory.CreateConnection();
            return await connection.ExecuteAsync(
                @"
                DELETE FROM Channel
                WHERE NOT EXISTS(SELECT * FROM ListChannel WHERE ListChannel.ChannelId = Channel.Id);
                ",
                new { now });
        }

        private static SqlDataRecord CreateVideoDataRecord(ChannelVideo video)
        {
            var dataRecord = new SqlDataRecord(VideoMetaData);
            var index = 0;
            dataRecord.SetString(index++, video.ChannelId);
            dataRecord.SetString(index++, video.VideoId);
            dataRecord.SetString(index++, video.Title);
            dataRecord.SetInt64(index++, video.Duration.Ticks);
            dataRecord.SetDateTimeOffset(index++, video.PublishedAt);
            if (video.ThumbnailUrl == null)
            {
                dataRecord.SetDBNull(index++);
            }
            else
            {
                dataRecord.SetString(index++, video.ThumbnailUrl);
            }

            return dataRecord;
        }

        private sealed class ChannelSubscriptionRow
        {
            public string ChannelId { get; set; }
            public Guid ListId { get; set; }
        }
    }
}
