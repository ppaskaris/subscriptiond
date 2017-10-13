using Dapper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using youtubed.Data;
using youtubed.Models;

namespace youtubed.Services
{
    public class ListService : IListService
    {
        private readonly IConnectionFactory _connectionFactory;

        public ListService(IConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task<ListModel> CreateListAsync()
        {
            var list = new ListModel
            {
                Id = Guid.NewGuid(),
                Token = CreateToken()
            };
            using (var connection = _connectionFactory.CreateConnection())
            {
                await connection.ExecuteAsync(
                    @"
                    INSERT INTO List (Id, Token)
                    VALUES (@Id, @Token);
                    ",
                    list);
            }
            return list;
        }

        public async Task<ListModel> GetListAsync(Guid id)
        {
            ListModel list;
            using (var connection = _connectionFactory.CreateConnection())
            {
                list = await connection.QueryFirstOrDefaultAsync<ListModel>(
                    @"
                    SELECT Id, Token
                    FROM list
                    WHERE Id = @id;
                    ",
                    new { id });
            }
            return list;
        }

        public async Task<ListViewModel> GetListViewAsync(Guid id)
        {
            ListViewModel listView;
            using (var connection = _connectionFactory.CreateConnection())
            using (var query = await connection.QueryMultipleAsync(
                @"
                SELECT Id, Token FROM List WHERE Id = @id;

                SELECT Channel.Id AS ChannelId,
                       Channel.Title AS ChannelTitle,
                       ChannelVideo.Id AS VideoId,
                       ChannelVideo.Title AS VideoTitle,
                       ChannelVideo.Duration AS VideoDuration,
                       ChannelVideo.PublishedAt AS VideoPublishedAt,
                       ChannelVideo.Thumbnail AS VideoThumbnail
                FROM ListChannel
                    INNER JOIN Channel ON Channel.Id = ListChannel.ChannelId
                    INNER JOIN ChannelVideo ON ChannelVideo.ChannelId = Channel.Id
                WHERE ListChannel.ListId = @id
                ORDER BY ChannelVideo.PublishedAt DESC,
                         ChannelVideo.Id ASC;
                ",
                new { id }))
            {
                var list = await query.ReadSingleAsync<ListModel>();
                var videos = await query.ReadAsync<VideoViewModel>();
                listView = new ListViewModel
                {
                    Id = list.Id,
                    Token = list.TokenString,
                    Videos = videos
                };
            }
            return listView;
        }

        public async Task AddChannelAsync(Guid listId, string channelId)
        {
            using (var connection = _connectionFactory.CreateConnection())
            {
                await connection.ExecuteAsync(
                    @"
                    MERGE INTO ListChannel target
                    USING (
                        SELECT @listId AS ListId,
                               @channelId AS ChannelId
                    ) source ON source.ListId = target.ListId
                            AND source.ChannelId = target.ChannelId
                    WHEN NOT MATCHED THEN
                        INSERT (ListId, ChannelId)
                        VALUES (@listId, @channelId);
                    ",
                    new { listId, channelId });
            }
        }

        private byte[] CreateToken()
        {
            byte[] token = new byte[40];
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetNonZeroBytes(token);
            }
            return token;
        }
    }
}
