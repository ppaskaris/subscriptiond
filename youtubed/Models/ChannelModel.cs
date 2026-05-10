using System;
using youtubed.Domain;

namespace youtubed.Models
{
    public class ChannelModel
    {
        public string Id { get; set; }
        public string Url { get; set; }
        public string Title { get; set; }
        public string Thumbnail { get; set; }
        public string PlaylistId { get; set; }
        public ChannelStatus Status { get; set; } = ChannelStatus.Active;
        public ChannelStatusReason StatusReason { get; set; } = ChannelStatusReason.None;
        public DateTimeOffset? StatusUpdatedAt { get; set; }
    }
}
