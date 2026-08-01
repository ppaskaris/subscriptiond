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
        public int? Ttl { get; set; }
        public long MembershipVersion { get; set; }
        public bool MembershipRecoveryPending { get; set; }
        public DateTimeOffset? MembershipRecoveryDueAt { get; set; }
        public DateTimeOffset? MembershipRecoveryStartedAt { get; set; }
        public int MembershipRecoveryAttempt { get; set; }
        public bool MembershipRecoveryPoison { get; set; }
        public string MembershipRecoveryLastErrorClass { get; set; }
        public IReadOnlyList<CosmosProjectedChannelDocument> Channels { get; set; } = Array.Empty<CosmosProjectedChannelDocument>();
        [JsonPropertyName("_etag")]
        public string ETag { get; set; }
    }

    internal sealed class CosmosProjectedChannelDocument
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

    internal sealed class CosmosVideoDocument
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public long DurationTicks { get; set; }
        public DateTimeOffset PublishedAt { get; set; }
        public string Thumbnail { get; set; }
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
        public IReadOnlyList<string> SubscribedListIds { get; set; } = Array.Empty<string>();
        public int SubscriptionCount { get; set; }
        public long SubscriptionGeneration { get; set; }
        public DateTimeOffset? OrphanedAfter { get; set; }
        public int? Ttl { get; set; }
        public long ProjectionVersion { get; set; }
        public bool ProjectionRecoveryPending { get; set; }
        public DateTimeOffset? ProjectionRecoveryDueAt { get; set; }
        public DateTimeOffset? ProjectionRecoveryStartedAt { get; set; }
        public int ProjectionRecoveryAttempt { get; set; }
        public bool ProjectionRecoveryPoison { get; set; }
        public string ProjectionRecoveryLastErrorClass { get; set; }
        public long? ProjectionRecoveryProjectionVersion { get; set; }
        public long? ProjectionRecoverySubscriptionGeneration { get; set; }
        public string ProjectionRecoveryAfterListId { get; set; }
        public IReadOnlyList<CosmosVideoDocument> Videos { get; set; } = Array.Empty<CosmosVideoDocument>();
        [JsonPropertyName("_etag")]
        public string ETag { get; set; }
    }

    internal sealed class CosmosShareLinkDocument
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

    internal sealed class CosmosWorkerStateDocument
    {
        public const string SchedulerId = "scheduler";

        public string Id { get; set; } = SchedulerId;
        public DateTimeOffset? NextChannelRefreshAt { get; set; }
        public long ChannelRefreshForceCount { get; set; }
        public DateTimeOffset NextPurgeAt { get; set; }
        public DateTimeOffset NextConsistencyRecoveryAt { get; set; }
        public long ConsistencyRecoveryForceCount { get; set; }
        [JsonPropertyName("_etag")]
        public string ETag { get; set; }
    }

    internal sealed class CosmosRecoveryLifecycleDocument
    {
        public const string DocumentId = "lifecycle";

        public string Id { get; set; } = DocumentId;
        public string ListId { get; set; }
        public string Kind { get; set; } = "Lifecycle";
        public string State { get; set; } = "Active";
        public DateTimeOffset ExpiredAfter { get; set; }
        public DateTimeOffset NextCheckAt { get; set; }
        public int ActiveEdgeCount { get; set; }
        public long EdgeGeneration { get; set; }
        public string MembershipEdgeAfterChannelId { get; set; }
        public string MembershipEdgeAfterId { get; set; }
        public long? MembershipTraversalEdgeGeneration { get; set; }
        public long? MembershipVersionBeingRepaired { get; set; }
        public string CleanupEdgeAfterChannelId { get; set; }
        public string CleanupEdgeAfterId { get; set; }
        public long? CleanupTraversalEdgeGeneration { get; set; }
        public DateTimeOffset? MissingObservedAt { get; set; }
        public string Owner { get; set; }
        public DateTimeOffset? LeaseUntil { get; set; }
        public int Attempt { get; set; }
        public DateTimeOffset NextAttemptAt { get; set; }
        public string LastErrorClass { get; set; }
        [JsonIgnore]
        public bool LeaseTakenOver { get; set; }
        [JsonPropertyName("_etag")]
        public string ETag { get; set; }
    }

    internal sealed class CosmosRecoveryEdgeDocument
    {
        public string Id { get; set; }
        public string ListId { get; set; }
        public string Kind { get; set; } = "Edge";
        public string ChannelId { get; set; }
        public bool Active { get; set; } = true;
        public string State { get; set; } = "Candidate";
        public long Generation { get; set; }
        public string Owner { get; set; }
        public DateTimeOffset? LeaseUntil { get; set; }
        public int Attempt { get; set; }
        public DateTimeOffset? NextAttemptAt { get; set; }
        public long? LastObservedMembershipVersion { get; set; }
        public string LastErrorClass { get; set; }
        [JsonIgnore]
        public bool LeaseTakenOver { get; set; }
        [JsonPropertyName("_etag")]
        public string ETag { get; set; }
    }

    internal sealed class CosmosRecoveryCursorDocument
    {
        public string Id { get; set; }
        public string ListId { get; set; } = "__system";
        public string Kind { get; set; } = "Cursor";
        public string WorkKind { get; set; }
        public DateTimeOffset CycleNow { get; set; }
        public long CycleGeneration { get; set; }
        public DateTimeOffset? AfterDueAt { get; set; }
        public string AfterListId { get; set; }
        public string AfterId { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        [JsonPropertyName("_etag")]
        public string ETag { get; set; }
    }

    internal sealed class CosmosRecoveryTicketCursorDocument
    {
        public const string DocumentId = "cursor:work-kind-rotation";

        public string Id { get; set; } = DocumentId;
        public string ListId { get; set; } = "__system";
        public string Kind { get; set; } = "Cursor";
        public string NextStartingKind { get; set; } = "Membership";
        public long RotationGeneration { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        [JsonPropertyName("_etag")]
        public string ETag { get; set; }
    }
}
