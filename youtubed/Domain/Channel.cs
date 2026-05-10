using System;
using System.Collections.Generic;

namespace youtubed.Domain
{
    public class Channel
    {
        public string Id { get; set; }
        public string Url { get; set; }
        public string Title { get; set; }
        public string Thumbnail { get; set; }
        public string PlaylistId { get; set; }
        public DateTimeOffset StaleAfter { get; set; }
        public ChannelStatus Status { get; set; } = ChannelStatus.Active;
        public ChannelStatusReason StatusReason { get; set; } = ChannelStatusReason.None;
        public DateTimeOffset? StatusUpdatedAt { get; set; }
        public IReadOnlyList<Guid> SubscribedListIds { get; set; } = Array.Empty<Guid>();
        public int SubscriptionCount { get; set; }
        public DateTimeOffset? OrphanedAfter { get; set; }
        public IReadOnlyList<ChannelVideo> Videos { get; set; } = Array.Empty<ChannelVideo>();
    }
}
