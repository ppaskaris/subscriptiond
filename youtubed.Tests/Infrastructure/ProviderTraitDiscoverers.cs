using System.Collections.Generic;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace youtubed.Tests.Infrastructure
{
    public sealed class LocalDbTraitDiscoverer : ITraitDiscoverer
    {
        public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
        {
            yield return new KeyValuePair<string, string>("Provider", "LocalDb");
        }
    }

    public sealed class CosmosTraitDiscoverer : ITraitDiscoverer
    {
        public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
        {
            yield return new KeyValuePair<string, string>("Provider", "Cosmos");
        }
    }
}
