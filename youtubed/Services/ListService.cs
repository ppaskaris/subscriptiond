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

        public async Task<ListModel> CreateListAsync(string title)
        {
            using var connection = _connectionFactory.CreateConnection();
            var list = new ListModel
            {
                Id = Guid.NewGuid(),
                Token = CreateToken(),
                Title = title,
                ExpiredAfter = CreateExpiredAfter()
            };

            await connection.ExecuteAsync(
                @"
                INSERT INTO List (Id, Token, Title, ExpiredAfter)
                VALUES (@Id, @Token, @Title, @ExpiredAfter);
                ",
                list);
            return list;
        }

        public async Task<ListModel> GetListAsync(Guid id)
        {
            using var connection = _connectionFactory.CreateConnection();
            return await connection.QueryFirstOrDefaultAsync<ListModel>(
                @"
                SELECT Id, Token, Title, ExpiredAfter
                FROM list
                WHERE Id = @id;
                ",
                new { id });
        }

        public async Task<ListViewModel> GetListViewAsync(Guid id)
        {
            using var connection = _connectionFactory.CreateConnection();
            var expiredAfter = CreateExpiredAfter();
            var now = DateTimeOffset.Now;
            using var query = await connection.QueryMultipleAsync(
                @"
                UPDATE List
                SET ExpiredAfter = @expiredAfter
                OUTPUT inserted.Id,
                       inserted.Token,
                       inserted.Title,
                       inserted.ExpiredAfter
                WHERE Id = @id;

                SELECT COUNT(*)
                FROM ListChannel
	                INNER JOIN Channel ON Channel.Id = ListChannel.ChannelId
                WHERE ListChannel.ListId = @id
                  AND Channel.StaleAfter <= @now;

                SELECT Channel.Title AS ChannelTitle,
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

                SELECT Channel.Id,
                       Channel.Title,
                       Channel.Url,
                       Channel.Thumbnail
                FROM ListChannel
                    INNER JOIN Channel ON Channel.Id = ListChannel.ChannelId
                WHERE ListChannel.ListId = @id
                ORDER BY Channel.Title ASC;
                ",
                new { id, expiredAfter, now });
            var list = await query.ReadSingleOrDefaultAsync<ListModel>();
            if (list == null)
            {
                return null;
            }
            var staleCount = await query.ReadSingleOrDefaultAsync<int>();
            var videos = await query.ReadAsync<VideoViewModel>();
            var channels = await query.ReadAsync<ChannelModel>();
            return new ListViewModel
            {
                Id = list.Id,
                Token = list.TokenString,
                Title = list.Title,
                ExpiredAfter = list.ExpiredAfter,
                StaleCount = staleCount,
                Videos = videos,
                Channels = channels
            };
        }

        public async Task AddChannelAsync(Guid listId, string channelId)
        {
            using var connection = _connectionFactory.CreateConnection();
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

        public async Task RemoveChannelAsync(Guid listId, string channelId)
        {
            using var connection = _connectionFactory.CreateConnection();
            await connection.ExecuteAsync(
                @"
                DELETE FROM ListChannel
                WHERE ListId = @listId
                  AND ChannelId = @channelId;
                ",
                new { listId, channelId });
        }

        public async Task RenameListAsync(Guid id, string title)
        {
            using var connection = _connectionFactory.CreateConnection();
            await connection.ExecuteAsync(
                @"
                UPDATE List
                SET Title = @title
                WHERE Id = @id;
                ",
                new { id, title });
        }

        public async Task DeleteListAsync(Guid id)
        {
            using var connection = _connectionFactory.CreateConnection();
            await connection.ExecuteAsync(
                @"
                DELETE FROM List
                WHERE Id = @id;
                ",
                new { id });
        }

        public async Task<int> RemoveExpiredListsAsync()
        {
            using var connection = _connectionFactory.CreateConnection();
            var now = DateTimeOffset.Now;
            int count = await connection.ExecuteAsync(
                @"
                DELETE FROM List
                WHERE ExpiredAfter <= @now
                ",
                new { now });
            return count;
        }

        private byte[] CreateToken()
        {
            using var rng = new RNGCryptoServiceProvider();
            byte[] token = new byte[40];
            rng.GetNonZeroBytes(token);
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
