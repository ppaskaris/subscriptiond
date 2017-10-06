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

        public async Task<ChannelModel> GetOrCreateChannel(string url)
        {
            ChannelModel model;
            using (var connection = _connectionFactory.CreateConnection())
            {
                model = await connection.QueryFirstOrDefaultAsync<ChannelModel>(
                    @"
                    SELECT Id, Url, Title, Description, Thumbnail
                    FROM Channel WHERE Url = @url;
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
                    Description = channel.Description,
                    Thumbnail = channel.Thumbnail
                };
            }

            using (var connection = _connectionFactory.CreateConnection())
            {
                await connection.ExecuteAsync(
                    @"
                    MERGE INTO Channel source
                    USING (
                        SELECT @Id as Id,
                                @Url as Url,
                                @Title as Title,
                                @Description as Description,
                                @Thumbnail as Thumbnail
                    ) target ON target.Url = source.Url
                    WHEN NOT MATCHED THEN 
                        INSERT (Id, Url, Title, Description, Thumbnail)
                        VALUES (@Id, @Url, @Title, @Description, @Thumbnail);
                    ",
                    model);
            }

            return model;
        }
    }
}
