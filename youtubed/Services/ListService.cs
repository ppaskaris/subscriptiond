using Dapper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using youtubed.Data;
using youtubed.Models;

namespace youtubed.Services
{
    public class ListService : IListService
    {
        private readonly IConnectionFactory _connectionFactory;

        public ListService(IConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task<ListModel> CreateListAsync()
        {
            var list = new ListModel
            {
                Id = Guid.NewGuid(),
                Token = CreateToken()
            };
            using (var connection = _connectionFactory.CreateConnection())
            {
                await connection.ExecuteAsync(
                    @"INSERT INTO List (Id, Token) VALUES (@Id, @Token)",
                    list);
            }
            return list;
        }

        public async Task<ListModel> GetListAsync(Guid id)
        {
            ListModel list;
            using (var connection = _connectionFactory.CreateConnection())
            {
                list = await connection.QueryFirstOrDefaultAsync<ListModel>(
                    @"SELECT Id, Token FROM list WHERE Id = @id",
                    new { id });
            }
            return list;
        }

        private byte[] CreateToken()
        {
            byte[] token = new byte[40];
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetNonZeroBytes(token);
            }
            return token;
        }
    }
}
