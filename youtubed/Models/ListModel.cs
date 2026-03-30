using Microsoft.AspNetCore.WebUtilities;
using System;

namespace youtubed.Models
{
    public class ListModel
    {
        public Guid Id { get; set; }
        public byte[] Token { get; set; }
        public string Title { get; set; }
        public DateTimeOffset ExpiredAfter { get; set; }

        public string TokenString => WebEncoders.Base64UrlEncode(Token);
    }
}
