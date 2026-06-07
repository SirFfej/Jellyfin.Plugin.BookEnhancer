using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.BookEnhancer.Controllers;

[ApiController]
[Route("Books/Config")]
public class ConfigController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ConfigController(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    [HttpPost("TestHardcover")]
    public async Task<ActionResult<TestResult>> TestHardcover([FromBody] TestKeyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            return Ok(new TestResult { Success = false, Message = "No API key provided." });

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {request.ApiKey}");
            var resp = await client.GetAsync("https://api.hardcover.app/v1/me", ct);

            if (resp.IsSuccessStatusCode)
                return Ok(new TestResult { Success = true, Message = "Hardcover API key is valid." });

            return Ok(new TestResult { Success = false, Message = $"Hardcover API returned {resp.StatusCode}. Check your key." });
        }
        catch (System.Exception ex)
        {
            return Ok(new TestResult { Success = false, Message = $"Connection failed: {ex.Message}" });
        }
    }

    [HttpPost("TestGoogleBooks")]
    public async Task<ActionResult<TestResult>> TestGoogleBooks([FromBody] TestKeyRequest request, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var url = string.IsNullOrWhiteSpace(request.ApiKey)
                ? "https://www.googleapis.com/books/v1/volumes?q=test&maxResults=1"
                : $"https://www.googleapis.com/books/v1/volumes?q=test&maxResults=1&key={request.ApiKey}";

            var resp = await client.GetAsync(url, ct);

            if (resp.IsSuccessStatusCode)
            {
                if (string.IsNullOrWhiteSpace(request.ApiKey))
                    return Ok(new TestResult { Success = true, Message = "Google Books (unauthenticated) is reachable." });
                return Ok(new TestResult { Success = true, Message = "Google Books API key is valid." });
            }

            return Ok(new TestResult { Success = false, Message = $"Google Books API returned {resp.StatusCode}." });
        }
        catch (System.Exception ex)
        {
            return Ok(new TestResult { Success = false, Message = $"Connection failed: {ex.Message}" });
        }
    }

    [HttpGet("Info")]
    public ActionResult<PluginInfoResult> GetInfo()
    {
        var plugin = Plugin.Instance;
        var version = plugin?.GetType().Assembly.GetName().Version?.ToString() ?? "unknown";

        return Ok(new PluginInfoResult
        {
            Version = version,
            Name = plugin?.Name ?? "BookEnhancers"
        });
    }
}

public class TestKeyRequest
{
    public string? ApiKey { get; set; }
}

public class TestResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class PluginInfoResult
{
    public string Version { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
