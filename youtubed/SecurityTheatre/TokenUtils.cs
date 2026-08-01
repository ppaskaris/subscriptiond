using System;
using System.Security.Cryptography;

namespace youtubed.SecurityTheatre
{
    public static class TokenUtils
    {
        public static bool NotEqual(byte[] actual, byte[] expected)
        {
            if (actual == null || expected == null || actual.Length != expected.Length)
            {
                return true;
            }

            return !CryptographicOperations.FixedTimeEquals(actual, expected);
        }
    }
}
