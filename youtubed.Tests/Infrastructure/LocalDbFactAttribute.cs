using System;
using Xunit;

namespace youtubed.Tests.Infrastructure
{
    public sealed class LocalDbFactAttribute : FactAttribute
    {
        public const string EnvironmentVariableName = "YOUTUBED_RUN_LOCALDB_TESTS";

        public LocalDbFactAttribute()
        {
            if (!ShouldRun())
            {
                Skip = $"Set {EnvironmentVariableName}=true to run LocalDB integration tests.";
            }
        }

        private static bool ShouldRun()
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentVariableName);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }
    }
}
