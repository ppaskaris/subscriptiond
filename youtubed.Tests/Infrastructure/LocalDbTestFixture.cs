using Dapper;
using Microsoft.Data.SqlClient;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using youtubed.Data;

namespace youtubed.Tests.Infrastructure
{
    public sealed class LocalDbTestFixture : IAsyncLifetime
    {
        public const string CollectionName = "LocalDb";

        private static int _isTimeSpanHandlerConfigured;

        public LocalDbTestFixture()
        {
            DatabaseName = $"youtubed_tests_{Guid.NewGuid():N}";
            ConnectionString = new SqlConnectionStringBuilder
            {
                DataSource = @"(localdb)\MSSQLLocalDB",
                InitialCatalog = DatabaseName,
                IntegratedSecurity = true,
                TrustServerCertificate = true
            }.ConnectionString;

            EnsureDapperTypeHandlers();
        }

        public string DatabaseName { get; }

        public string ConnectionString { get; }

        public IConnectionFactory ConnectionFactory => new ConnectionStringConnectionFactory(ConnectionString);

        public async Task InitializeAsync()
        {
            var schemaSql = await File.ReadAllTextAsync(GetSchemaPath());

            using var masterConnection = CreateMasterConnection();
            await masterConnection.OpenAsync();
            await masterConnection.ExecuteAsync($"CREATE DATABASE [{DatabaseName}];");

            using var databaseConnection = CreateConnection();
            await databaseConnection.OpenAsync();
            await databaseConnection.ExecuteAsync(schemaSql);
        }

        public async Task ResetAsync()
        {
            using var connection = CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                @"
                DELETE FROM ListChannel;
                DELETE FROM ChannelVideo;
                DELETE FROM List;
                DELETE FROM Channel;
                ");
        }

        public async Task DisposeAsync()
        {
            try
            {
                using var masterConnection = CreateMasterConnection();
                await masterConnection.OpenAsync();
                await masterConnection.ExecuteAsync(
                    $@"
                    IF DB_ID(N'{DatabaseName}') IS NOT NULL
                    BEGIN
                        ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                        DROP DATABASE [{DatabaseName}];
                    END
                    ");
            }
            catch
            {
                // Best-effort cleanup keeps failed runs debuggable.
            }
        }

        public SqlConnection CreateConnection()
        {
            return new SqlConnection(ConnectionString);
        }

        private SqlConnection CreateMasterConnection()
        {
            var builder = new SqlConnectionStringBuilder(ConnectionString)
            {
                InitialCatalog = "master"
            };

            return new SqlConnection(builder.ConnectionString);
        }

        private static void EnsureDapperTypeHandlers()
        {
            if (Interlocked.Exchange(ref _isTimeSpanHandlerConfigured, 1) == 0)
            {
                SqlMapper.AddTypeHandler(new TimeSpanTypeHandler());
            }
        }

        private static string GetSchemaPath()
        {
            return Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "youtubed",
                "Schema.sql"));
        }
    }
}
