using System;
using Microsoft.AspNetCore.WebUtilities;
using youtubed.Domain;

namespace youtubed.Controllers
{
    internal static class ListRouteToken
    {
        public static string Encode(SubscriptionList list)
        {
            ArgumentNullException.ThrowIfNull(list);
            return WebEncoders.Base64UrlEncode(list.Token);
        }
    }
}
