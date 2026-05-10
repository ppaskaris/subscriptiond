using Dapper;
using System;
using System.Data;
using System.Threading.Tasks;
using youtubed.Data;
using youtubed.Domain;
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
                SELECT Id, Url, Title, Thumbnail, PlaylistId, Status, StatusReason, StatusUpdatedAt
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
                status = ChannelStatus.Active,
                statusReason = ChannelStatusReason.None,
                staleAfter
            };

            // Rediscovery should only make an existing channel eligible again.
            var updated = await connection.ExecuteAsync(
                @"
                UPDATE Channel WITH (UPDLOCK, HOLDLOCK)
                SET StaleAfter = @staleAfter,
                    Status = @status,
                    StatusReason = @statusReason,
                    StatusUpdatedAt = NULL
                WHERE Id = @id
                   OR Url = @url;
                ",
                parameters,
                transaction);

            if (updated == 0)
            {
                await connection.ExecuteAsync(
                    @"
                    INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter, Status, StatusReason, StatusUpdatedAt)
                    SELECT @id, @url, @title, @thumbnail, @playlistId, @staleAfter, @staleAfter, @status, @statusReason, NULL
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM Channel WITH (UPDLOCK, HOLDLOCK)
                        WHERE Id = @id
                           OR Url = @url
                    );
                    ",
                    parameters,
                    transaction);

                await connection.ExecuteAsync(
                    @"
                    UPDATE Channel
                    SET StaleAfter = @staleAfter
                    WHERE Id = @id
                       OR Url = @url;
                    ",
                    parameters,
                    transaction);
            }

            transaction.Commit();
        }

        public async Task UpdateMetadataAsync(string id, string url, string title, string thumbnail, string playlistId)
        {
            using var connection = _connectionFactory.CreateConnection();
            await connection.ExecuteAsync(
                @"
                UPDATE Channel
                SET Url = @url,
                    Title = @title,
                    Thumbnail = @thumbnail,
                    PlaylistId = @playlistId,
                    Status = @status,
                    StatusReason = @statusReason,
                    StatusUpdatedAt = NULL
                WHERE Id = @id;
                ",
                new { id, url, title, thumbnail, playlistId, status = ChannelStatus.Active, statusReason = ChannelStatusReason.None });
        }

        public async Task MarkUnavailableAsync(string id, ChannelStatusReason reason, DateTimeOffset statusUpdatedAt, DateTimeOffset staleAfter)
        {
            using var connection = _connectionFactory.CreateConnection();
            await connection.ExecuteAsync(
                @"
                UPDATE Channel
                SET Status = @status,
                    StatusReason = @reason,
                    StatusUpdatedAt = @statusUpdatedAt,
                    StaleAfter = @staleAfter
                WHERE Id = @id;
                ",
                new
                {
                    id,
                    status = ChannelStatus.Unavailable,
                    reason,
                    statusUpdatedAt,
                    staleAfter
                });
        }

        public async Task<StaleChannelModel> ClaimNextStaleChannelAsync(DateTimeOffset now, DateTimeOffset visibleAfter)
        {
            using var connection = _connectionFactory.CreateConnection();
            return await connection.QueryFirstOrDefaultAsync<StaleChannelModel>(
                @"
                ;WITH nextChannel AS (
                    SELECT TOP (1) Id, Url, Title, Thumbnail, PlaylistId, Status, StatusReason, StatusUpdatedAt, VisibleAfter
                    FROM Channel WITH (UPDLOCK, ROWLOCK)
                    WHERE StaleAfter <= @now
                      AND VisibleAfter <= @now
                      AND Status = @status
                      AND EXISTS(SELECT * FROM ListChannel WHERE ListChannel.ChannelId = Channel.Id)
                    ORDER BY StaleAfter ASC,
                             VisibleAfter ASC
                )
                UPDATE nextChannel
                SET VisibleAfter = @visibleAfter
                OUTPUT inserted.Id,
                       inserted.Url,
                       inserted.Title,
                       inserted.Thumbnail,
                       inserted.PlaylistId,
                       inserted.Status,
                       inserted.StatusReason,
                       inserted.StatusUpdatedAt
                ;
                ",
                new { now, visibleAfter, status = ChannelStatus.Active });
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
