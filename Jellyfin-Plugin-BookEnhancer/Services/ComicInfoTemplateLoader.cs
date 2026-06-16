using System.Reflection;
using System.Xml.Linq;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;

namespace Jellyfin.Plugin.BookEnhancer.Services;

/// <summary>
/// Loads a ComicInfo.xml template and applies it as fallback metadata.
/// The plugin ships a default template that is written to the plugin data folder
/// so users can edit it without configuring a path.
/// </summary>
public static class ComicInfoTemplateLoader
{
    private const string EmbeddedResourceName = "Jellyfin.Plugin.BookEnhancer.Configuration.ComicInfoTemplate.xml";
    private const string TemplateFileName = "ComicInfoTemplate.xml";

    /// <summary>
    /// Gets the full path to the user-editable default template file.
    /// </summary>
    /// <returns>The default template path, or <c>null</c> when the plugin data path is unavailable.</returns>
    public static string? GetDefaultTemplatePath()
    {
        var dataPath = Plugin.DataPath;
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            return null;
        }

        return Path.Combine(dataPath, "plugins", "BookEnhancer", TemplateFileName);
    }

    /// <summary>
    /// Writes the built-in template to the default template path if it does not already exist.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The full path to the template file, or <c>null</c> if it could not be created.</returns>
    public static async Task<string?> EnsureDefaultTemplateExistsAsync(CancellationToken ct = default)
    {
        var path = GetDefaultTemplatePath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (File.Exists(path))
        {
            return path;
        }

        var content = await ReadEmbeddedTemplateAsync(ct).ConfigureAwait(false);
        if (content is null)
        {
            return null;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>
    /// Loads the ComicInfo.xml template and converts it to <see cref="FileMetadata"/>.
    /// The user-editable file at <see cref="GetDefaultTemplatePath"/> takes priority;
    /// if it does not exist, the embedded default template is used.
    /// </summary>
    /// <returns>The template metadata, or <c>null</c> if no template can be loaded.</returns>
    public static FileMetadata? LoadTemplate()
    {
        var path = GetDefaultTemplatePath();
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                using var stream = File.OpenRead(path);
                var doc = XDocument.Load(stream);
                return MapTemplateToMetadata(doc);
            }
            catch
            {
                // Fall back to the embedded template if the user file is unreadable.
            }
        }

        var embedded = LoadEmbeddedTemplate();
        return embedded;
    }

    /// <summary>
    /// Merges template values into <paramref name="target"/> for any fields that are null or empty.
    /// </summary>
    /// <param name="target">The metadata to merge template values into.</param>
    /// <param name="template">The template metadata.</param>
    public static void MergeTemplate(FileMetadata target, FileMetadata? template)
    {
        if (template is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(target.Publisher) && !string.IsNullOrWhiteSpace(template.Publisher))
        {
            target.Publisher = template.Publisher;
        }

        if (string.IsNullOrWhiteSpace(target.AgeRating) && !string.IsNullOrWhiteSpace(template.AgeRating))
        {
            target.AgeRating = template.AgeRating;
        }

        if (string.IsNullOrWhiteSpace(target.Manga) && !string.IsNullOrWhiteSpace(template.Manga))
        {
            target.Manga = template.Manga;
        }

        if (string.IsNullOrWhiteSpace(target.Format) && !string.IsNullOrWhiteSpace(template.Format))
            target.Format = template.Format;

        if (string.IsNullOrWhiteSpace(target.Language) && !string.IsNullOrWhiteSpace(template.Language))
        {
            target.Language = template.Language;
        }

        foreach (var genre in template.Genres)
        {
            if (!target.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase))
            {
                target.Genres.Add(genre);
            }
        }

        foreach (var tag in template.Tags)
        {
            if (!target.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                target.Tags.Add(tag);
            }
        }
    }

    private static FileMetadata? LoadEmbeddedTemplate()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName);
            if (stream is null)
            {
                return null;
            }

            var doc = XDocument.Load(stream);
            return MapTemplateToMetadata(doc);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> ReadEmbeddedTemplateAsync(CancellationToken ct)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName);
            if (stream is null)
            {
                return null;
            }

            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static FileMetadata MapTemplateToMetadata(XDocument doc)
    {
        var root = doc.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "ComicInfo", StringComparison.OrdinalIgnoreCase))
        {
            return new FileMetadata();
        }

        var meta = new FileMetadata
        {
            Publisher = GetElement(root, "Publisher"),
            AgeRating = GetElement(root, "AgeRating"),
            Manga = GetElement(root, "Manga"),
            Format = GetElement(root, "Format"),
            Language = GetElement(root, "Language")
        };

        var genres = GetElement(root, "Genre");
        if (!string.IsNullOrWhiteSpace(genres))
        {
            foreach (var genre in genres.Split([',', '/', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(genre))
                {
                    meta.Genres.Add(genre);
                }
            }
        }

        var tags = GetElement(root, "Tags");
        if (!string.IsNullOrWhiteSpace(tags))
        {
            foreach (var tag in tags.Split([',', '/', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    meta.Tags.Add(tag);
                }
            }
        }

        return meta;
    }

    private static string? GetElement(XElement root, string name)
    {
        var element = root.Element(name);
        var value = element?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
