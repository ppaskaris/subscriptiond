﻿using Dapper;
using Microsoft.SqlServer.Server;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using youtubed.Data;
using youtubed.Models;

namespace youtubed.Services
{
    public class ChannelVideoService : IChannelVideoService
    {
        private readonly IConnectionFactory _connectionFactory;
        private readonly IYoutubeService _youtubeService;

        public ChannelVideoService(
            IConnectionFactory connectionFactory,
            IYoutubeService youtubeService)
        {
            _connectionFactory = connectionFactory;
            _youtubeService = youtubeService;
        }

        public async Task RefreshVideosAsync(StaleChannelModel channel)
        {
            using var connection = _connectionFactory.CreateConnection();
            var earliestPublishedAt =
                DateTimeOffset.Now.Subtract(Constants.VideoMaxAge);
            var videos = await _youtubeService.GetVideosAsync(
                channel.PlaylistId,
                earliestPublishedAt);
            var videoRecords = videos
                .Where(video => video.ChannelId == channel.Id)
                .Select(CreateVideoDataRecord)
                .ToList();
            var updateMaxAge = Constants.RandomlyBetween(
                Constants.ChannelMaxAgeMin,
                Constants.ChannelMaxAgeMax);
            var later = DateTimeOffset.Now.Add(updateMaxAge);
            // Dapper doesn't support empty TVPs...
            if (videoRecords.Any())
            {
                var videoTable = videoRecords
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
                    SET StaleAfter = @later
                    WHERE Id = @channelId;
                    ",
                    new
                    {
                        channelId = channel.Id,
                        earliestPublishedAt,
                        videoTable,
                        later
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
                    SET StaleAfter = @later
                    WHERE Id = @channelId;
                    ",
                    new
                    {
                        channelId = channel.Id,
                        earliestPublishedAt,
                        later
                    });
            }
        }

        private static readonly SqlMetaData[] VideoMetaData = new[]
            {
                new SqlMetaData("ChannelId", SqlDbType.NVarChar, 50),
                new SqlMetaData("Id", SqlDbType.NVarChar, 50),
                new SqlMetaData("Title", SqlDbType.NVarChar, 100),
                new SqlMetaData("Duration", SqlDbType.BigInt),
                new SqlMetaData("PublishedAt", SqlDbType.DateTimeOffset),
                new SqlMetaData("Thumbnail", SqlDbType.NVarChar, 2000),
            };

        private static SqlDataRecord CreateVideoDataRecord(YoutubeVideo video)
        {
            int index = 0;
            var dataRecord = new SqlDataRecord(VideoMetaData);
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
