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
    public sealed class DropWorkerStateMigrationIntegrationTests : LocalDbIntegrationTestBase
    {
        public DropWorkerStateMigrationIntegrationTests(LocalDbTestFixture fixture)
            : base(fixture)
        {
        }

        [LocalDbFact]
        public async Task Migration_DropsIntermediateWorkerStateAndIsRerunnable()
        {
            await ExecuteAsync(@"
                IF SCHEMA_ID(N'youtubed') IS NULL
                    EXEC(N'CREATE SCHEMA youtubed');

                CREATE TABLE dbo.WorkerState (
                    Id INT NOT NULL CONSTRAINT PK_WorkerState PRIMARY KEY
                );
                INSERT INTO dbo.WorkerState (Id) VALUES (1);

                CREATE TABLE youtubed.WorkerState (
                    Id INT NOT NULL CONSTRAINT PK_youtubed_WorkerState PRIMARY KEY
                );
                INSERT INTO youtubed.WorkerState (Id) VALUES (1);
                ");
            var migration = await File.ReadAllTextAsync(Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "youtubed", "Migrations",
                "20260814_DropWorkerState.sql")));

            await ExecuteAsync(migration);
            await ExecuteAsync(migration);

            Assert.Null(await ScalarAsync<int?>("SELECT OBJECT_ID(N'dbo.WorkerState', N'U');"));
            Assert.Null(await ScalarAsync<int?>("SELECT OBJECT_ID(N'youtubed.WorkerState', N'U');"));
        }
    }
}
