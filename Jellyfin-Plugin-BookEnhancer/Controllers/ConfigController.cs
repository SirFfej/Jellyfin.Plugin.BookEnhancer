using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Jellyfin.Plugin.BookEnhancer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Controllers;

[ApiController]
[Authorize]
[Route("Books/Config")]
public class ConfigController : ControllerBase
{
    private static readonly Uri _hardcoverGraphQlEndpoint = new("https://api.hardcover.app/v1/graphql");
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
            var httpReq = new HttpRequestMessage(HttpMethod.Post, _hardcoverGraphQlEndpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            httpReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", request.ApiKey);

            var resp = await client.SendAsync(httpReq, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

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

            var resp = await client.GetAsync(url, ct).ConfigureAwait(false);

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

    [HttpPost("TestGrandComicsDb")]
    public async Task<ActionResult<TestResult>> TestGrandComicsDb([FromBody] TestBasicAuthRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return Ok(new TestResult { Success = false, Message = "Username and password required." });

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-BookEnhancer/1.0");
            var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{request.Username}:{request.Password}"));
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);

            var resp = await client.GetAsync("https://www.comics.org/api/series/name/Batman/", ct).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
            {
                _logger.LogInformation("Grand Comics Database API test succeeded");
                return Ok(new TestResult { Success = true, Message = "GCD credentials are valid." });
            }

            _logger.LogWarning("GCD API test failed: {StatusCode}", resp.StatusCode);
            return Ok(new TestResult { Success = false, Message = $"GCD API returned {resp.StatusCode}. Check credentials." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GCD API test connection failed");
            return Ok(new TestResult { Success = false, Message = $"Connection failed: {ex.Message}" });
        }
    }

    [HttpPost("TestConnectivity")]
    public async Task<ActionResult<ConnectivityResult>> TestConnectivity(CancellationToken ct)
    {
        var results = new List<ServiceConnectivity>();

        // Hardcover
        var hcResult = new ServiceConnectivity { Name = "Hardcover", Url = "https://api.hardcover.app/v1/graphql" };
        try
        {
            using var hcClient = _httpClientFactory.CreateClient();
            hcClient.Timeout = TimeSpan.FromSeconds(10);
            hcClient.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-BookEnhancer/1.0");
            var payload = JsonSerializer.Serialize(new { query = "query { me { id } }" });
            var hcReq = new HttpRequestMessage(HttpMethod.Post, _hardcoverGraphQlEndpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            var hcResp = await hcClient.SendAsync(hcReq, ct).ConfigureAwait(false);
            hcResult.Reachable = true;
            hcResult.StatusCode = (int)hcResp.StatusCode;
        }
        catch (Exception ex)
        {
            hcResult.Reachable = false;
            hcResult.Error = ex.GetType().Name + ": " + ex.Message;
        }
        results.Add(hcResult);

        // Google Books
        var gbResult = new ServiceConnectivity { Name = "Google Books", Url = "https://www.googleapis.com/books/v1/volumes?q=test&maxResults=1" };
        try
        {
            using var gbClient = _httpClientFactory.CreateClient();
            gbClient.Timeout = TimeSpan.FromSeconds(10);
            gbClient.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-BookEnhancer/1.0");
            var gbResp = await gbClient.GetAsync("https://www.googleapis.com/books/v1/volumes?q=test&maxResults=1", ct).ConfigureAwait(false);
            gbResult.Reachable = true;
            gbResult.StatusCode = (int)gbResp.StatusCode;
        }
        catch (Exception ex)
        {
            gbResult.Reachable = false;
            gbResult.Error = ex.GetType().Name + ": " + ex.Message;
        }
        results.Add(gbResult);

        // OpenLibrary
        var olResult = new ServiceConnectivity { Name = "OpenLibrary", Url = "https://openlibrary.org/api/books?bibkeys=ISBN:9780307272119&format=json" };
        try
        {
            using var olClient = _httpClientFactory.CreateClient();
            olClient.Timeout = TimeSpan.FromSeconds(10);
            olClient.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-BookEnhancer/1.0");
            var olResp = await olClient.GetAsync("https://openlibrary.org/api/books?bibkeys=ISBN:9780307272119&format=json", ct).ConfigureAwait(false);
            olResult.Reachable = true;
            olResult.StatusCode = (int)olResp.StatusCode;
        }
        catch (Exception ex)
        {
            olResult.Reachable = false;
            olResult.Error = ex.GetType().Name + ": " + ex.Message;
        }
        results.Add(olResult);

        // Comic Vine
        var cvResult = new ServiceConnectivity { Name = "Comic Vine", Url = "https://comicvine.gamespot.com/api" };
        try
        {
            using var cvClient = _httpClientFactory.CreateClient();
            cvClient.Timeout = TimeSpan.FromSeconds(10);
            cvClient.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-BookEnhancer/1.0");
            var cvResp = await cvClient.GetAsync("https://comicvine.gamespot.com/api", ct).ConfigureAwait(false);
            cvResult.Reachable = true;
            cvResult.StatusCode = (int)cvResp.StatusCode;
        }
        catch (Exception ex)
        {
            cvResult.Reachable = false;
            cvResult.Error = ex.GetType().Name + ": " + ex.Message;
        }
        results.Add(cvResult);

        // Metron
        var mtResult = new ServiceConnectivity { Name = "Metron", Url = "https://metron.cloud/api" };
        try
        {
            using var mtClient = _httpClientFactory.CreateClient();
            mtClient.Timeout = TimeSpan.FromSeconds(10);
            mtClient.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-BookEnhancer/1.0");
            var mtResp = await mtClient.GetAsync("https://metron.cloud/api", ct).ConfigureAwait(false);
            mtResult.Reachable = true;
            mtResult.StatusCode = (int)mtResp.StatusCode;
        }
        catch (Exception ex)
        {
            mtResult.Reachable = false;
            mtResult.Error = ex.GetType().Name + ": " + ex.Message;
        }
        results.Add(mtResult);

        // VerseDB
        var vdResult = new ServiceConnectivity { Name = "VerseDB", Url = "https://versedb.com/api" };
        try
        {
            using var vdClient = _httpClientFactory.CreateClient();
            vdClient.Timeout = TimeSpan.FromSeconds(20);
            vdClient.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-BookEnhancer/1.0");
            var vdResp = await vdClient.GetAsync("https://versedb.com/api", ct).ConfigureAwait(false);
            vdResult.Reachable = true;
            vdResult.StatusCode = (int)vdResp.StatusCode;
        }
        catch (Exception ex)
        {
            vdResult.Reachable = false;
            vdResult.Error = ex.GetType().Name + ": " + ex.Message;
        }
        results.Add(vdResult);

        // Grand Comics Database
        var gcdResult = new ServiceConnectivity { Name = "Grand Comics Database", Url = "https://www.comics.org/api/issue/1033778/" };
        try
        {
            using var gcdClient = _httpClientFactory.CreateClient();
            gcdClient.Timeout = TimeSpan.FromSeconds(20);
            gcdClient.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-BookEnhancer/1.0");
            var gcdResp = await gcdClient.GetAsync("https://www.comics.org/api/issue/1033778/", ct).ConfigureAwait(false);
            gcdResult.Reachable = true;
            gcdResult.StatusCode = (int)gcdResp.StatusCode;
        }
        catch (Exception ex)
        {
            gcdResult.Reachable = false;
            gcdResult.Error = ex.GetType().Name + ": " + ex.Message;
        }
        results.Add(gcdResult);

        return Ok(new ConnectivityResult { Results = results });
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

    [HttpPost("ConvertCbrToCbz")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Authorized endpoint used for user-configured library directories only.")]
    public async Task<ActionResult<CbrToCbzResult>> ConvertCbrToCbz(
        [FromBody] CbrToCbzRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ScanPath))
            return Ok(new CbrToCbzResult { Errors = 1, ErrorDetails = ["No scan path provided."] });

        var service = HttpContext.RequestServices.GetRequiredService<CbrToCbzService>();
        var result = await service.ConvertAsync(request.ScanPath, ct: ct).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpPost("ValidateDirectory")]
    [Authorize]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Authorized endpoint used for user-configured source directories only.")]
    public ActionResult<ValidateDirectoryResult> ValidateDirectory([FromBody] ValidateDirectoryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            return Ok(new ValidateDirectoryResult { Exists = false, Message = "No path provided." });

        try
        {
            var fullPath = Path.GetFullPath(request.Path);
            var exists = Directory.Exists(fullPath);
            if (exists)
                return Ok(new ValidateDirectoryResult { Exists = true, Message = "Directory exists." });

            if (request.CreateIfMissing)
            {
                Directory.CreateDirectory(fullPath);
                _logger.LogInformation("Created directory: {Path}", fullPath);
                return Ok(new ValidateDirectoryResult { Exists = true, Created = true, Message = "Directory created." });
            }

            return Ok(new ValidateDirectoryResult { Exists = false, Message = "Directory does not exist." });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to validate directory: {Path}", request.Path);
            return Ok(new ValidateDirectoryResult { Exists = false, Message = $"Error: {ex.Message}" });
        }
    }
}

public class TestKeyRequest
{
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }
}

public class TestBasicAuthRequest
{
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }
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

public class ValidateDirectoryRequest
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("createIfMissing")]
    public bool CreateIfMissing { get; set; }
}

public class ValidateDirectoryResult
{
    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    [JsonPropertyName("created")]
    public bool Created { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public class CbrToCbzRequest
{
    [JsonPropertyName("scanPath")]
    public string ScanPath { get; set; } = string.Empty;
}

public class ConnectivityResult
{
    [JsonPropertyName("results")]
    public List<ServiceConnectivity> Results { get; set; } = new();
}

public class ServiceConnectivity
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("reachable")]
    public bool Reachable { get; set; }

    [JsonPropertyName("statusCode")]
    public int? StatusCode { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
