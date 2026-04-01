using Microsoft.AspNetCore.WebUtilities;
using System;

namespace youtubed.Models
{
    public class ConsumedShareLinkModel
    {
        public Guid ListId { get; set; }
        public byte[] Token { get; set; }

        public string TokenString => WebEncoders.Base64UrlEncode(Token);
    }
}
