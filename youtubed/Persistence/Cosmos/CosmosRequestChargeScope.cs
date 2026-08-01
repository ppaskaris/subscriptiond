using System;
using System.Threading;

namespace youtubed.Persistence.Cosmos
{
    internal sealed class CosmosRequestChargeScope : IDisposable
    {
        private static readonly AsyncLocal<CosmosRequestChargeScope> CurrentScope = new();
        private readonly CosmosRequestChargeScope _parent;

        private CosmosRequestChargeScope()
        {
            _parent = CurrentScope.Value;
            CurrentScope.Value = this;
        }

        internal double RequestCharge { get; private set; }

        internal int RequestCount { get; private set; }

        internal static CosmosRequestChargeScope Begin() => new();

        internal static void Record(double requestCharge)
        {
            for (var scope = CurrentScope.Value; scope != null; scope = scope._parent)
            {
                scope.RequestCount++;
                if (requestCharge > 0)
                {
                    scope.RequestCharge += requestCharge;
                }
            }
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
