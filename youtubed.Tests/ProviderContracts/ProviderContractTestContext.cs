using youtubed.Persistence;

namespace youtubed.Tests.ProviderContracts
{
    public sealed class ProviderContractTestContext
    {
        public ProviderContractTestContext(
            IListRepository lists,
            IChannelRepository channels,
            IShareLinkRepository shareLinks,
            IListProjectionRepository listProjections,
            IWorkerStateStore workerState,
            IExpirationPurger expirationPurger)
        {
            Lists = lists;
            Channels = channels;
            ShareLinks = shareLinks;
            ListProjections = listProjections;
            WorkerState = workerState;
            ExpirationPurger = expirationPurger;
        }

        public IListRepository Lists { get; }

        public IChannelRepository Channels { get; }

        public IShareLinkRepository ShareLinks { get; }

        public IListProjectionRepository ListProjections { get; }

        public IWorkerStateStore WorkerState { get; }

        public IExpirationPurger ExpirationPurger { get; }
    }
}
