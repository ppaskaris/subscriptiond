using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using youtubed.Models;

namespace youtubed.Services
{
    public class FakeListService : IListService
    {
        private static readonly byte[] _privateKey = new byte[] {
            0x5b, 0x9e, 0xb7, 0xdb, 0x3c, 0xb3, 0x8f, 0xe1, 0x63, 0x9e,
            0xb8, 0xec, 0xf9, 0x12, 0x23, 0xa2, 0xb5, 0x89, 0x00, 0xa3,
            0xc5, 0x7b, 0xe1, 0x68, 0x2c, 0xd5, 0xe5, 0x54, 0xd9, 0x06,
            0xa4, 0x8b, 0x94, 0xd5, 0x29, 0xae, 0x2e, 0xb9, 0x39, 0x8f
        };

        public ListModel GetListById(Guid id)
        {
            var list = new ListModel
            {
                Id = id,
                Token = ComputeToken(id)
            };
            return list;
        }

        public ListModel CreateList()
        {
            var id = Guid.NewGuid();
            var list = new ListModel
            {
                Id = id,
                Token = ComputeToken(id)
            };
            return list;
        }

        private String ComputeToken(Guid id)
        {
            var message = id.ToByteArray();
            var hmac = new HMACSHA256(_privateKey);
            var digest = hmac.ComputeHash(message);
            var token = Base64UrlEncoder.Encode(digest);
            return token;
        }
    }
}
