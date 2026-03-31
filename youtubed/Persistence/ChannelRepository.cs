using Dapper;
using System;
using System.Data;
using System.Threading.Tasks;
using youtubed.Data;
using youtubed.Models;

namespace youtubed.Persistence
{
    public class ChannelRepository : IChannelRepository
    {
        private readonly IConnectionFactory _connectionFactory;

        public ChannelRepository(IConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task<ChannelModel> GetByUrlAsync(string url)
        {
            using var connection = _connectionFactory.CreateConnection();
            return await connection.QueryFirstOrDefaultAsync<ChannelModel>(
                @"
                SELECT Id, Url, Title, Thumbnail, PlaylistId
                FROM Channel
                WHERE Url = @url;
                ",
                new { url });
        }

        public async Task SaveDiscoveredChannelAsync(ChannelModel channel, DateTimeOffset staleAfter)
        {
            using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);

            var parameters = new
            {
                id = channel.Id,
                url = channel.Url,
                title = channel.Title,
                thumbnail = channel.Thumbnail,
                playlistId = channel.PlaylistId,
                staleAfter
            };

            // Rediscovery should only make an existing channel eligible again.
            var updated = await connection.ExecuteAsync(
                @"
                UPDATE Channel WITH (UPDLOCK, HOLDLOCK)
                SET StaleAfter = @staleAfter
                WHERE Url = @url;
                ",
                parameters,
                transaction);

            if (updated == 0)
            {
                await connection.ExecuteAsync(
                    @"
                    INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                    SELECT @id, @url, @title, @thumbnail, @playlistId, @staleAfter, @staleAfter
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM Channel WITH (UPDLOCK, HOLDLOCK)
                        WHERE Url = @url
                    );
                    ",
                    parameters,
                    transaction);

                await connection.ExecuteAsync(
                    @"
                    UPDATE Channel
                    SET StaleAfter = @staleAfter
                    WHERE Url = @url;
                    ",
                    parameters,
                    transaction);
            }

            transaction.Commit();
        }

        public async Task UpdateMetadataAsync(string id, string title, string thumbnail)
        {
            using var connection = _connectionFactory.CreateConnection();
            await connection.ExecuteAsync(
                @"
                UPDATE Channel
                SET Title = @title,
                    Thumbnail = @thumbnail
                WHERE Id = @id;
                ",
                new { id, title, thumbnail });
        }

        public async Task<StaleChannelModel> ClaimNextStaleChannelAsync(DateTimeOffset now, DateTimeOffset visibleAfter)
        {
            using var connection = _connectionFactory.CreateConnection();
            return await connection.QueryFirstOrDefaultAsync<StaleChannelModel>(
                @"
                ;WITH nextChannel AS (
                    SELECT TOP (1) Id, Url, Title, Thumbnail, PlaylistId, VisibleAfter
                    FROM Channel WITH (UPDLOCK, ROWLOCK)
                    WHERE StaleAfter <= @now
                      AND VisibleAfter <= @now
                      AND EXISTS(SELECT * FROM ListChannel WHERE ListChannel.ChannelId = Channel.Id)
                    ORDER BY StaleAfter ASC,
                             VisibleAfter ASC
                )
                UPDATE nextChannel
                SET VisibleAfter = @visibleAfter
                OUTPUT inserted.Id, inserted.Url, inserted.Title, inserted.Thumbnail, inserted.PlaylistId
                ;
                ",
                new { now, visibleAfter });
        }

        public async Task<int> RemoveOrphanChannelsAsync(DateTimeOffset now)
        {
            using var connection = _connectionFactory.CreateConnection();
            return await connection.ExecuteAsync(
                @"
                DELETE FROM Channel
                WHERE VisibleAfter <= @now
                  AND NOT EXISTS(SELECT * FROM ListChannel WHERE ListChannel.ChannelId = Channel.Id);
                ",
                new { now });
        }
    }
}
