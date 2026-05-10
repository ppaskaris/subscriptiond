using System;

namespace youtubed.Domain
{
    public class ShareLink
    {
        public string Password { get; set; }
        public Guid ListId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset ExpiresAfter { get; set; }
        public DateTimeOffset? UsedAt { get; set; }
    }
}
