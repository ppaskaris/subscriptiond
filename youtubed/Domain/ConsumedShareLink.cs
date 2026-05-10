using System;

namespace youtubed.Domain
{
    public class ConsumedShareLink
    {
        public Guid ListId { get; set; }
        public byte[] Token { get; set; } = Array.Empty<byte>();
    }
}
