using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Controllers;

[ApiController]
[Route("Books/Config")]
public class ConfigController : ControllerBase
{
    private static readonly Uri HardcoverGraphQlEndpoint = new("https://api.hardcover.app/v1/graphql");
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ConfigController> _logger;

    public ConfigController(IHttpClientFactory httpClientFactory, ILogger<ConfigController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [HttpPost("TestHardcover")]
    public async Task<ActionResult<TestResult>> TestHardcover([FromBody] TestKeyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            return Ok(new TestResult { Success = false, Message = "No API key provided." });

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-BookEnhancer/1.0");
            var payload = JsonSerializer.Serialize(new { query = "query { me { id } }" });
            var httpReq = new HttpRequestMessage(HttpMethod.Post, HardcoverGraphQlEndpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            httpReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", request.ApiKey);

            var resp = await client.SendAsync(httpReq, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (resp.IsSuccessStatusCode)
            {
                _logger.LogInformation("Hardcover API test succeeded");
                return Ok(new TestResult { Success = true, Message = "Hardcover API key is valid." });
            }

            _logger.LogWarning("Hardcover API test failed: {StatusCode} {Body}", resp.StatusCode, body);
            return Ok(new TestResult { Success = false, Message = $"Hardcover API returned {resp.StatusCode}. Check your key." });
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Hardcover API test connection failed");
            return Ok(new TestResult { Success = false, Message = $"Connection failed: {ex.Message}" });
        }
    }

    [HttpPost("TestGoogleBooks")]
    public async Task<ActionResult<TestResult>> TestGoogleBooks([FromBody] TestKeyRequest request, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-BookEnhancer/1.0");
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
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }
}

public class TestResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public class PluginInfoResult
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}
