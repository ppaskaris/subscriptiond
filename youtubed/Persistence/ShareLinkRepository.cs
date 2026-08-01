using Dapper;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using youtubed.Data;
using youtubed.Domain;

namespace youtubed.Persistence
{
    public class ShareLinkRepository : IShareLinkRepository
    {
        private readonly IConnectionFactory _connectionFactory;

        public ShareLinkRepository(IConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task<bool> TryCreateAsync(ShareLink shareLink)
        {
            using var connection = _connectionFactory.CreateConnection();

            try
            {
                await connection.ExecuteAsync(
                    @"
                    INSERT INTO ShareLink (Password, ListId, CreatedAt, ExpiresAfter, UsedAt)
                    VALUES (@Password, @ListId, @CreatedAt, @ExpiresAfter, @UsedAt);
                    ",
                    shareLink);
                return true;
            }
            catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
            {
                return false;
            }
        }

        public async Task<IReadOnlyList<ShareLink>> GetByListAsync(Guid listId)
        {
            using var connection = _connectionFactory.CreateConnection();
            var rows = await connection.QueryAsync<ShareLink>(
                @"
                SELECT Password, ListId, CreatedAt, ExpiresAfter, UsedAt
                FROM ShareLink
                WHERE ListId = @listId
                ORDER BY CreatedAt DESC,
                         Password ASC;
                ",
                new { listId });
            return rows.AsList();
        }

        public async Task DeleteAsync(Guid listId, string password)
        {
            using var connection = _connectionFactory.CreateConnection();
            await connection.ExecuteAsync(
                @"
                DELETE FROM ShareLink
                WHERE ListId = @listId
                  AND Password = @password;
                ",
                new { listId, password });
        }

        public async Task DeleteByListAsync(Guid listId)
        {
            using var connection = _connectionFactory.CreateConnection();
            await connection.ExecuteAsync(
                @"
                DELETE FROM ShareLink
                WHERE ListId = @listId;
                ",
                new { listId });
        }

        public async Task<ConsumedShareLink> ConsumeAsync(string password, DateTimeOffset now)
        {
            using var connection = _connectionFactory.CreateConnection();
            return await connection.QuerySingleOrDefaultAsync<ConsumedShareLink>(
                @"
                DECLARE @Consumed TABLE (
                    ListId UNIQUEIDENTIFIER NOT NULL
                );

                UPDATE ShareLink
                SET UsedAt = @now
                OUTPUT inserted.ListId INTO @Consumed (ListId)
                WHERE Password = @password
                  AND UsedAt IS NULL
                  AND ExpiresAfter > @now;

                SELECT consumed.ListId,
                       [List].Token
                FROM @Consumed consumed
                    INNER JOIN [List] ON [List].Id = consumed.ListId;
                ",
                new { password, now });
        }

        public async Task<int> RemoveExpiredAsync(DateTimeOffset deleteBefore)
        {
            using var connection = _connectionFactory.CreateConnection();
            return await connection.ExecuteAsync(
                @"
                DELETE FROM ShareLink
                WHERE ExpiresAfter <= @deleteBefore;
                ",
                new { deleteBefore });
        }
    }
}
