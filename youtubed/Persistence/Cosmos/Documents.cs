using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace youtubed.Persistence.Cosmos
{
    public sealed class CosmosListDocument
    {
        public string Id { get; set; }
        public byte[] Token { get; set; }
        public string Title { get; set; }
        public decimal PlaybackRate { get; set; }
        public DateTimeOffset ExpiredAfter { get; set; }
        public DateOnly? ExpirationRenewedOn { get; set; }
        public int? Ttl { get; set; }
        public IReadOnlyList<CosmosProjectedChannelDocument> Channels { get; set; } = Array.Empty<CosmosProjectedChannelDocument>();
        [JsonPropertyName("_etag")]
        public string ETag { get; set; }
    }

    public sealed class CosmosProjectedChannelDocument
    {
        public string Id { get; set; }
        public string Url { get; set; }
        public string Title { get; set; }
        public string Thumbnail { get; set; }
        public DateTimeOffset StaleAfter { get; set; }
        public string Status { get; set; }
        public string StatusReason { get; set; }
        public DateTimeOffset? StatusUpdatedAt { get; set; }
        public IReadOnlyList<CosmosVideoDocument> Videos { get; set; } = Array.Empty<CosmosVideoDocument>();
    }

    public sealed class CosmosVideoDocument
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public long DurationTicks { get; set; }
        public DateTimeOffset PublishedAt { get; set; }
        public string Thumbnail { get; set; }
    }

    public sealed class CosmosChannelDocument
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
        public IReadOnlyList<string> SubscribedListIds { get; set; } = Array.Empty<string>();
        public int SubscriptionCount { get; set; }
        public DateTimeOffset? OrphanedAfter { get; set; }
        public int? Ttl { get; set; }
        public IReadOnlyList<CosmosVideoDocument> Videos { get; set; } = Array.Empty<CosmosVideoDocument>();
        [JsonPropertyName("_etag")]
        public string ETag { get; set; }
    }

    public sealed class CosmosShareLinkDocument
    {
        public string Id { get; set; }
        public string ListId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset ExpiresAfter { get; set; }
        public DateTimeOffset? UsedAt { get; set; }
        public int? Ttl { get; set; }
        [JsonPropertyName("_etag")]
        public string ETag { get; set; }
    }

    public sealed class CosmosWorkerStateDocument
    {
        public const string SchedulerId = "scheduler";

        public string Id { get; set; } = SchedulerId;
        public DateTimeOffset? NextChannelRefreshAt { get; set; }
        public long ChannelRefreshForceCount { get; set; }
        public DateTimeOffset NextPurgeAt { get; set; }
        [JsonPropertyName("_etag")]
        public string ETag { get; set; }
    }
}
