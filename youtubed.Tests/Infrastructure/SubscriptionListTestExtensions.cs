using Microsoft.AspNetCore.WebUtilities;
using youtubed.Domain;

namespace youtubed.Tests.Infrastructure
{
    internal static class SubscriptionListTestExtensions
    {
        public static string TokenString(this SubscriptionList list)
        {
            return WebEncoders.Base64UrlEncode(list.Token);
        }
    }
}
