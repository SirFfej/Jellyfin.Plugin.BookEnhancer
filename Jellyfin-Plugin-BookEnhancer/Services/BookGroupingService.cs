using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer.Services;

public class BookGroupingService
{
    private readonly string _dbPath;
    private readonly ILogger<BookGroupingService> _logger;

    public BookGroupingService(string dbDirectory, ILogger<BookGroupingService> logger)
    {
        _logger = logger;

        if (!Directory.Exists(dbDirectory))
            Directory.CreateDirectory(dbDirectory);

        _dbPath = Path.Combine(dbDirectory, "bookgroups.db");
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var conn = CreateConnection();
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS book_groups (
                Id TEXT PRIMARY KEY,
                Isbn TEXT,
                Title TEXT,
                Author TEXT,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS book_formats (
                Id TEXT PRIMARY KEY,
                GroupId TEXT NOT NULL REFERENCES book_groups(Id),
                FilePath TEXT NOT NULL,
                FormatType TEXT NOT NULL,
                JellyfinItemId TEXT,
                IsPrimary INTEGER NOT NULL DEFAULT 0,
                AddedAt TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_formats_group ON book_formats(GroupId);
            CREATE INDEX IF NOT EXISTS idx_groups_isbn ON book_groups(Isbn);
            CREATE INDEX IF NOT EXISTS idx_formats_path ON book_formats(FilePath);
            """;

        cmd.ExecuteNonQuery();

        MigrateAddEnrichedAt(conn);
    }

    private static void MigrateAddEnrichedAt(SqliteConnection conn)
    {
        var hasColumn = false;
        using var check = conn.CreateCommand();
        check.CommandText = "PRAGMA table_info(book_formats)";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            if (string.Equals(name, "EnrichedAt", StringComparison.OrdinalIgnoreCase))
            {
                hasColumn = true;
                break;
            }
        }

        if (!hasColumn)
        {
            using var migrate = conn.CreateCommand();
            migrate.CommandText = "ALTER TABLE book_formats ADD COLUMN EnrichedAt TEXT";
            migrate.ExecuteNonQuery();
        }
    }

    private static Dictionary<string, int> GetFormatPriorityMap()
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.FormatPriority is null || config.FormatPriority.Count == 0)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["EPUB"] = 0,
                ["MOBI"] = 1,
                ["PDF"] = 2,
                ["Comic"] = 3,
                ["Audio"] = 4,
            };
        }

        return config.FormatPriority
            .Where(e => !string.IsNullOrWhiteSpace(e.FormatName))
            .ToDictionary(e => e.FormatName, e => e.Priority, StringComparer.OrdinalIgnoreCase);
    }

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection($"Data Source={_dbPath}");
    }

    public BookGroup? GetGroupByIsbn(string? isbn)
    {
        if (string.IsNullOrWhiteSpace(isbn))
            return null;

        using var conn = CreateConnection();
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Isbn, Title, Author, CreatedAt, UpdatedAt FROM book_groups WHERE Isbn = @Isbn";
        cmd.Parameters.AddWithValue("@Isbn", isbn);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        var group = new BookGroup
        {
            Id = reader.GetString(0),
            Isbn = reader.IsDBNull(1) ? null : reader.GetString(1),
            Title = reader.IsDBNull(2) ? null : reader.GetString(2),
            Author = reader.IsDBNull(3) ? null : reader.GetString(3),
            CreatedAt = DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
            UpdatedAt = DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
            Formats = GetFormatsForGroup(reader.GetString(0))
        };

        return group;
    }

    public BookGroup CreateGroup(FileMetadata metadata)
    {
        using var conn = CreateConnection();
        conn.Open();

        var group = new BookGroup
        {
            Id = Guid.NewGuid().ToString("N"),
            Isbn = metadata.Isbn,
            Title = metadata.Title,
            Author = metadata.Authors.Count > 0 ? string.Join("; ", metadata.Authors) : null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO book_groups (Id, Isbn, Title, Author, CreatedAt, UpdatedAt)
            VALUES (@Id, @Isbn, @Title, @Author, @CreatedAt, @UpdatedAt)
            """;

        cmd.Parameters.AddWithValue("@Id", group.Id);
        cmd.Parameters.AddWithValue("@Isbn", (object?)group.Isbn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Title", (object?)group.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Author", (object?)group.Author ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAt", group.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@UpdatedAt", group.UpdatedAt.ToString("O"));

        cmd.ExecuteNonQuery();

        _logger.LogDebug("Created book group {GroupId} for ISBN {Isbn}", group.Id, group.Isbn);
        return group;
    }
    public BookFormat AddFormatToGroup(string groupId, string filePath, string formatType, bool isPrimary = false)
    {
        using var conn = CreateConnection();
        conn.Open();

        var format = new BookFormat
        {
            Id = Guid.NewGuid().ToString("N"),
            GroupId = groupId,
            FilePath = filePath,
            FormatType = formatType,
            IsPrimary = isPrimary,
            AddedAt = DateTime.UtcNow
        };

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO book_formats (Id, GroupId, FilePath, FormatType, JellyfinItemId, IsPrimary, AddedAt)
            VALUES (@Id, @GroupId, @FilePath, @FormatType, @JellyfinItemId, @IsPrimary, @AddedAt)
            """;

        cmd.Parameters.AddWithValue("@Id", format.Id);
        cmd.Parameters.AddWithValue("@GroupId", format.GroupId);
        cmd.Parameters.AddWithValue("@FilePath", format.FilePath);
        cmd.Parameters.AddWithValue("@FormatType", format.FormatType);
        cmd.Parameters.AddWithValue("@JellyfinItemId", (object?)format.JellyfinItemId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IsPrimary", format.IsPrimary ? 1 : 0);
        cmd.Parameters.AddWithValue("@AddedAt", format.AddedAt.ToString("O"));

        cmd.ExecuteNonQuery();

        return format;
    }

    public void UpdateFormatJellyfinId(string formatId, string jellyfinItemId)
    {
        using var conn = CreateConnection();
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE book_formats SET JellyfinItemId = @JellyfinItemId WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", formatId);
        cmd.Parameters.AddWithValue("@JellyfinItemId", jellyfinItemId);
        cmd.ExecuteNonQuery();
    }

    public int UpdateFormatPath(string oldPath, string newPath)
    {
        using var conn = CreateConnection();
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE book_formats SET FilePath = @NewPath WHERE FilePath = @OldPath";
        cmd.Parameters.AddWithValue("@OldPath", oldPath);
        cmd.Parameters.AddWithValue("@NewPath", newPath);
        var count = cmd.ExecuteNonQuery();

        if (count > 0)
            _logger.LogDebug("Updated format path in DB: {Old} -> {New}", oldPath, newPath);

        return count;
    }

    public DateTime? GetLastEnrichmentTime(string filePath)
    {
        using var conn = CreateConnection();
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EnrichedAt FROM book_formats WHERE FilePath = @FilePath LIMIT 1";
        cmd.Parameters.AddWithValue("@FilePath", filePath);

        var result = cmd.ExecuteScalar();
        if (result is null || result == DBNull.Value)
            return null;

        if (DateTime.TryParse(result.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            return dt;

        return null;
    }

    public bool IsEnrichmentOnCooldown(string filePath, int cooldownDays)
    {
        if (cooldownDays <= 0)
            return false;

        var lastEnriched = GetLastEnrichmentTime(filePath);
        return lastEnriched.HasValue && (DateTime.UtcNow - lastEnriched.Value).TotalDays < cooldownDays;
    }

    public void SetLastEnrichmentTime(string filePath)
    {
        using var conn = CreateConnection();
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE book_formats SET EnrichedAt = @Now WHERE FilePath = @FilePath";
        cmd.Parameters.AddWithValue("@FilePath", filePath);
        cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public void SetPrimaryFormat(string groupId, string formatId)
    {
        using var conn = CreateConnection();
        conn.Open();

        using var clearCmd = conn.CreateCommand();
        clearCmd.CommandText = "UPDATE book_formats SET IsPrimary = 0 WHERE GroupId = @GroupId";
        clearCmd.Parameters.AddWithValue("@GroupId", groupId);
        clearCmd.ExecuteNonQuery();

        using var setCmd = conn.CreateCommand();
        setCmd.CommandText = "UPDATE book_formats SET IsPrimary = 1 WHERE Id = @Id";
        setCmd.Parameters.AddWithValue("@Id", formatId);
        setCmd.ExecuteNonQuery();
    }

    public void RemoveFormat(string formatId)
    {
        using var conn = CreateConnection();
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM book_formats WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", formatId);
        cmd.ExecuteNonQuery();
    }

    public List<BookFormat> GetFormatsForGroup(string groupId)
    {
        var formats = new List<BookFormat>();

        using var conn = CreateConnection();
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, GroupId, FilePath, FormatType, JellyfinItemId, IsPrimary, AddedAt, EnrichedAt
            FROM book_formats
            WHERE GroupId = @GroupId
            ORDER BY IsPrimary DESC, AddedAt ASC
            """;

        cmd.Parameters.AddWithValue("@GroupId", groupId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            formats.Add(ReadFormat(reader));
        }

        return formats;
    }

    public List<BookFormat> GetAllFormats()
    {
        var formats = new List<BookFormat>();

        using var conn = CreateConnection();
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, GroupId, FilePath, FormatType, JellyfinItemId, IsPrimary, AddedAt, EnrichedAt
            FROM book_formats
            ORDER BY AddedAt ASC
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            formats.Add(ReadFormat(reader));
        }

        return formats;
    }

    public BookGroup? RegisterFile(string path, FileMetadata metadata, bool isPrimary)
    {
        var existingGroup = !string.IsNullOrWhiteSpace(metadata.Isbn)
            ? GetGroupByIsbn(metadata.Isbn)
            : null;

        if (existingGroup is null && isPrimary)
        {
            var group = CreateGroup(metadata);
            AddFormatToGroup(group.Id, path, metadata.FileFormat, isPrimary: true);
            return group;
        }

        if (existingGroup is not null)
        {
            AddFormatToGroup(existingGroup.Id, path, metadata.FileFormat, isPrimary: false);
            return existingGroup;
        }

        return null;
    }

    public List<BookGroup> GetAllGroupsWithMultipleFormats()
    {
        var groups = new List<BookGroup>();

        using var conn = CreateConnection();
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT g.Id, g.Isbn, g.Title, g.Author, g.CreatedAt, g.UpdatedAt
            FROM book_groups g
            WHERE (SELECT COUNT(*) FROM book_formats f WHERE f.GroupId = g.Id) > 1
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var group = new BookGroup
            {
                Id = reader.GetString(0),
                Isbn = reader.IsDBNull(1) ? null : reader.GetString(1),
                Title = reader.IsDBNull(2) ? null : reader.GetString(2),
                Author = reader.IsDBNull(3) ? null : reader.GetString(3),
                CreatedAt = DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
                UpdatedAt = DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)
            };

            group.Formats = GetFormatsForGroup(group.Id);
            groups.Add(group);
        }

        return groups;
    }

    public static int GetFormatPriority(string formatType)
    {
        var map = GetFormatPriorityMap();
        return map.TryGetValue(formatType, out var priority) ? priority : 100;
    }

    private static BookFormat ReadFormat(SqliteDataReader reader)
    {
        return new BookFormat
        {
            Id = reader.GetString(0),
            GroupId = reader.GetString(1),
            FilePath = reader.GetString(2),
            FormatType = reader.GetString(3),
            JellyfinItemId = reader.IsDBNull(4) ? null : reader.GetString(4),
            IsPrimary = reader.GetInt32(5) == 1,
            AddedAt = DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
            EnrichedAt = reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
        };
    }
}
