using Dapper;
using System;
using System.Threading;
using System.Threading.Tasks;
using youtubed.Data;
using youtubed.Domain;
using youtubed.Services;

namespace youtubed.Persistence
{
    public sealed class WorkerStateRepository : IWorkerStateStore
    {
        private const int WorkerStateId = 1;

        private readonly IConnectionFactory _connectionFactory;
        private readonly IAppClock _clock;

        public WorkerStateRepository(IConnectionFactory connectionFactory, IAppClock clock)
        {
            _connectionFactory = connectionFactory;
            _clock = clock;
        }

        public async Task<WorkerState> GetOrCreateAsync(CancellationToken cancellationToken)
        {
            using var connection = _connectionFactory.CreateConnection();
            var now = _clock.UtcNow;
            var command = new CommandDefinition(
                @"
                MERGE INTO WorkerState WITH (HOLDLOCK) target
                USING (
                    SELECT @id AS Id,
                           @now AS NextChannelRefreshAt,
                           0 AS ChannelRefreshForceCount,
                           @now AS NextPurgeAt
                ) source ON source.Id = target.Id
                WHEN NOT MATCHED THEN
                    INSERT (Id, NextChannelRefreshAt, ChannelRefreshForceCount, NextPurgeAt)
                    VALUES (source.Id, source.NextChannelRefreshAt, source.ChannelRefreshForceCount, source.NextPurgeAt);

                SELECT NextChannelRefreshAt, ChannelRefreshForceCount, NextPurgeAt
                FROM WorkerState
                WHERE Id = @id;
                ",
                new { id = WorkerStateId, now },
                cancellationToken: cancellationToken);

            return await connection.QuerySingleAsync<WorkerState>(command);
        }

        public async Task ForceChannelRefreshAsync(CancellationToken cancellationToken)
        {
            await GetOrCreateAsync(cancellationToken);

            using var connection = _connectionFactory.CreateConnection();
            var command = new CommandDefinition(
                @"
                UPDATE WorkerState
                SET NextChannelRefreshAt = @nextChannelRefreshAt,
                    ChannelRefreshForceCount = ChannelRefreshForceCount + 1
                WHERE Id = @id;
                ",
                new
                {
                    id = WorkerStateId,
                    nextChannelRefreshAt = DateTimeOffset.MinValue
                },
                cancellationToken: cancellationToken);

            await connection.ExecuteAsync(command);
        }

        public async Task CompleteChannelRefreshPassAsync(
            DateTimeOffset? observedNextChannelRefreshAt,
            long observedChannelRefreshForceCount,
            DateTimeOffset? nextChannelRefreshAt,
            CancellationToken cancellationToken)
        {
            await GetOrCreateAsync(cancellationToken);

            using var connection = _connectionFactory.CreateConnection();
            var command = new CommandDefinition(
                @"
                UPDATE WorkerState
                SET NextChannelRefreshAt = @nextChannelRefreshAt
                WHERE Id = @id
                  AND (
                    (NextChannelRefreshAt IS NULL AND @observedNextChannelRefreshAt IS NULL)
                    OR NextChannelRefreshAt = @observedNextChannelRefreshAt
                  )
                  AND ChannelRefreshForceCount = @observedChannelRefreshForceCount;
                ",
                new
                {
                    id = WorkerStateId,
                    observedNextChannelRefreshAt,
                    observedChannelRefreshForceCount,
                    nextChannelRefreshAt
                },
                cancellationToken: cancellationToken);

            await connection.ExecuteAsync(command);
        }

        public async Task CompletePurgeAsync(
            DateTimeOffset nextPurgeAt,
            CancellationToken cancellationToken)
        {
            await GetOrCreateAsync(cancellationToken);

            using var connection = _connectionFactory.CreateConnection();
            var command = new CommandDefinition(
                @"
                UPDATE WorkerState
                SET NextPurgeAt = @nextPurgeAt
                WHERE Id = @id;
                ",
                new { id = WorkerStateId, nextPurgeAt },
                cancellationToken: cancellationToken);

            await connection.ExecuteAsync(command);
        }
    }
}
