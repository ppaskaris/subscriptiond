using youtubed.Persistence;

namespace youtubed.Tests.ProviderContracts
{
    public sealed class ProviderContractTestContext
    {
        public ProviderContractTestContext(
            IListRepository lists,
            IChannelRepository channels,
            IShareLinkRepository shareLinks)
        {
            Lists = lists;
            Channels = channels;
            ShareLinks = shareLinks;
        }

        public IListRepository Lists { get; }

        public IChannelRepository Channels { get; }

        public IShareLinkRepository ShareLinks { get; }

    }
}
