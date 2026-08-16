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
            var videos = (await query.ReadAsync<ChannelVideo>())
                .ToLookup(video => video.ChannelId, StringComparer.Ordinal);

            foreach (var channel in channelsById.Values)
            {
                channel.Videos = videos[channel.Id].ToList();
            }

            return channelIds
                .Where(channelsById.ContainsKey)
                .Select(id => channelsById[id])
                .ToList();
        }

        public async Task SaveRefreshResultAsync(
            ChannelRefreshResult result,
            CancellationToken cancellationToken)
        {
            if (result?.Channel == null)
            {
                throw new ArgumentException("A refresh result must contain a channel.", nameof(result));
            }

            using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
            var channel = result.Channel;
            var parameters = new
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
            };
            var updated = await connection.ExecuteAsync(
                new CommandDefinition(
                    @"
                    UPDATE Channel WITH (UPDLOCK, HOLDLOCK)
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
                    parameters,
                    transaction,
                    cancellationToken: cancellationToken));

            if (updated == 0)
            {
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        @"
                        INSERT INTO Channel
                            (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, Status, StatusReason, StatusUpdatedAt)
                        SELECT @id, @url, @title, @thumbnail, @playlistId, @staleAfter, @status, @statusReason, @statusUpdatedAt
                        WHERE NOT EXISTS (
                            SELECT 1
                            FROM Channel WITH (UPDLOCK, HOLDLOCK)
                            WHERE Id = @id
                        );
                        ",
                        parameters,
                        transaction,
                        cancellationToken: cancellationToken));
            }

            if (result.VideosRefreshed)
            {

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
                            WHERE ChannelId = @channelId;
                            ",
                            new
                            {
                                channelId = channel.Id
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

    }
}
