using Dapper;
using Microsoft.Azure.Cosmos;
using System;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using youtubed.Data;
using youtubed.Persistence.Cosmos;
using youtubed.Services;

namespace youtubed.DataTransfer
{
    internal sealed record SqlToCosmosCommand(
        SqlToCosmosImportMode Mode,
        string SourceConnectionString,
        string TargetConnectionString,
        string TargetDatabaseName,
        int BatchSize,
        bool ConfirmEmptyTarget,
        bool ConfirmPreCutoverRerun);

    internal interface ISqlToCosmosCommandRunner
    {
        Task RunAsync(
            SqlToCosmosCommand command,
            TextWriter output,
            CancellationToken cancellationToken);
    }

    internal interface IConsoleCancelSignal
    {
        IDisposable Register(Action cancel);
    }

    internal sealed class ConsoleCancelSignal : IConsoleCancelSignal
    {
        public static readonly ConsoleCancelSignal Instance = new();

        public IDisposable Register(Action cancel)
        {
            ArgumentNullException.ThrowIfNull(cancel);
            ConsoleCancelEventHandler handler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancel();
            };
            Console.CancelKeyPress += handler;
            return new CallbackDisposable(() => Console.CancelKeyPress -= handler);
        }

        private sealed class CallbackDisposable : IDisposable
        {
            private Action _callback;

