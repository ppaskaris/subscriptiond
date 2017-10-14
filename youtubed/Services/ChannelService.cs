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
            ChannelModel model;
            using (var connection = _connectionFactory.CreateConnection())
            {
                model = await connection.QueryFirstOrDefaultAsync<ChannelModel>(
                    @"
                    SELECT Id, Url, Title, Thumbnail
                    FROM Channel
                    WHERE Url = @url;
                    ",
                    new { url });
            }

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
                    Thumbnail = channel.Thumbnail
                };

                using (var connection = _connectionFactory.CreateConnection())
                {
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
                            INSERT (Id, Url, Title, Thumbnail, StaleAfter, VisibleAfter)
                            VALUES (@id, @url, @title, @thumbnail, @manyYearsAgo, @manyYearsAgo);
                        ",
                        new
                        {
                            id = model.Id,
                            url = model.Url,
                            title = model.Title,
                            thumbnail = model.Thumbnail,
                            manyYearsAgo
                        });
                }
            }

            return model;
        }

        public async Task<string> GetNextStaleIdOrDefaultAsync()
        {
            string id;
            using (var connection = _connectionFactory.CreateConnection())
            {
                var now = DateTimeOffset.Now;
                var visibilityTimeout = Constants.RandomlyBetween(
                    Constants.VisibilityTimeoutMin,
                    Constants.VisibilityTimeoutMax);
                var visibleAfter = now.Add(visibilityTimeout);
                id = await connection.ExecuteScalarAsync<string>(
                    @"
                    UPDATE target
                    SET VisibleAfter = @visibleAfter
                    OUTPUT inserted.Id
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
            return id;
        }

        public async Task<int> RemoveOrphanChannelsAsync()
        {
            int count;
            using (var connection = _connectionFactory.CreateConnection())
            {
                var now = DateTimeOffset.Now;
                count = await connection.ExecuteAsync(
                    @"
                    DELETE FROM Channel
                    WHERE VisibleAfter <= @now
                      AND NOT EXISTS(SELECT * FROM ListChannel WHERE ListChannel.ChannelId = Channel.Id);
                    ",
                    new { now });
            }
            return count;
        }
    }
}
