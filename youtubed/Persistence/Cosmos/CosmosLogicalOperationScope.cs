using System;
using System.Collections.Generic;
using System.Threading;

namespace youtubed.Persistence.Cosmos
{
    internal sealed class CosmosLogicalOperationScope : IDisposable
    {
        internal const string Unattributed = "unattributed";
        internal const string ListPage = "list_page";
        internal const string ListCreate = "list_create";
        internal const string ListSettings = "list_settings";
        internal const string MembershipAdd = "membership_add";
        internal const string MembershipRemove = "membership_remove";
        internal const string ListDelete = "list_delete";
        internal const string ChannelRead = "channel_read";
        internal const string ChannelDiscovery = "channel_discovery";
        internal const string ChannelRefreshRead = "channel_refresh_read";
        internal const string ChannelRefreshWrite = "channel_refresh_write";
        internal const string ProjectionFanOut = "projection_fan_out";
        internal const string ShareCreate = "share_create";
        internal const string ShareList = "share_list";
        internal const string ShareDelete = "share_delete";
        internal const string ShareConsume = "share_consume";
        internal const string SchedulerRead = "scheduler_read";
        internal const string SchedulerWrite = "scheduler_write";
        internal const string Reconciliation = "reconciliation";
        internal const string ContainerInitialization = "container_initialization";

        private static readonly HashSet<string> AllowedOperations = new(
            StringComparer.Ordinal)
        {
            ListPage,
            ListCreate,
            ListSettings,
            MembershipAdd,
            MembershipRemove,
            ListDelete,
            ChannelRead,
            ChannelDiscovery,
            ChannelRefreshRead,
            ChannelRefreshWrite,
            ProjectionFanOut,
            ShareCreate,
            ShareList,
            ShareDelete,
            ShareConsume,
            SchedulerRead,
            SchedulerWrite,
            Reconciliation,
            ContainerInitialization
        };

        private static readonly AsyncLocal<CosmosLogicalOperationScope> CurrentScope = new();
        private readonly CosmosLogicalOperationScope _parent;

        private CosmosLogicalOperationScope(string operation)
        {
            Operation = operation;
            _parent = CurrentScope.Value;
            CurrentScope.Value = this;
        }

        internal string Operation { get; }

        internal static string Current => CurrentScope.Value?.Operation ?? Unattributed;

        internal static CosmosLogicalOperationScope Begin(string operation)
        {
            if (!AllowedOperations.Contains(operation))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(operation),
                    operation,
                    "Cosmos logical operations must use the fixed low-cardinality allowlist.");
            }

            return new CosmosLogicalOperationScope(operation);
        }

        public void Dispose()
        {
            if (ReferenceEquals(CurrentScope.Value, this))
            {
                CurrentScope.Value = _parent;
            }
        }
    }
}
