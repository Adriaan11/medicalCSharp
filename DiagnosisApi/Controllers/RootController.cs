using Microsoft.AspNetCore.Mvc;

namespace DiagnosisApi.Controllers;

[ApiController]
[Route("/")]
public class RootController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new { status = "ok", message = "Root health check successful" });
    }
}
