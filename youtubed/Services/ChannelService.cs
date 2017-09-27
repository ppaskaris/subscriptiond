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

        public async Task<ChannelModel> CreateChannelAsync(string url)
        {
            ChannelModel model;
            using (var connection = _connectionFactory.CreateConnection())
            {
                model = await connection.QueryFirstOrDefaultAsync<ChannelModel>(
                    @"SELECT Id, Title, Description, Thumbnail FROM Channel WHERE Id = @id",
                    new { id = url });
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
                    Title = channel.Title,
                    Description = channel.Description,
                    Thumbnail = channel.Thumbnail
                };
            }

            return model;
        }
    }
}
