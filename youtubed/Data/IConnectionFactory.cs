using Microsoft.Data.SqlClient;

namespace youtubed.Data
{
    public interface IConnectionFactory
    {
        SqlConnection CreateConnection();
    }
}
