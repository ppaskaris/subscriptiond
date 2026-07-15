using System.Threading.Tasks;
using youtubed.Services;

namespace youtubed.Tests.ProviderContracts
{
    public enum ExpirationPurgeBehavior
    {
        ImmediateDeletion,
        NoOp
    }

    public interface IProviderContractTestFixture
    {
        string ProviderName { get; }

        ExpirationPurgeBehavior PurgeBehavior { get; }

        Task ResetAsync();

        ProviderContractTestContext CreateContext(IAppClock clock);
    }
}
