using Microsoft.AspNetCore.WebUtilities;
using System;

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

        public string TokenString => WebEncoders.Base64UrlEncode(Token);
    }
}
