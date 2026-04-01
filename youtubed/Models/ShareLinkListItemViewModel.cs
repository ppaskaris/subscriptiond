using System;

namespace youtubed.Models
{
    public class ShareLinkListItemViewModel
    {
        public string Password { get; set; }
        public string ShareUrl { get; set; }
        public DateTimeOffset ExpiresAfter { get; set; }
        public DateTimeOffset? UsedAt { get; set; }
        public string Status { get; set; }
    }
}
