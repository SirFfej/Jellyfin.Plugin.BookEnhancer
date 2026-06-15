using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Services;

public class TaskCheckpointService
{
    private readonly ILogger<TaskCheckpointService> _logger;
    private readonly string _checkpointDir;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public TaskCheckpointService(string checkpointDir, ILogger<TaskCheckpointService> logger)
    {
        _checkpointDir = checkpointDir;
        _logger = logger;
        Directory.CreateDirectory(_checkpointDir);
    }

    public TaskCheckpoint? LoadCheckpoint(string key, TimeSpan maxAge)
    {
        var path = GetCheckpointPath(key);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            var checkpoint = JsonSerializer.Deserialize<TaskCheckpoint>(json, JsonOptions);
            if (checkpoint is null)
                return null;

            var age = DateTime.UtcNow - checkpoint.TimestampUtc;
            if (age > maxAge)
            {
                _logger.LogInformation("Checkpoint {Key} is {Age:g} old; ignoring.", key, age);
                return null;
            }

            _logger.LogInformation("Loaded checkpoint {Key} from {TimestampUtc:u}; last processed {LastProcessedPath}", key, checkpoint.TimestampUtc, checkpoint.LastProcessedPath);
            return checkpoint;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load checkpoint {Key}", key);
            return null;
        }
    }

    public void SaveCheckpoint(string key, string lastProcessedPath)
    {
        var checkpoint = new TaskCheckpoint
        {
            Key = key,
            LastProcessedPath = lastProcessedPath,
            TimestampUtc = DateTime.UtcNow
        };

        var path = GetCheckpointPath(key);
        try
        {
            Directory.CreateDirectory(_checkpointDir);
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(checkpoint, JsonOptions));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save checkpoint {Key}", key);
        }
    }

    public void ClearCheckpoint(string key)
    {
        var path = GetCheckpointPath(key);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear checkpoint {Key}", key);
        }
    }

    private string GetCheckpointPath(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_checkpointDir, hash + ".json");
    }
}
