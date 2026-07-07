using System.Threading.Tasks;
using youtubed.Services;

namespace youtubed.Tests.ProviderContracts
{
    public interface IProviderContractTestFixture
    {
        string ProviderName { get; }

        Task ResetAsync();

        ProviderContractTestContext CreateContext(IAppClock clock);
    }
}
