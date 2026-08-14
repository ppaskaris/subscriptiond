using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace youtubed.Persistence.Cosmos
{
    internal sealed class CosmosListDocument
    {
        public string Id { get; set; }
        public byte[] Token { get; set; }
        public string Title { get; set; }
        public decimal PlaybackRate { get; set; }
        public DateTimeOffset ExpiredAfter { get; set; }
        public DateOnly? ExpirationRenewedOn { get; set; }
        public IReadOnlyList<string> ChannelIds { get; set; } = Array.Empty<string>();
        public int Ttl { get; set; }

        [JsonPropertyName("_etag")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string ETag { get; set; }
    }

    internal sealed class CosmosChannelDocument
    {
        public string Id { get; set; }
        public string Url { get; set; }
        public string Title { get; set; }
        public string Thumbnail { get; set; }
        public string PlaylistId { get; set; }
        public DateTimeOffset StaleAfter { get; set; }
        public string Status { get; set; }
        public string StatusReason { get; set; }
        public DateTimeOffset? StatusUpdatedAt { get; set; }
        public IReadOnlyList<CosmosVideoDocument> Videos { get; set; } = Array.Empty<CosmosVideoDocument>();

        [JsonPropertyName("_etag")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string ETag { get; set; }
    }

    internal sealed class CosmosVideoDocument
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public long DurationTicks { get; set; }
        public DateTimeOffset PublishedAt { get; set; }
        public string Thumbnail { get; set; }
    }

    internal sealed class CosmosShareLinkDocument
    {
        public string Id { get; set; }
        public string ListId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset ExpiresAfter { get; set; }
        public DateTimeOffset? UsedAt { get; set; }
        public int Ttl { get; set; }

        [JsonPropertyName("_etag")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string ETag { get; set; }
    }
}
