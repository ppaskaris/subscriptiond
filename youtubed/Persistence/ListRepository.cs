using Dapper;
using System;
using System.Linq;
using System.Threading.Tasks;
using youtubed.Data;
using youtubed.Domain;
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

        public async Task<ListVideoProjection> GetVideoProjectionAsync(Guid id, DateTimeOffset expiredAfter, int videoLimit)
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

                SELECT Channel.Id,
                       Channel.Title,
                       Channel.Url,
                       Channel.Thumbnail,
                       Channel.StaleAfter,
                       Channel.Status,
                       Channel.StatusReason,
                       Channel.StatusUpdatedAt
                FROM ListChannel
                    INNER JOIN Channel ON Channel.Id = ListChannel.ChannelId
                WHERE ListChannel.ListId = @id
                ORDER BY Channel.Id ASC;

                SELECT TOP (@videoLimit)
                       ChannelVideo.ChannelId,
                       ChannelVideo.Id AS VideoId,
                       ChannelVideo.Title,
                       ChannelVideo.Duration,
                       ChannelVideo.PublishedAt,
                       ChannelVideo.Thumbnail AS ThumbnailUrl
                FROM ListChannel
                    INNER JOIN ChannelVideo ON ChannelVideo.ChannelId = ListChannel.ChannelId
                WHERE ListChannel.ListId = @id
                ORDER BY ChannelVideo.PublishedAt DESC,
                         ChannelVideo.Id ASC;
                ",
                new { id, expiredAfter, videoLimit });
            var list = await query.ReadSingleOrDefaultAsync<SubscriptionList>();
            if (list == null)
            {
                return null;
            }

            var channels = (await query.ReadAsync<ListVideoProjection.Channel>()).AsList();
            var videosByChannelId = (await query.ReadAsync<ChannelVideo>())
                .ToLookup(video => video.ChannelId, StringComparer.Ordinal);

            foreach (var channel in channels)
            {
                channel.Videos = videosByChannelId[channel.Id].AsList();
            }

            return new ListVideoProjection
            {
                List = list,
                Channels = channels
            };
        }

        public async Task<ListChannelProjection> GetChannelProjectionAsync(Guid id, DateTimeOffset expiredAfter)
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

                SELECT Channel.Id,
                       Channel.Title,
                       Channel.Url,
                       Channel.Thumbnail,
                       Channel.StaleAfter,
                       Channel.Status,
                       Channel.StatusReason,
                       Channel.StatusUpdatedAt
                FROM ListChannel
                    INNER JOIN Channel ON Channel.Id = ListChannel.ChannelId
                WHERE ListChannel.ListId = @id
                ORDER BY Channel.Title ASC;
                ",
                new { id, expiredAfter });
            var list = await query.ReadSingleOrDefaultAsync<SubscriptionList>();
            if (list == null)
            {
                return null;
            }

            return new ListChannelProjection
            {
                List = list,
                Channels = (await query.ReadAsync<ListChannelProjection.Channel>()).AsList()
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