            public CallbackDisposable(Action callback)
            {
                _callback = callback;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _callback, null)?.Invoke();
            }
        }
    }

    internal sealed class SqlToCosmosCommandRunner : ISqlToCosmosCommandRunner
    {
        public async Task RunAsync(
            SqlToCosmosCommand command,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var initializationStopwatch = Stopwatch.StartNew();
            var initializationDuration = TimeSpan.Zero;
            CosmosImportTarget target = null;
            var succeeded = false;
            SqlMapper.AddTypeHandler(new TimeSpanTypeHandler());
            SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
            var cosmosOptions = new CosmosOptions
            {
                ConnectionString = command.TargetConnectionString,
                DatabaseName = command.TargetDatabaseName
            };
            try
            {
                using var client = CosmosClientFactory.Create(cosmosOptions);
                CosmosPersistenceContext context;
                try
                {
                    context = await new CosmosContainerInitializer()
                        .InitializeProductionAsync(client, cosmosOptions, cancellationToken);
                    initializationStopwatch.Stop();
                    initializationDuration = initializationStopwatch.Elapsed;
                }
                catch (InvalidOperationException)
                {
                    throw new SqlToCosmosImportOperationException(
                        SqlToCosmosImportError.TargetConfigurationInvalid);
                }

                var clock = new AppClock();
                target = new CosmosImportTarget(context);
                var service = new SqlToCosmosImportService(
                    new SqlImportSource(command.SourceConnectionString),
                    target,
                    output,
                    clock);
                await service.RunAsync(
                    new SqlToCosmosImportOptions(
                        command.Mode,
                        command.BatchSize,
                        command.ConfirmEmptyTarget,
                        command.ConfirmPreCutoverRerun),
                    clock.UtcNow,
                    cancellationToken);
                succeeded = true;
            }
            finally
            {
                stopwatch.Stop();
                if (initializationStopwatch.IsRunning)
                {
                    initializationStopwatch.Stop();
                    initializationDuration = initializationStopwatch.Elapsed;
                }
                var metrics = target?.Metrics ?? new SqlToCosmosTargetMetrics(0, 0, 0);
                await output.WriteLineAsync(CreateMetricsOutput(
                    command.Mode,
                    succeeded,
                    stopwatch.Elapsed,
                    initializationDuration,
                    metrics));
            }
        }

        internal static string CreateMetricsOutput(
            SqlToCosmosImportMode mode,
            bool succeeded,
            TimeSpan totalDuration,
            TimeSpan initializationDuration,
            SqlToCosmosTargetMetrics metrics)
        {
            ArgumentNullException.ThrowIfNull(metrics);
            return $"MigrationMetrics Mode={mode.ToString().ToLowerInvariant()} " +
                    $"Succeeded={succeeded.ToString().ToLowerInvariant()} " +
                    $"TotalDurationMs={totalDuration.TotalMilliseconds.ToString("0.##", CultureInfo.InvariantCulture)} " +
                    $"InitializationDurationMs={initializationDuration.TotalMilliseconds.ToString("0.##", CultureInfo.InvariantCulture)} " +
                    $"TargetSdkOperations={metrics.RequestCount.ToString(CultureInfo.InvariantCulture)} " +
                    $"TargetOperationRu={metrics.RequestCharge.ToString("0.##", CultureInfo.InvariantCulture)} " +
                    $"SurfacedThrottles={metrics.SurfacedThrottleCount.ToString(CultureInfo.InvariantCulture)} " +
                    "InitializationIncludedInTargetMetrics=false";
        }
    }

    internal static class SqlToCosmosImportCli
    {
        private const string CommandName = "import-sql-to-cosmos";

        public static bool IsCommand(string[] args)
        {
            return args.Length > 0
                && string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase);
        }

        public static async Task<int> RunAsync(
            string[] args,
            TextWriter output = null,
            TextWriter error = null,
            CancellationToken cancellationToken = default,
            ISqlToCosmosCommandRunner runner = null,
            IConsoleCancelSignal cancelSignal = null)
        {
            output ??= Console.Out;
            error ??= Console.Error;
            if (!TryParse(args.Skip(1).ToArray(), out var command, out var parseError))
            {
                await error.WriteLineAsync(parseError);
                WriteUsage(error);
                return 2;
            }

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            using var cancelRegistration = (cancelSignal ?? ConsoleCancelSignal.Instance)
                .Register(linkedCancellation.Cancel);
            try
            {
                await (runner ?? new SqlToCosmosCommandRunner())
                    .RunAsync(command, output, linkedCancellation.Token);
                return 0;
            }
            catch (OperationCanceledException)
            {
                await error.WriteLineAsync("SQL-to-Cosmos operation cancelled.");
                return 1;
            }
            catch (SqlToCosmosImportOperationException exception)
            {
                await error.WriteLineAsync("SQL-to-Cosmos operation failed: " + exception.Message);
                return 1;
            }
            catch
            {
                await error.WriteLineAsync(
                    "SQL-to-Cosmos operation failed. No connection details, document data, or raw diagnostics were written.");
                return 1;
            }
        }

        private static bool TryParse(
            string[] args,
            out SqlToCosmosCommand command,
            out string error)
        {
            command = null;
            error = null;
            if (args.Length == 0 || !Enum.TryParse<SqlToCosmosImportMode>(args[0], true, out var mode))
            {
                error = "Mode must be validate, import, or reconcile.";
                return false;
            }

            string sourceConnectionString = null;
            string targetConnectionString = null;
            string targetDatabaseName = null;
            var batchSize = 100;
            var confirmEmptyTarget = false;
            var confirmPreCutoverRerun = false;

            for (var index = 1; index < args.Length; index++)
            {
                if (IsOption(args[index], "confirm-empty-target"))
                {
                    confirmEmptyTarget = true;
                    continue;
                }
                if (IsOption(args[index], "confirm-pre-cutover-rerun"))
                {
                    confirmPreCutoverRerun = true;
                    continue;
                }
                if (TryReadOption(args, ref index, "SourceConnectionString", out var source))
                {
                    sourceConnectionString = source;
                    continue;
                }
                if (TryReadOption(args, ref index, "TargetConnectionString", out var target))
                {
                    targetConnectionString = target;
                    continue;
                }
                if (TryReadOption(args, ref index, "TargetDatabaseName", out var database))
                {
                    targetDatabaseName = database;
                    continue;
                }
                if (TryReadOption(args, ref index, "BatchSize", out var batchText))
                {
                    if (!int.TryParse(batchText, NumberStyles.None, CultureInfo.InvariantCulture, out batchSize)
                        || batchSize < 1
                        || batchSize > 100)
                    {
                        error = "BatchSize must be from 1 through 100.";
                        return false;
                    }
                    continue;
                }

                error = "Unknown SQL-to-Cosmos argument.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(sourceConnectionString)
                || string.IsNullOrWhiteSpace(targetConnectionString)
                || string.IsNullOrWhiteSpace(targetDatabaseName))
            {
                error = "SourceConnectionString, TargetConnectionString, and TargetDatabaseName are required.";
                return false;
            }
            if (confirmEmptyTarget && confirmPreCutoverRerun)
            {
                error = "Choose only one target confirmation option.";
                return false;
            }
            if (mode != SqlToCosmosImportMode.Import
                && (confirmEmptyTarget || confirmPreCutoverRerun))
            {
                error = "Target confirmation options are valid only for import mode.";
                return false;
            }

            command = new SqlToCosmosCommand(
                mode,
                sourceConnectionString,
                targetConnectionString,
                targetDatabaseName,
                batchSize,
                confirmEmptyTarget,
                confirmPreCutoverRerun);
            return true;
        }

        private static bool TryReadOption(
            string[] args,
            ref int index,
            string name,
            out string value)
        {
            var prefix = "--" + name;
            value = null;
            if (args[index].StartsWith(prefix + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = args[index].Substring(prefix.Length + 1);
                return true;
            }
            if (!string.Equals(args[index], prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (++index >= args.Length)
            {
                value = string.Empty;
                return true;
            }
            value = args[index];
            return true;
        }

        private static bool IsOption(string value, string name)
        {
            return string.Equals(value, "--" + name, StringComparison.OrdinalIgnoreCase);
        }

        private static void WriteUsage(TextWriter writer)
        {
            writer.WriteLine(
                "Usage: import-sql-to-cosmos <validate|import|reconcile> --SourceConnectionString <sql> --TargetConnectionString <cosmos> --TargetDatabaseName <database> [--BatchSize <1-100>] [--confirm-empty-target|--confirm-pre-cutover-rerun]");
        }
    }
}
