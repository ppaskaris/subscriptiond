using System;
using Xunit;

namespace youtubed.Tests.Infrastructure
{
    public sealed class CosmosFactAttribute : FactAttribute
    {
        public const string EnvironmentVariableName = "YOUTUBED_RUN_COSMOS_TESTS";

        public CosmosFactAttribute()
        {
            if (!IsEnabled())
            {
                Skip = $"Set {EnvironmentVariableName}=true to run Cosmos emulator tests.";
            }
        }

        public static bool IsEnabled()
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentVariableName);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }
    }
}
