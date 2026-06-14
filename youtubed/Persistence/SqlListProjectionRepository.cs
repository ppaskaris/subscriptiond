using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using youtubed.Domain;

namespace youtubed.Persistence
{
    public sealed class SqlListProjectionRepository : IListProjectionRepository
    {
        public Task UpdateProjectedChannelsAsync(
            IReadOnlyCollection<Channel> refreshedChannels,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
