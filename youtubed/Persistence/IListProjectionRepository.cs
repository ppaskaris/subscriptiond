using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using youtubed.Domain;

namespace youtubed.Persistence
{
    public interface IListProjectionRepository
    {
        Task UpdateProjectedChannelsAsync(
            IReadOnlyCollection<Channel> refreshedChannels,
            CancellationToken cancellationToken);
    }
}
