using youtubed.Persistence;

namespace youtubed.Tests.ProviderContracts
{
    public sealed class ProviderContractTestContext
    {
        public ProviderContractTestContext(
            IListRepository lists,
            IChannelRepository channels,
            IShareLinkRepository shareLinks,
            IExpirationPurger expirationPurger)
        {
            Lists = lists;
            Channels = channels;
            ShareLinks = shareLinks;
            ExpirationPurger = expirationPurger;
        }

        public IListRepository Lists { get; }

        public IChannelRepository Channels { get; }

        public IShareLinkRepository ShareLinks { get; }

        public IExpirationPurger ExpirationPurger { get; }
    }
}
