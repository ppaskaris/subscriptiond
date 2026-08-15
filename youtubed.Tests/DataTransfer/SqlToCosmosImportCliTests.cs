using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using youtubed.DataTransfer;

namespace youtubed.Tests.DataTransfer
{
    public sealed class SqlToCosmosImportCliTests
    {
        private const string SourceSecret = "Server=source;Password=source-secret";
        private const string TargetSecret = "AccountEndpoint=https://target;AccountKey=target-secret";

        [Fact]
        public async Task RunAsync_ConsoleCancellationCancelsWorkAndUnregistersHandler()
        {
            var signal = new FakeConsoleCancelSignal();
            var runner = new CallbackRunner((_, _, cancellationToken) =>
            {
                signal.Trigger();
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });
            using var error = new StringWriter();

            var exitCode = await SqlToCosmosImportCli.RunAsync(
                CreateArgs(),
                TextWriter.Null,
                error,
                runner: runner,
                cancelSignal: signal);

            Assert.Equal(1, exitCode);
            Assert.Contains("operation cancelled", error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.True(signal.WasRegistered);
            Assert.True(signal.WasDisposed);
        }

        [Fact]
        public async Task RunAsync_ControlledFailureIsActionableAndRedacted()
        {
            var runner = new CallbackRunner((_, _, _) => throw new SqlToCosmosImportOperationException(
                SqlToCosmosImportError.RerunConfirmationRequired));
            using var error = new StringWriter();

            var exitCode = await SqlToCosmosImportCli.RunAsync(
                CreateArgs(),
                TextWriter.Null,
                error,
                runner: runner,
                cancelSignal: new FakeConsoleCancelSignal());

            var message = error.ToString();
            Assert.Equal(1, exitCode);
            Assert.Contains("--confirm-pre-cutover-rerun", message, StringComparison.Ordinal);
            Assert.Contains("offline", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(SourceSecret, message, StringComparison.Ordinal);
            Assert.DoesNotContain(TargetSecret, message, StringComparison.Ordinal);
            Assert.DoesNotContain("source-secret", message, StringComparison.Ordinal);
            Assert.DoesNotContain("target-secret", message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task RunAsync_RawProviderFailureRemainsGenericAndRedacted()
        {
            var runner = new CallbackRunner((command, _, _) => throw new InvalidOperationException(
                "Provider failed for " + command.SourceConnectionString + " and " + command.TargetConnectionString));
            using var error = new StringWriter();

            var exitCode = await SqlToCosmosImportCli.RunAsync(
                CreateArgs(),
                TextWriter.Null,
                error,
                runner: runner,
                cancelSignal: new FakeConsoleCancelSignal());

            var message = error.ToString();
            Assert.Equal(1, exitCode);
            Assert.Contains("No connection details", message, StringComparison.Ordinal);
            Assert.DoesNotContain(SourceSecret, message, StringComparison.Ordinal);
            Assert.DoesNotContain(TargetSecret, message, StringComparison.Ordinal);
            Assert.DoesNotContain("Provider failed", message, StringComparison.Ordinal);
        }

        private static string[] CreateArgs()
        {
            return new[]
            {
                "import-sql-to-cosmos",
                "validate",
                "--SourceConnectionString",
                SourceSecret,
                "--TargetConnectionString",
                TargetSecret,
                "--TargetDatabaseName",
                "target-database"
            };
        }

        private sealed class CallbackRunner : ISqlToCosmosCommandRunner
        {
            private readonly Func<SqlToCosmosCommand, TextWriter, CancellationToken, Task> _callback;

            public CallbackRunner(
                Func<SqlToCosmosCommand, TextWriter, CancellationToken, Task> callback)
            {
                _callback = callback;
            }

            public Task RunAsync(
                SqlToCosmosCommand command,
                TextWriter output,
                CancellationToken cancellationToken)
            {
                return _callback(command, output, cancellationToken);
            }
        }

        private sealed class FakeConsoleCancelSignal : IConsoleCancelSignal
        {
            private Action _cancel;

            public bool WasRegistered { get; private set; }
            public bool WasDisposed { get; private set; }

            public IDisposable Register(Action cancel)
            {
                WasRegistered = true;
                _cancel = cancel;
                return new Registration(this);
            }

            public void Trigger()
            {
                _cancel?.Invoke();
            }

            private sealed class Registration : IDisposable
            {
                private readonly FakeConsoleCancelSignal _owner;

                public Registration(FakeConsoleCancelSignal owner)
                {
                    _owner = owner;
                }

                public void Dispose()
                {
                    _owner._cancel = null;
                    _owner.WasDisposed = true;
                }
            }
        }
    }
}
