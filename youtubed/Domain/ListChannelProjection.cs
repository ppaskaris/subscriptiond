using System;
using System.Collections.Generic;

namespace youtubed.Domain
{
    public class ListChannelProjection
    {
        public SubscriptionList List { get; set; }
        public IReadOnlyList<string> ChannelIds { get; set; } = Array.Empty<string>();
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
        }
    }
}
