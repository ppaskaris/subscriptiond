using Dapper;
using System;
using System.Linq;
using System.Threading.Tasks;
using youtubed.Data;
using youtubed.Domain;
using youtubed.SecurityTheatre;

namespace youtubed.Persistence
{
    public class ListRepository : IListRepository
    {
        private readonly IConnectionFactory _connectionFactory;

        public ListRepository(IConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task CreateAsync(SubscriptionList list)
        {
            using var connection = _connectionFactory.CreateConnection();
            await connection.ExecuteAsync(
                @"
                INSERT INTO List (Id, Token, Title, PlaybackRate, ExpiredAfter, ExpirationRenewedOn)
                VALUES (@Id, @Token, @Title, @PlaybackRate, @ExpiredAfter, @ExpirationRenewedOn);
                ",
                list);
        }

        public async Task<SubscriptionList> GetAsync(Guid id)
        {
            using var connection = _connectionFactory.CreateConnection();
            return await connection.QueryFirstOrDefaultAsync<SubscriptionList>(
                @"
                SELECT Id, Token, Title, PlaybackRate, ExpiredAfter, ExpirationRenewedOn
                FROM List
                WHERE Id = @id;
                ",
                new { id });
        }

        public async Task<ListVideoProjection> GetAuthenticatedVideoProjectionAsync(
            Guid id,
            byte[] token,
            DateTimeOffset expiredAfter,
            DateOnly renewedOn,
            int videoLimit)
        {
            var list = await GetAsync(id);
            if (list == null
                || TokenUtils.NotEqual(token, list.Token))
            {
                return null;
            }

            if (list.ExpirationRenewedOn != renewedOn)
            {
                await RenewExpirationAsync(id, expiredAfter, renewedOn);
            }

            return await GetVideoProjectionCoreAsync(list, videoLimit);
        }

        public async Task RenewExpirationAsync(Guid id, DateTimeOffset expiredAfter, DateOnly renewedOn)
        {
            using var connection = _connectionFactory.CreateConnection();
            await connection.ExecuteAsync(
                @"
                UPDATE List
                SET ExpiredAfter = @expiredAfter,
                    ExpirationRenewedOn = @renewedOn
                WHERE Id = @id
                  AND (ExpirationRenewedOn IS NULL OR ExpirationRenewedOn <> @renewedOn);
                ",
                new { id, expiredAfter, renewedOn });
        }

        public Task<ListVideoProjection> GetVideoProjectionAsync(
            SubscriptionList list,
            int videoLimit) => GetVideoProjectionCoreAsync(list, videoLimit);

        private async Task<ListVideoProjection> GetVideoProjectionCoreAsync(
            SubscriptionList list,
            int videoLimit)
        {
            using var connection = _connectionFactory.CreateConnection();
            using var query = await connection.QueryMultipleAsync(
                @"
                SELECT ChannelId
                FROM ListChannel
                WHERE ListId = @id
                ORDER BY ChannelId ASC;

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
                new { id = list.Id, videoLimit });

            var channelIds = (await query.ReadAsync<string>()).AsList();
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
                ChannelIds = channelIds,
                Channels = channels
            };
        }

        public async Task<ListChannelProjection> GetChannelProjectionAsync(SubscriptionList list)
        {
            using var connection = _connectionFactory.CreateConnection();
            using var query = await connection.QueryMultipleAsync(
                @"
                SELECT ChannelId
                FROM ListChannel
                WHERE ListId = @id
                ORDER BY ChannelId ASC;

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
                new { id = list.Id });

            var channelIds = (await query.ReadAsync<string>()).AsList();
            return new ListChannelProjection
            {
                List = list,
                ChannelIds = channelIds,
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
