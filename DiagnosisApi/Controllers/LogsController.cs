using DiagnosisApi.Data;
using System.Linq;
using Microsoft.AspNetCore.Mvc;

namespace DiagnosisApi.Controllers;

[ApiController]
[Route("/logs")]
public class LogsController : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetLogs([FromQuery] string password)
    {
        if (!await DatabaseHelper.PasswordExistsAsync(password))
            return Unauthorized("Invalid password");
        try
        {
            var logs = await DatabaseHelper.GetLogsAsync();
            var results = logs.Select(l => new
            {
                logId = l.LogId,
                requestText = l.RequestText,
                responseJson = l.ResponseJson,
                requestTime = l.RequestTime?.ToString("yyyy-MM-dd HH:mm:ss")
            });
            return Ok(results);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Database error: {ex.Message}");
        }
    }
}
