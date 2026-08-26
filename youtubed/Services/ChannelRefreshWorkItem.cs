using System.Collections.Generic;
using youtubed.Domain;

namespace youtubed.Services
{
    internal sealed record ChannelRefreshWorkItem(
        int SelectedIndex,
        ChannelRefreshRequest Request,
        Channel Channel,
        IReadOnlyList<ChannelVideo> CachedVideos);
}
