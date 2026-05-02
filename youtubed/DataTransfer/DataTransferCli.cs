using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace youtubed.DataTransfer
{
    internal static class DataTransferCli
    {
        private const string CommandName = "transfer-data";

        private static readonly IReadOnlyList<TableTransfer> Transfers = new[]
        {
            new TableTransfer(
                "Channel",
                new[] { "Id", "Url", "Title", "Thumbnail", "PlaylistId", "StaleAfter", "VisibleAfter" }),
            new TableTransfer(
                "List",
                new[] { "Id", "Token", "Title", "PlaybackRate", "ExpiredAfter" }),
            new TableTransfer(
                "ShareLink",
                new[] { "Password", "ListId", "CreatedAt", "ExpiresAfter", "UsedAt" }),
            new TableTransfer(
                "ChannelVideo",
                new[] { "ChannelId", "Id", "Title", "Duration", "PublishedAt", "Thumbnail" }),
            new TableTransfer(
                "ListChannel",
                new[] { "ListId", "ChannelId" })
        };

        public static bool IsDataTransferCommand(string[] args)
        {
            return args.Length > 0 && string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase);
        }

        public static async Task<int> RunAsync(string[] args)
        {
            if (!TryParse(args.Skip(1).ToArray(), out var options, out var error))
            {
                Console.Error.WriteLine(error);
                WriteUsage(Console.Error);
                return 2;
            }

            if (options.DryRun)
            {
                await WriteDryRunAsync(options.SourceConnectionString);
                return 0;
            }

            await TransferAsync(options.SourceConnectionString, options.TargetConnectionString);
            return 0;
        }

        private static bool TryParse(string[] args, out DataTransferOptions options, out string error)
        {
            options = null;
            error = null;

            string sourceConnectionString = null;
            string targetConnectionString = null;
            var dryRun = false;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (IsOption(arg, "dry-run") || IsOption(arg, "dryrun"))
                {
                    dryRun = true;
                    continue;
                }

                if (TryReadOptionValue(args, ref i, "SourceConnectionString", out var source))
                {
                    sourceConnectionString = source;
                    continue;
                }

                if (TryReadOptionValue(args, ref i, "TargetConnectionString", out var target))
                {
                    targetConnectionString = target;
                    continue;
                }

                error = $"Unknown argument: {arg}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(sourceConnectionString))
            {
                error = "SourceConnectionString is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(targetConnectionString))
            {
                error = "TargetConnectionString is required.";
                return false;
            }

            options = new DataTransferOptions(sourceConnectionString, targetConnectionString, dryRun);
            return true;
        }

        private static bool TryReadOptionValue(string[] args, ref int index, string name, out string value)
        {
            var arg = args[index];
            var prefixedName = "--" + name;
            value = null;

            if (arg.StartsWith(prefixedName + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = arg.Substring(prefixedName.Length + 1);
                return true;
            }

            if (!string.Equals(arg, prefixedName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (index + 1 >= args.Length)
            {
                value = string.Empty;
                return true;
            }

            index++;
            value = args[index];
            return true;
        }

        private static bool IsOption(string arg, string name)
        {
            return string.Equals(arg, "--" + name, StringComparison.OrdinalIgnoreCase);
        }

        private static void WriteUsage(System.IO.TextWriter writer)
        {
            writer.WriteLine(
                "Usage: transfer-data --SourceConnectionString <connection-string> --TargetConnectionString <connection-string> [--dry-run]");
        }

        private static async Task WriteDryRunAsync(string sourceConnectionString)
        {
            await using var source = new SqlConnection(sourceConnectionString);
            await source.OpenAsync();

            Console.WriteLine("SET XACT_ABORT ON;");
            Console.WriteLine("BEGIN TRANSACTION;");
            Console.WriteLine();
            WriteDeleteStatements(Console.Out);

            foreach (var transfer in Transfers)
            {
                await foreach (var row in ReadRowsAsync(source, transfer))
                {
                    Console.WriteLine(CreateInsertStatement(transfer, row));
                }
            }

            Console.WriteLine("COMMIT TRANSACTION;");
        }

        private static async Task TransferAsync(string sourceConnectionString, string targetConnectionString)
        {
            await using var source = new SqlConnection(sourceConnectionString);
            await using var target = new SqlConnection(targetConnectionString);
            await source.OpenAsync();
            await target.OpenAsync();

            if (string.Equals(
                await GetDatabaseIdentityAsync(source),
                await GetDatabaseIdentityAsync(target),
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("SourceConnectionString and TargetConnectionString point to the same SQL database.");
            }

            await using var transaction = (SqlTransaction)await target.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                await ExecuteDeleteStatementsAsync(target, transaction);

                foreach (var transfer in Transfers)
                {
                    var inserted = 0;
                    await foreach (var row in ReadRowsAsync(source, transfer))
                    {
                        await InsertRowAsync(target, transaction, transfer, row);
                        inserted++;
                    }

                    Console.WriteLine($"Copied {inserted} row(s) into {QuoteName(transfer.TableName)}.");
                }

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        private static async Task<string> GetDatabaseIdentityAsync(SqlConnection connection)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT CONCAT(CONVERT(NVARCHAR(128), SERVERPROPERTY('ServerName')), N'|', DB_NAME());";
            return (string)await command.ExecuteScalarAsync();
        }

        private static async IAsyncEnumerable<IReadOnlyList<object>> ReadRowsAsync(SqlConnection source, TableTransfer transfer)
        {
            await using var command = source.CreateCommand();
            command.CommandText = $"SELECT {transfer.ColumnList} FROM {QuoteName(transfer.TableName)} ORDER BY {transfer.OrderByList};";

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                yield return values;
            }
        }

        private static async Task ExecuteDeleteStatementsAsync(SqlConnection target, SqlTransaction transaction)
        {
            await using var command = target.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = CreateDeleteSql();
            await command.ExecuteNonQueryAsync();
        }

        private static void WriteDeleteStatements(System.IO.TextWriter writer)
        {
            writer.Write(CreateDeleteSql());
        }

        private static string CreateDeleteSql()
        {
            var builder = new StringBuilder();
            foreach (var transfer in Transfers.Reverse())
            {
                builder.Append("DELETE FROM ");
                builder.Append(QuoteName(transfer.TableName));
                builder.AppendLine(";");
            }

            builder.AppendLine();
            return builder.ToString();
        }

        private static async Task InsertRowAsync(
            SqlConnection target,
            SqlTransaction transaction,
            TableTransfer transfer,
            IReadOnlyList<object> values)
        {
            await using var command = target.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = transfer.InsertCommandText;

            for (var i = 0; i < values.Count; i++)
            {
                command.Parameters.AddWithValue("@p" + i.ToString(CultureInfo.InvariantCulture), NormalizeDbValue(values[i]));
            }

            await command.ExecuteNonQueryAsync();
        }

        private static object NormalizeDbValue(object value)
        {
            return value == DBNull.Value ? DBNull.Value : value;
        }

        private static string CreateInsertStatement(TableTransfer transfer, IReadOnlyList<object> values)
        {
            var literals = string.Join(", ", values.Select(ToSqlLiteral));
            return $"INSERT INTO {QuoteName(transfer.TableName)} ({transfer.ColumnList}) VALUES ({literals});";
        }

        private static string ToSqlLiteral(object value)
        {
            if (value == null || value == DBNull.Value)
            {
                return "NULL";
            }

            return value switch
            {
                string text => "N'" + text.Replace("'", "''") + "'",
                Guid guid => "'" + guid.ToString("D", CultureInfo.InvariantCulture) + "'",
                byte[] bytes => "0x" + Convert.ToHexString(bytes),
                DateTimeOffset dateTimeOffset => "'" + dateTimeOffset.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture) + "'",
                DateTime dateTime => "'" + dateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "'",
                decimal number => number.ToString(CultureInfo.InvariantCulture),
                double number => number.ToString("R", CultureInfo.InvariantCulture),
                float number => number.ToString("R", CultureInfo.InvariantCulture),
                bool boolean => boolean ? "1" : "0",
                _ => Convert.ToString(value, CultureInfo.InvariantCulture)
            };
        }

        private static string QuoteName(string name)
        {
            return "[" + name.Replace("]", "]]") + "]";
        }

        private sealed class DataTransferOptions
        {
            public DataTransferOptions(string sourceConnectionString, string targetConnectionString, bool dryRun)
            {
                SourceConnectionString = sourceConnectionString;
                TargetConnectionString = targetConnectionString;
                DryRun = dryRun;
            }

            public string SourceConnectionString { get; }
            public string TargetConnectionString { get; }
            public bool DryRun { get; }
        }

        private sealed class TableTransfer
        {
            public TableTransfer(string tableName, IReadOnlyList<string> columns)
            {
                TableName = tableName;
                Columns = columns;
                ColumnList = string.Join(", ", Columns.Select(QuoteName));
                OrderByList = string.Join(", ", Columns.Select(QuoteName));
                InsertCommandText = CreateInsertCommandText();
            }

            public string TableName { get; }
            public IReadOnlyList<string> Columns { get; }
            public string ColumnList { get; }
            public string OrderByList { get; }
            public string InsertCommandText { get; }

            private string CreateInsertCommandText()
            {
                var parameters = string.Join(
                    ", ",
                    Columns.Select((_, index) => "@p" + index.ToString(CultureInfo.InvariantCulture)));
                return $"INSERT INTO {QuoteName(TableName)} ({ColumnList}) VALUES ({parameters});";
            }
        }
    }
}
