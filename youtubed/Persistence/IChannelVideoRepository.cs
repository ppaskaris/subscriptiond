using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace youtubed.Persistence
{
    public interface IChannelVideoRepository
    {
        Task RefreshAsync(
            string channelId,
            DateTimeOffset earliestPublishedAt,
            IReadOnlyCollection<ChannelVideoRecord> videos,
            DateTimeOffset staleAfter);
    }
}
