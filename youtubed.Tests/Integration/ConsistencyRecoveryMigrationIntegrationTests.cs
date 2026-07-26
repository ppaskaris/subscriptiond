using Dapper;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(LocalDbTestFixture.CollectionName)]
    [Trait("Category", "LocalDb")]
    public sealed class ConsistencyRecoveryMigrationIntegrationTests
    {
        private readonly LocalDbTestFixture _fixture;

        public ConsistencyRecoveryMigrationIntegrationTests(LocalDbTestFixture fixture)
        {
            _fixture = fixture;
        }

        [LocalDbFact]
        public async Task MigrationRestoresDefaultAndIsRerunnable()
        {
            var migration = await File.ReadAllTextAsync(Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "youtubed",
                "Migrations",
                "20260725_AddConsistencyRecoveryWorkerState.sql")));
            using var connection = _fixture.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                @"
                ALTER TABLE dbo.WorkerState
                    DROP CONSTRAINT DF_WorkerState_ConsistencyRecoveryForceCount;
                ALTER TABLE dbo.WorkerState
                    ALTER COLUMN NextConsistencyRecoveryAt DATETIMEOFFSET NULL;
                ");

            await connection.ExecuteAsync(migration);
            await connection.ExecuteAsync(migration);

            var isNullable = await connection.ExecuteScalarAsync<bool>(
                @"
                SELECT CAST(c.is_nullable AS bit)
                FROM sys.columns c
                WHERE c.object_id = OBJECT_ID(N'dbo.WorkerState')
                  AND c.name = N'NextConsistencyRecoveryAt';
                ");
            var forceDefaultCount = await connection.ExecuteScalarAsync<int>(
                @"
                SELECT COUNT(*)
                FROM sys.default_constraints dc
                INNER JOIN sys.columns c
                    ON c.object_id = dc.parent_object_id
                    AND c.column_id = dc.parent_column_id
                WHERE dc.parent_object_id = OBJECT_ID(N'dbo.WorkerState')
                  AND c.name = N'ConsistencyRecoveryForceCount';
                ");
            Assert.False(isNullable);
            Assert.Equal(1, forceDefaultCount);
        }
    }
}
