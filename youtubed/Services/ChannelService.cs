using Dapper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using youtubed.Data;
using youtubed.Models;

namespace youtubed.Services
{
    public class ChannelService : IChannelService
    {
        private readonly IConnectionFactory _connectionFactory;
        private readonly IYoutubeService _youtubeService;

        public ChannelService(
            IConnectionFactory connectionFactory,
            IYoutubeService youtubeService)
        {
            _connectionFactory = connectionFactory;
            _youtubeService = youtubeService;
        }

        public async Task<ChannelModel> GetOrCreateChannelAsync(string url)
        {
            using var connection = _connectionFactory.CreateConnection();
            var model = await connection.QueryFirstOrDefaultAsync<ChannelModel>(
                @"
                SELECT Id, Url, Title, Thumbnail, PlaylistId
                FROM Channel
                WHERE Url = @url;
                ",
                new { url });

            if (model != null)
            {
                return model;
            }

            var channel = await _youtubeService.GetChannelAsync(url);
            if (channel != null)
            {
                model = new ChannelModel
                {
                    Id = channel.Id,
                    Url = url,
                    Title = channel.Title,
                    Thumbnail = channel.Thumbnail,
                    PlaylistId = channel.PlaylistId
                };

                var manyYearsAgo = DateTimeOffset.MinValue;
                await connection.ExecuteAsync(
                    @"
                    MERGE INTO Channel target
                    USING (
                        SELECT @id as Url
                    ) source ON source.Url = target.Url
                    WHEN MATCHED THEN
                        UPDATE SET StaleAfter = @manyYearsAgo
                    WHEN NOT MATCHED THEN
                        INSERT (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                        VALUES (@id, @url, @title, @thumbnail, @playlistId, @manyYearsAgo, @manyYearsAgo);
                    ",
                    new
                    {
                        id = model.Id,
                        url = model.Url,
                        title = model.Title,
                        thumbnail = model.Thumbnail,
                        playlistId = model.PlaylistId,
                        manyYearsAgo
                    });
            }

            return model;
        }

        public async Task<StaleChannelModel> GetNextStaleChannelOrDefaultAsync()
        {
            using var connection = _connectionFactory.CreateConnection();
            var now = DateTimeOffset.Now;
            var visibilityTimeout = Constants.RandomlyBetween(
                Constants.VisibilityTimeoutMin,
                Constants.VisibilityTimeoutMax);
            var visibleAfter = now.Add(visibilityTimeout);
            return await connection.QueryFirstOrDefaultAsync<StaleChannelModel>(
                @"
                UPDATE target
                SET VisibleAfter = @visibleAfter
                OUTPUT inserted.Id, inserted.PlaylistId
                FROM (
                    SELECT TOP (1) *
                    FROM Channel
                    WHERE StaleAfter <= @now
                      AND VisibleAfter <= @now
                      AND EXISTS(SELECT * FROM ListChannel WHERE ListChannel.ChannelId = Channel.Id)
                    ORDER BY StaleAfter ASC,
                             VisibleAfter ASC
                ) target;
                ",
                new { now, visibleAfter });
        }

        public async Task<int> RemoveOrphanChannelsAsync()
        {
            using var connection = _connectionFactory.CreateConnection();
            var now = DateTimeOffset.Now;
            int count = await connection.ExecuteAsync(
                @"
                DELETE FROM Channel
                WHERE VisibleAfter <= @now
                    AND NOT EXISTS(SELECT * FROM ListChannel WHERE ListChannel.ChannelId = Channel.Id);
                ",
                new { now });
            return count;
        }
    }
}
