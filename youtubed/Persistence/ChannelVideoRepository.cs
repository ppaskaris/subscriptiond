using Dapper;
using Microsoft.SqlServer.Server;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using youtubed.Data;

namespace youtubed.Persistence
{
    public class ChannelVideoRepository : IChannelVideoRepository
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

        public ChannelVideoRepository(IConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task RefreshAsync(
            string channelId,
            DateTimeOffset earliestPublishedAt,
            IReadOnlyCollection<ChannelVideoRecord> videos,
            DateTimeOffset staleAfter)
        {
            using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();

            if (videos.Any())
            {
                var videoTable = videos
                    .Select(CreateVideoDataRecord)
                    .AsTableValuedParameter("ChannelVideoType");

                await connection.ExecuteAsync(
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

                    UPDATE Channel
                    SET StaleAfter = @staleAfter
                    WHERE Id = @channelId;
                    ",
                    new
                    {
                        channelId,
                        earliestPublishedAt,
                        videoTable,
                        staleAfter
                    },
                    transaction);
            }
            else
            {
                await connection.ExecuteAsync(
                    @"
                    DELETE FROM ChannelVideo
                    WHERE ChannelId = @channelId
                      AND PublishedAt < @earliestPublishedAt;

                    UPDATE Channel
                    SET StaleAfter = @staleAfter
                    WHERE Id = @channelId;
                    ",
                    new
                    {
                        channelId,
                        earliestPublishedAt,
                        staleAfter
                    },
                    transaction);
            }

            transaction.Commit();
        }

        private static SqlDataRecord CreateVideoDataRecord(ChannelVideoRecord video)
        {
            var dataRecord = new SqlDataRecord(VideoMetaData);
            var index = 0;
            dataRecord.SetString(index++, video.ChannelId);
            dataRecord.SetString(index++, video.Id);
            dataRecord.SetString(index++, video.Title);
            dataRecord.SetInt64(index++, video.Duration.Ticks);
            dataRecord.SetDateTimeOffset(index++, video.PublishedAt);
            dataRecord.SetString(index++, video.Thumbnail);
            return dataRecord;
        }
    }
}
