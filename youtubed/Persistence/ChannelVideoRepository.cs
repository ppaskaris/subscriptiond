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

            if (videos.Any())
            {
                var videoTable = videos
                    .Select(CreateVideoDataRecord)
                    .AsTableValuedParameter("ChannelVideoType");

                await connection.ExecuteAsync(
                    @"
                    MERGE INTO ChannelVideo target
                    USING @videoTable source
                       ON source.Id = target.Id
                      AND source.ChannelId = target.ChannelId
                    WHEN MATCHED THEN
                        UPDATE SET Title = source.Title,
                                   Duration = source.Duration,
                                   PublishedAt = source.PublishedAt,
                                   Thumbnail = source.Thumbnail
                    WHEN NOT MATCHED BY TARGET THEN
                        INSERT (ChannelId, Id, Title, Duration, PublishedAt, Thumbnail)
                        VALUES (
                            source.ChannelId,
                            source.Id,
                            source.Title,
                            source.Duration,
                            source.PublishedAt,
                            source.Thumbnail
                        )
                    WHEN NOT MATCHED BY SOURCE
                         AND target.ChannelId = @channelId
                         AND target.PublishedAt < @earliestPublishedAt THEN
                            DELETE;

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
                    });
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
                    });
            }
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
