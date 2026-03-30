using Dapper;
using System;
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
            await connection.ExecuteAsync(
                @"
                MERGE INTO Channel target
                USING (
                    SELECT @url AS Url
                ) source ON source.Url = target.Url
                WHEN MATCHED THEN
                    UPDATE SET StaleAfter = @staleAfter
                WHEN NOT MATCHED THEN
                    INSERT (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                    VALUES (@id, @url, @title, @thumbnail, @playlistId, @staleAfter, @staleAfter);
                ",
                new
                {
                    id = channel.Id,
                    url = channel.Url,
                    title = channel.Title,
                    thumbnail = channel.Thumbnail,
                    playlistId = channel.PlaylistId,
                    staleAfter
                });
        }

        public async Task<StaleChannelModel> ClaimNextStaleChannelAsync(DateTimeOffset now, DateTimeOffset visibleAfter)
        {
            using var connection = _connectionFactory.CreateConnection();
            return await connection.QueryFirstOrDefaultAsync<StaleChannelModel>(
                @"
                UPDATE target
                SET VisibleAfter = @visibleAfter
                OUTPUT inserted.Id, inserted.PlaylistId
                FROM (
                    SELECT TOP (1) *
                    FROM Channel
                    WHERE StaleAfter <= @now
                      AND VisibleAfter <= @now
                      AND EXISTS(SELECT * FROM ListChannel WHERE ListChannel.ChannelId = Channel.Id)
                    ORDER BY StaleAfter ASC,
                             VisibleAfter ASC
                ) target;
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
