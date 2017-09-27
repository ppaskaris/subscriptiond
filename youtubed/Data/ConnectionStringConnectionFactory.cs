using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;

namespace youtubed.Data
{
    public class ConnectionStringConnectionFactory : IConnectionFactory
    {
        private readonly string _connectionString;

        public ConnectionStringConnectionFactory(string connectionString)
        {
            _connectionString = connectionString;
        }

        public SqlConnection CreateConnection()
        {
            return new SqlConnection(_connectionString);
        }
    }
}
