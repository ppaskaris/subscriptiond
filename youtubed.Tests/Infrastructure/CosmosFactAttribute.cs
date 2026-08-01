using System;
using Xunit;
using Xunit.Sdk;

namespace youtubed.Tests.Infrastructure
{
    [TraitDiscoverer("youtubed.Tests.Infrastructure.CosmosTraitDiscoverer", "youtubed.Tests")]
    public sealed class CosmosFactAttribute : FactAttribute, ITraitAttribute
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
