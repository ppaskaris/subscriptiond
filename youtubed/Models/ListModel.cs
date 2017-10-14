using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace youtubed.Models
{
    public class ListModel
    {
        public Guid Id { get; set; }
        public byte[] Token { get; set; }
        public string Title { get; set; }
        public DateTimeOffset ExpiredAfter { get; set; }

        public string TokenString => Base64UrlEncoder.Encode(Token);
    }
}