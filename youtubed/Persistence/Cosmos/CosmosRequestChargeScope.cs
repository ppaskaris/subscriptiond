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

        internal static CosmosRequestChargeScope Begin() => new();

        internal static void Record(double requestCharge)
        {
            if (requestCharge > 0 && CurrentScope.Value != null)
            {
                CurrentScope.Value.RequestCharge += requestCharge;
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
