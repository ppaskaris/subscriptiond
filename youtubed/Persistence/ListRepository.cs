using Dapper;
using System;
using System.Threading.Tasks;
using youtubed.Data;
using youtubed.Models;

namespace youtubed.Persistence
{
    public class ListRepository : IListRepository
    {
        private readonly IConnectionFactory _connectionFactory;

        public ListRepository(IConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task CreateAsync(ListModel list)
        {
            using var connection = _connectionFactory.CreateConnection();
            await connection.ExecuteAsync(
                @"
                INSERT INTO List (Id, Token, Title, PlaybackRate, ExpiredAfter)
                VALUES (@Id, @Token, @Title, @PlaybackRate, @ExpiredAfter);
                ",
                list);
        }

        public async Task<ListModel> GetAsync(Guid id)
        {
            using var connection = _connectionFactory.CreateConnection();
            return await connection.QueryFirstOrDefaultAsync<ListModel>(
                @"
                SELECT Id, Token, Title, PlaybackRate, ExpiredAfter
                FROM List
                WHERE Id = @id;
                ",
                new { id });
        }

        public async Task<ListViewModel> GetViewAsync(Guid id, DateTimeOffset expiredAfter, DateTimeOffset now)
        {
            using var connection = _connectionFactory.CreateConnection();
            using var query = await connection.QueryMultipleAsync(
                @"
                UPDATE List
                SET ExpiredAfter = @expiredAfter
                OUTPUT inserted.Id,
                       inserted.Token,
                       inserted.Title,
                       inserted.PlaybackRate,
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

            return new ListViewModel
            {
                Id = list.Id,
                Token = list.TokenString,
                Title = list.Title,
                PlaybackRate = list.PlaybackRate,
                ExpiredAfter = list.ExpiredAfter,
                StaleCount = await query.ReadSingleOrDefaultAsync<int>(),
                Videos = await query.ReadAsync<VideoViewModel>(),
                Channels = await query.ReadAsync<ChannelModel>()
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

        public async Task UpdateAsync(Guid id, string title, decimal playbackRate)
        {
            using var connection = _connectionFactory.CreateConnection();
            await connection.ExecuteAsync(
                @"
                UPDATE List
                SET Title = @title,
                    PlaybackRate = @playbackRate
                WHERE Id = @id;
                ",
                new { id, title, playbackRate });
        }

        public async Task DeleteAsync(Guid id)
        {
            using var connection = _connectionFactory.CreateConnection();
            await connection.ExecuteAsync(
                @"
                DELETE FROM List
                WHERE Id = @id;
                ",
                new { id });
        }

        public async Task<int> RemoveExpiredAsync(DateTimeOffset now)
        {
            using var connection = _connectionFactory.CreateConnection();
            return await connection.ExecuteAsync(
                @"
                DELETE FROM List
                WHERE ExpiredAfter <= @now;
                ",
                new { now });
        }
    }
}
