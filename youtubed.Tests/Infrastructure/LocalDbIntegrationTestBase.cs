using Dapper;
using Microsoft.Data.SqlClient;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace youtubed.Tests.Infrastructure
{
    public abstract class LocalDbIntegrationTestBase : IAsyncLifetime
    {
        protected LocalDbIntegrationTestBase(LocalDbTestFixture fixture)
        {
            Fixture = fixture;
        }

        protected LocalDbTestFixture Fixture { get; }

        public Task InitializeAsync()
        {
            return Fixture.ResetAsync();
        }

        public Task DisposeAsync()
        {
            return Task.CompletedTask;
        }

        protected SqlConnection CreateConnection()
        {
            return Fixture.CreateConnection();
        }

        protected async Task ExecuteAsync(string sql, object param = null)
        {
            using var connection = CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(sql, param);
        }

        protected async Task<T> ScalarAsync<T>(string sql, object param = null)
        {
            using var connection = CreateConnection();
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<T>(sql, param);
        }

        protected async Task<T> QuerySingleAsync<T>(string sql, object param = null)
        {
            using var connection = CreateConnection();
            await connection.OpenAsync();
            return await connection.QuerySingleAsync<T>(sql, param);
        }

        protected async Task<T> QuerySingleOrDefaultAsync<T>(string sql, object param = null)
        {
            using var connection = CreateConnection();
            await connection.OpenAsync();
            return await connection.QuerySingleOrDefaultAsync<T>(sql, param);
        }

        protected async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object param = null)
        {
            using var connection = CreateConnection();
            await connection.OpenAsync();
            var rows = await connection.QueryAsync<T>(sql, param);
            return rows.AsList();
        }
    }
}
