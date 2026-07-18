using System;

namespace youtubed.Tests.Infrastructure
{
    public sealed class CosmosEmulatorOptions
    {
        public const string ConnectionStringEnvironmentVariableName =
            "YOUTUBED_COSMOS_EMULATOR_CONNECTION_STRING";

        public const string DefaultConnectionString =
            "AccountEndpoint=https://localhost:8081/;" +
            "AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;";

        public string ConnectionString { get; set; } = DefaultConnectionString;

        public static CosmosEmulatorOptions FromEnvironment()
        {
            return new CosmosEmulatorOptions
            {
                ConnectionString = Environment.GetEnvironmentVariable(
                    ConnectionStringEnvironmentVariableName) ?? DefaultConnectionString
            };
        }
    }
}
