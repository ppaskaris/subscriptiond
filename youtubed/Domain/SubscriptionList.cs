using System;
using System.Collections.Generic;

namespace youtubed.Domain
{
    public class SubscriptionList
    {
        public Guid Id { get; set; }
        public byte[] Token { get; set; } = Array.Empty<byte>();
        public string Title { get; set; }
        public decimal PlaybackRate { get; set; } = Constants.DefaultListPlaybackRate;
        public DateTimeOffset ExpiredAfter { get; set; }
        public DateOnly? ExpirationRenewedOn { get; set; }
        public IReadOnlyList<string> ChannelIds { get; set; } = Array.Empty<string>();
    }
}
