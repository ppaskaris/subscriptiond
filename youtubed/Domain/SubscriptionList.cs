using System;
using System.Collections.Generic;

namespace youtubed.Domain
{
    public class SubscriptionList
    {
        private byte[] _token = Array.Empty<byte>();

        public Guid Id { get; set; }
        public byte[] Token
        {
            get => (byte[])_token.Clone();
            set => _token = value == null
                ? Array.Empty<byte>()
                : (byte[])value.Clone();
        }
        public string Title { get; set; }
        public decimal PlaybackRate { get; set; } = Constants.DefaultListPlaybackRate;
        public DateTimeOffset ExpiredAfter { get; set; }
        public DateOnly? ExpirationRenewedOn { get; set; }
        public IReadOnlyList<string> ChannelIds { get; set; } = Array.Empty<string>();
    }
}
