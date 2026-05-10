using System;
using System.Collections.Generic;

namespace youtubed.Domain
{
    public class ListVideoProjection
    {
        public SubscriptionList List { get; set; }
        public IReadOnlyList<Channel> Channels { get; set; } = Array.Empty<Channel>();

        public class Channel
        {
            public string Id { get; set; }
            public string Url { get; set; }
            public string Title { get; set; }
            public string Thumbnail { get; set; }
            public DateTimeOffset StaleAfter { get; set; }
            public ChannelStatus Status { get; set; } = ChannelStatus.Active;
            public ChannelStatusReason StatusReason { get; set; } = ChannelStatusReason.None;
            public DateTimeOffset? StatusUpdatedAt { get; set; }
            public IReadOnlyList<ChannelVideo> Videos { get; set; } = Array.Empty<ChannelVideo>();
        }
    }
}
