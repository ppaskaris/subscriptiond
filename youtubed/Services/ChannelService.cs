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
            }

            using (var connection = _connectionFactory.CreateConnection())
            {
                var now = DateTimeOffset.Now;
                await connection.ExecuteAsync(
                    @"
                    MERGE INTO Channel target
                    USING (
                        SELECT @id as Id,
                               @url as Url,
                               @title as Title,
                               @thumbnail as Thumbnail,
                               @now as StaleAfter,
                               @now as VisibleAfter
                    ) source ON source.Url = target.Url
                    WHEN NOT MATCHED THEN
                        INSERT (Id, Url, Title, Thumbnail, StaleAfter, VisibleAfter)
                        VALUES (@id, @url, @title, @thumbnail, @now, @now);
                    ",
                    new
                    {
                        id = model.Id,
                        url = model.Url,
                        title = model.Title,
                        thumbnail = model.Thumbnail,
                        now
                    });
            }

            return model;
        }

        public async Task<string> GetNextStaleIdOrDefaultAsync()
        {
            string id;
            using (var connection = _connectionFactory.CreateConnection())
            {
                var now = DateTimeOffset.Now;
                var visibleAfter = now.Add(Constants.VisibilityTimeout);
                id = await connection.ExecuteScalarAsync<string>(
                    @"
                    UPDATE TOP (1) Channel
                    SET VisibleAfter = @visibleAfter
                    OUTPUT inserted.Id
                    WHERE StaleAfter <= @now
                      AND VisibleAfter <= @now;
                    ",
                    new { now, visibleAfter });
            }
            return id;
        }
    }
}
