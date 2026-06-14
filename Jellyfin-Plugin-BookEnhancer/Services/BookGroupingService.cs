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
                GroupId TEXT NOT NULL REFERENCES book_groups(Id) ON DELETE CASCADE,
                FilePath TEXT NOT NULL,
                FormatType TEXT NOT NULL,
                JellyfinItemId TEXT,
                IsPrimary INTEGER NOT NULL DEFAULT 0,
                AddedAt TEXT NOT NULL,
                EnrichedBy TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_formats_group ON book_formats(GroupId);
            CREATE INDEX IF NOT EXISTS idx_groups_isbn ON book_groups(Isbn);
            CREATE INDEX IF NOT EXISTS idx_formats_path ON book_formats(FilePath);
            """;

        cmd.ExecuteNonQuery();

        MigrateAddEnrichedAt(conn);
        MigrateAddEnrichedBy(conn);
        MigrateUniqueFilePath(conn);
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

    private static void MigrateAddEnrichedBy(SqliteConnection conn)
    {
        var hasColumn = false;
        using var check = conn.CreateCommand();
        check.CommandText = "PRAGMA table_info(book_formats)";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            if (string.Equals(name, "EnrichedBy", StringComparison.OrdinalIgnoreCase))
            {
                hasColumn = true;
                break;
            }
        }

        if (!hasColumn)
        {
            using var migrate = conn.CreateCommand();
            migrate.CommandText = "ALTER TABLE book_formats ADD COLUMN EnrichedBy TEXT";
            migrate.ExecuteNonQuery();
        }
    }

    private static void MigrateUniqueFilePath(SqliteConnection conn)
    {
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_formats_path_unique'";
        var existing = Convert.ToInt32(check.ExecuteScalar());
        if (existing > 0)
            return;

        using var dedupe = conn.CreateCommand();
        dedupe.CommandText = """
            DELETE FROM book_formats
            WHERE Id NOT IN (
                SELECT Id FROM (
                    SELECT Id, ROW_NUMBER() OVER (PARTITION BY FilePath ORDER BY AddedAt ASC) AS rn
                    FROM book_formats
                )
                WHERE rn = 1
            )
            """;
        dedupe.ExecuteNonQuery();

        using var createIndex = conn.CreateCommand();
        createIndex.CommandText = "CREATE UNIQUE INDEX idx_formats_path_unique ON book_formats(FilePath)";
        createIndex.ExecuteNonQuery();
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
        return new SqliteConnection($"Data Source={_dbPath};Foreign Keys=True");
    }

    public BookGroup? GetGroupByIsbn(string? isbn)
    {
        if (string.IsNullOrWhiteSpace(isbn))
            return null;

        using var conn = CreateConnection();
        conn.Open();

        var groupId = GetGroupIdByIsbn(conn, isbn);
        return groupId is null ? null : LoadGroupWithFormats(conn, groupId);
    }

    private static string? GetGroupIdByIsbn(SqliteConnection conn, string? isbn)
    {
        if (string.IsNullOrWhiteSpace(isbn))
            return null;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id FROM book_groups WHERE Isbn = @Isbn";
        cmd.Parameters.AddWithValue("@Isbn", isbn);

        var result = cmd.ExecuteScalar();
        return result?.ToString();
    }

    public BookGroup? GetGroupByTitleAuthor(string? title, string? author)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        using var conn = CreateConnection();
        conn.Open();

        var groupId = GetGroupIdByTitleAuthor(conn, title, author);
        return groupId is null ? null : LoadGroupWithFormats(conn, groupId);
    }

    private static string? GetGroupIdByTitleAuthor(SqliteConnection conn, string? title, string? author)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        using var cmd = conn.CreateCommand();
        if (string.IsNullOrWhiteSpace(author))
        {
            cmd.CommandText = "SELECT Id FROM book_groups WHERE Title = @Title AND (Author IS NULL OR Author = '')";
        }
        else
        {
            cmd.CommandText = "SELECT Id FROM book_groups WHERE Title = @Title AND Author = @Author";
            cmd.Parameters.AddWithValue("@Author", author);
        }

        cmd.Parameters.AddWithValue("@Title", title);

        var result = cmd.ExecuteScalar();
        return result?.ToString();
    }

    private BookGroup? LoadGroupWithFormats(SqliteConnection conn, string groupId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Isbn, Title, Author, CreatedAt, UpdatedAt FROM book_groups WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", groupId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new BookGroup
        {
            Id = reader.GetString(0),
            Isbn = reader.IsDBNull(1) ? null : reader.GetString(1),
            Title = reader.IsDBNull(2) ? null : reader.GetString(2),
            Author = reader.IsDBNull(3) ? null : reader.GetString(3),
            CreatedAt = DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
            UpdatedAt = DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
            Formats = GetFormatsForGroup(conn, groupId)
        };
    }

    public BookGroup CreateGroup(FileMetadata metadata)
    {
        using var conn = CreateConnection();
        conn.Open();
        return CreateGroup(conn, metadata);
    }

    private static BookGroup CreateGroup(SqliteConnection conn, FileMetadata metadata)
    {
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

        return group;
    }
    public BookFormat AddFormatToGroup(string groupId, string filePath, string formatType, bool isPrimary = false)
    {
        using var conn = CreateConnection();
        conn.Open();
        return AddFormatToGroup(conn, groupId, filePath, formatType, isPrimary);
    }

    private static BookFormat AddFormatToGroup(SqliteConnection conn, string groupId, string filePath, string formatType, bool isPrimary = false)
    {
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT Id, GroupId, FilePath, FormatType, JellyfinItemId, IsPrimary, AddedAt, EnrichedAt FROM book_formats WHERE FilePath = @FilePath LIMIT 1";
        check.Parameters.AddWithValue("@FilePath", filePath);
        using var reader = check.ExecuteReader();
        if (reader.Read())
        {
            return ReadFormat(reader);
        }

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
        return GetEnrichmentCooldownInfo(filePath, cooldownDays).OnCooldown;
    }

    public (bool OnCooldown, string? EnrichedBy) GetEnrichmentCooldownInfo(string filePath, int cooldownDays)
    {
        if (cooldownDays <= 0)
            return (false, null);

        using var conn = CreateConnection();
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EnrichedAt, EnrichedBy FROM book_formats WHERE FilePath = @FilePath LIMIT 1";
        cmd.Parameters.AddWithValue("@FilePath", filePath);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return (false, null);

        var enrichedAtRaw = reader.IsDBNull(0) ? null : reader.GetString(0);
        var enrichedBy = reader.IsDBNull(1) ? null : reader.GetString(1);

        if (string.IsNullOrWhiteSpace(enrichedAtRaw))
            return (false, null);

        if (!DateTime.TryParse(enrichedAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var lastEnriched))
            return (false, null);

        var onCooldown = (DateTime.UtcNow - lastEnriched).TotalDays < cooldownDays;
        return (onCooldown, enrichedBy);
    }

    public void SetLastEnrichmentTime(string filePath, string? enrichedBy = null)
    {
        using var conn = CreateConnection();
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE book_formats SET EnrichedAt = @Now, EnrichedBy = @EnrichedBy WHERE FilePath = @FilePath";
        cmd.Parameters.AddWithValue("@FilePath", filePath);
        cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@EnrichedBy", enrichedBy ?? (object)DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void SetPrimaryFormat(string groupId, string formatId)
    {
        using var conn = CreateConnection();
        conn.Open();

        using var transaction = conn.BeginTransaction();
        try
        {
            using var clearCmd = conn.CreateCommand();
            clearCmd.Transaction = transaction;
            clearCmd.CommandText = "UPDATE book_formats SET IsPrimary = 0 WHERE GroupId = @GroupId";
            clearCmd.Parameters.AddWithValue("@GroupId", groupId);
            clearCmd.ExecuteNonQuery();

            using var setCmd = conn.CreateCommand();
            setCmd.Transaction = transaction;
            setCmd.CommandText = "UPDATE book_formats SET IsPrimary = 1 WHERE Id = @Id";
            setCmd.Parameters.AddWithValue("@Id", formatId);
            setCmd.ExecuteNonQuery();

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
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
        using var conn = CreateConnection();
        conn.Open();
        return GetFormatsForGroup(conn, groupId);
    }

    private static List<BookFormat> GetFormatsForGroup(SqliteConnection conn, string groupId)
    {
        var formats = new List<BookFormat>();

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

    public BookGroup? RegisterFile(string path, FileMetadata metadata, bool isPrimary, string strategy = "IsbnOnly")
    {
        using var conn = CreateConnection();
        conn.Open();

        using var transaction = conn.BeginTransaction();
        try
        {
            var existingGroupId = FindExistingGroupId(conn, metadata, strategy);

            string? groupId;
            if (existingGroupId is null && isPrimary)
            {
                groupId = CreateGroup(conn, metadata).Id;
            }
            else
            {
                groupId = existingGroupId;
            }

            if (groupId is not null)
            {
                AddFormatToGroup(conn, groupId, path, metadata.FileFormat, isPrimary);
                transaction.Commit();
                return LoadGroupWithFormats(conn, groupId);
            }

            transaction.Rollback();
            return null;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static string? FindExistingGroupId(SqliteConnection conn, FileMetadata metadata, string strategy)
    {
        var normalizedTitle = metadata.Title?.Trim() ?? string.Empty;
        var normalizedAuthor = metadata.Authors.Count > 0
            ? string.Join("; ", metadata.Authors).Trim()
            : string.Empty;

        if (strategy.Equals("IsbnOnly", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(metadata.Isbn)
                ? GetGroupIdByIsbn(conn, metadata.Isbn)
                : null;
        }

        if (strategy.Equals("TitleAuthor", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(normalizedTitle)
                ? GetGroupIdByTitleAuthor(conn, normalizedTitle, normalizedAuthor)
                : null;
        }

        if (strategy.Equals("Both", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(metadata.Isbn))
            {
                var byIsbn = GetGroupIdByIsbn(conn, metadata.Isbn);
                if (byIsbn is not null)
                    return byIsbn;
            }

            if (!string.IsNullOrWhiteSpace(normalizedTitle))
                return GetGroupIdByTitleAuthor(conn, normalizedTitle, normalizedAuthor);

            return null;
        }

        if (strategy.Equals("FileNamePrefix", StringComparison.OrdinalIgnoreCase))
        {
            var prefix = !string.IsNullOrWhiteSpace(metadata.SeriesName)
                ? metadata.SeriesName.Trim()
                : normalizedTitle;
            return !string.IsNullOrWhiteSpace(prefix)
                ? GetGroupIdByTitleAuthor(conn, prefix, string.Empty)
                : null;
        }

        return !string.IsNullOrWhiteSpace(metadata.Isbn)
            ? GetGroupIdByIsbn(conn, metadata.Isbn)
            : null;
    }

    private BookGroup? FindExistingGroup(FileMetadata metadata, string strategy)
    {
        var normalizedTitle = metadata.Title?.Trim() ?? string.Empty;
        var normalizedAuthor = metadata.Authors.Count > 0
            ? string.Join("; ", metadata.Authors).Trim()
            : string.Empty;

        if (strategy.Equals("IsbnOnly", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(metadata.Isbn)
                ? GetGroupByIsbn(metadata.Isbn)
                : null;
        }

        if (strategy.Equals("TitleAuthor", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(normalizedTitle)
                ? GetGroupByTitleAuthor(normalizedTitle, normalizedAuthor)
                : null;
        }

        if (strategy.Equals("Both", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(metadata.Isbn))
            {
                var byIsbn = GetGroupByIsbn(metadata.Isbn);
                if (byIsbn is not null)
                    return byIsbn;
            }

            if (!string.IsNullOrWhiteSpace(normalizedTitle))
                return GetGroupByTitleAuthor(normalizedTitle, normalizedAuthor);

            return null;
        }

        if (strategy.Equals("FileNamePrefix", StringComparison.OrdinalIgnoreCase))
        {
            var prefix = !string.IsNullOrWhiteSpace(metadata.SeriesName)
                ? metadata.SeriesName.Trim()
                : normalizedTitle;
            return !string.IsNullOrWhiteSpace(prefix)
                ? GetGroupByTitleAuthor(prefix, string.Empty)
                : null;
        }

        return !string.IsNullOrWhiteSpace(metadata.Isbn)
            ? GetGroupByIsbn(metadata.Isbn)
            : null;
    }

    public List<BookGroup> GetAllGroupsWithMultipleFormats()
    {
        var groups = new Dictionary<string, BookGroup>(StringComparer.OrdinalIgnoreCase);

        using var conn = CreateConnection();
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT g.Id, g.Isbn, g.Title, g.Author, g.CreatedAt, g.UpdatedAt,
                   f.Id, f.GroupId, f.FilePath, f.FormatType, f.JellyfinItemId, f.IsPrimary, f.AddedAt, f.EnrichedAt
            FROM book_groups g
            JOIN book_formats f ON f.GroupId = g.Id
            WHERE g.Id IN (
                SELECT GroupId FROM book_formats GROUP BY GroupId HAVING COUNT(*) > 1
            )
            ORDER BY g.Id, f.IsPrimary DESC, f.AddedAt ASC
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var groupId = reader.GetString(0);
            if (!groups.TryGetValue(groupId, out var group))
            {
                group = new BookGroup
                {
                    Id = groupId,
                    Isbn = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Title = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Author = reader.IsDBNull(3) ? null : reader.GetString(3),
                    CreatedAt = DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
                    UpdatedAt = DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
                    Formats = new List<BookFormat>()
                };
                groups[groupId] = group;
            }

            group.Formats.Add(ReadFormat(reader, 6));
        }

        return groups.Values.ToList();
    }

    public static int GetFormatPriority(string formatType)
    {
        var map = GetFormatPriorityMap();
        return map.TryGetValue(formatType, out var priority) ? priority : 100;
    }

    private static BookFormat ReadFormat(SqliteDataReader reader)
    {
        return ReadFormat(reader, 0);
    }

    private static BookFormat ReadFormat(SqliteDataReader reader, int offset)
    {
        return new BookFormat
        {
            Id = reader.GetString(offset + 0),
            GroupId = reader.GetString(offset + 1),
            FilePath = reader.GetString(offset + 2),
            FormatType = reader.GetString(offset + 3),
            JellyfinItemId = reader.IsDBNull(offset + 4) ? null : reader.GetString(offset + 4),
            IsPrimary = reader.GetInt32(offset + 5) == 1,
            AddedAt = DateTime.Parse(reader.GetString(offset + 6), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
            EnrichedAt = reader.IsDBNull(offset + 7) ? null : DateTime.Parse(reader.GetString(offset + 7), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
        };
    }
}
