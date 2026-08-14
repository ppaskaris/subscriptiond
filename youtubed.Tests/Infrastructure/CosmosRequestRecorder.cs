using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace youtubed.Tests.Infrastructure
{
    internal sealed class CosmosRequestRecorder<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<CosmosRequestRecord> _records = new();

        public IReadOnlyList<CosmosRequestRecord> Records => _records.ToArray();

        public void Clear()
        {
            while (_records.TryDequeue(out _))
            {
            }
        }

        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception exception,
            Func<TState, Exception, string> formatter)
        {
            var values = state as IEnumerable<KeyValuePair<string, object>>;
            if (eventId.Id != 4100 || values == null)
            {
                return;
            }

            var fields = values.ToDictionary(value => value.Key, value => value.Value);
            _records.Enqueue(new CosmosRequestRecord(
                (string)fields["Operation"],
                (string)fields["Container"],
                (int)fields["RequestCount"],
                Convert.ToDouble(fields["RequestCharge"]),
                Convert.ToDouble(fields["ElapsedMilliseconds"]),
                (int)fields["Status"],
                (int)fields["RetryCount"],
                formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    internal sealed record CosmosRequestRecord(
        string Operation,
        string Container,
        int RequestCount,
        double RequestCharge,
        double ElapsedMilliseconds,
        int Status,
        int RetryCount,
        string Rendered);
}
