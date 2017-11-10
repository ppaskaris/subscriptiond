using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace youtubed.SecurityTheatre
{
    public static class TokenUtils
    {
        public static bool NotEqual(string actual, string expected)
        {
            return !TimingSafeEqual(actual, expected);
        }

        private static bool TimingSafeEqual(string first, string second)
        {
            if (first.Length != second.Length)
            {
                return false;
            }
            uint acc = 0;
            for (int i = 0; i < first.Length; i++)
            {
                acc |= (uint)(first[i] ^ second[i]);
            }
            return acc == 0;
        }
    }
}
