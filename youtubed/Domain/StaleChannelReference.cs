using System;

namespace youtubed.Domain
{
    public class StaleChannelReference
    {
        public string Id { get; set; }
        public DateTimeOffset StaleAfter { get; set; }
    }
}
