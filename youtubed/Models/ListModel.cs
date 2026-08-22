using Microsoft.AspNetCore.WebUtilities;
using System;
using System.Collections.Generic;

namespace youtubed.Models
{
    public class ListModel
    {
        public Guid Id { get; set; }
        public byte[] Token { get; set; }
        public string Title { get; set; }
        public decimal PlaybackRate { get; set; } = Constants.DefaultListPlaybackRate;
        public DateTimeOffset ExpiredAfter { get; set; }
        public DateOnly? ExpirationRenewedOn { get; set; }
        public IReadOnlyList<string> ChannelIds { get; set; } = Array.Empty<string>();

        public string TokenString => WebEncoders.Base64UrlEncode(Token);
    }
}
