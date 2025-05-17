using System.Data.SqlClient;

namespace DiagnosisApi.Data;

public static class DatabaseHelper
{
    private const string ConnectionString = "Server=localhost\\SQLEXPRESS;Database=DiagnosisDB;Trusted_Connection=True;";

    public static async Task<bool> PasswordExistsAsync(string password)
    {
        using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        using var cmd = new SqlCommand("SELECT 1 FROM ApiPasswords WHERE PasswordText = @p", conn);
        cmd.Parameters.AddWithValue("@p", password);
        var result = await cmd.ExecuteScalarAsync();
        return result != null;
    }

    public static async Task LogRequestResponseAsync(string requestText, string responseJson)
    {
        using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        using var cmd = new SqlCommand("INSERT INTO ApiLogs (RequestText, ResponseJson) VALUES (@r, @j)", conn);
        cmd.Parameters.AddWithValue("@r", requestText);
        cmd.Parameters.AddWithValue("@j", responseJson);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<List<LogEntry>> GetLogsAsync()
    {
        var result = new List<LogEntry>();
        using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        using var cmd = new SqlCommand("SELECT LogId, RequestText, ResponseJson, RequestTime FROM ApiLogs ORDER BY LogId DESC", conn);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new LogEntry
            {
                LogId = reader.GetInt32(0),
                RequestText = reader.GetString(1),
                ResponseJson = reader.GetString(2),
                RequestTime = reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3)
            });
        }
        return result;
    }
}

public class LogEntry
{
    public int LogId { get; set; }
    public string RequestText { get; set; } = string.Empty;
    public string ResponseJson { get; set; } = string.Empty;
    public DateTime? RequestTime { get; set; }
}
