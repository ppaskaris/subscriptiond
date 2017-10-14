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
                Token = CreateToken(),
                ExpiredAfter = CreateExpiredAfter()
            };
            using (var connection = _connectionFactory.CreateConnection())
            {
                await connection.ExecuteAsync(
                    @"
                    INSERT INTO List (Id, Token, ExpiredAfter)
                    VALUES (@Id, @Token, @ExpiredAfter);
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
                    SELECT Id, Token, ExpiredAfter
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
            var expiredAfter = CreateExpiredAfter();
            var now = DateTimeOffset.Now;
            using (var connection = _connectionFactory.CreateConnection())
            using (var query = await connection.QueryMultipleAsync(
                @"
                UPDATE List
                SET ExpiredAfter = @expiredAfter
                OUTPUT inserted.Id,
                       inserted.Token,
                       inserted.ExpiredAfter
                WHERE Id = @id;

                SELECT COUNT(*)
                FROM ListChannel
	                INNER JOIN Channel ON Channel.Id = ListChannel.ChannelId
                WHERE ListChannel.ListId = @id
                  AND Channel.StaleAfter <= @now;

                SELECT Channel.Id AS ChannelId,
                       Channel.Title AS ChannelTitle,
                       Channel.Url AS ChannelUrl,
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
                new { id, expiredAfter, now }))
            {
                var list = await query.ReadSingleOrDefaultAsync<ListModel>();
                if (list == null)
                {
                    return null;
                }
                var staleCount = await query.ReadSingleOrDefaultAsync<int>();
                var videos = await query.ReadAsync<VideoViewModel>();
                listView = new ListViewModel
                {
                    Id = list.Id,
                    Token = list.TokenString,
                    ExpiredAfter = list.ExpiredAfter,
                    StaleCount = staleCount,
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

        public async Task<int> RemoveExpiredListsAsync()
        {
            int count;
            var now = DateTimeOffset.Now;
            using (var connection = _connectionFactory.CreateConnection())
            {
                count = await connection.ExecuteAsync(
                    @"
                    DELETE FROM List
                    WHERE ExpiredAfter <= @now
                    ",
                    new { now });
            }
            return count;
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

        private static DateTimeOffset CreateExpiredAfter()
        {
            var maxAge = Constants.RandomlyBetween(
                Constants.ListMaxAgeMin,
                Constants.ListMaxAgeMax);
            return DateTimeOffset.Now.Add(maxAge);
        }
    }
}
