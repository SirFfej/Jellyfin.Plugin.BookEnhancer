using System.Xml.Linq;
using Jellyfin.Plugin.BookEnhancer.Models.Shared;

namespace Jellyfin.Plugin.BookEnhancer.Services;

/// <summary>
/// Loads a ComicInfo.xml template from disk or URL and applies it as fallback metadata.
/// </summary>
public static class ComicInfoTemplateLoader
{
    /// <summary>
    /// Loads the configured ComicInfo.xml template and converts it to <see cref="FileMetadata"/>.
    /// </summary>
    /// <returns>The template metadata, or <c>null</c> if no template is configured or the template cannot be loaded.</returns>
    public static FileMetadata? LoadTemplate()
    {
        var config = Plugin.Instance?.Configuration;
        var path = config?.ComicInfoTemplatePath;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            var doc = XDocument.Load(stream);
            return MapTemplateToMetadata(doc);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads the template from the configured URL and saves it to <paramref name="destinationPath"/>.
    /// </summary>
    /// <param name="url">The URL to download from.</param>
    /// <param name="destinationPath">The local path to save the template to.</param>
    /// <param name="httpClient">An <see cref="HttpClient"/> instance to use for the download.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the download succeeded; otherwise <c>false</c>.</returns>
    public static async Task<bool> DownloadTemplateAsync(string url, string destinationPath, HttpClient httpClient, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        try
        {
            var content = await httpClient.GetStringAsync(new Uri(url), ct).ConfigureAwait(false);
            var doc = XDocument.Parse(content);
            if (doc.Root?.Name.LocalName != "ComicInfo")
                return false;

            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            await File.WriteAllTextAsync(destinationPath, content, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Merges template values into <paramref name="target"/> for any fields that are null or empty.
    /// </summary>
    /// <param name="target">The metadata to merge template values into.</param>
    /// <param name="template">The template metadata.</param>
    public static void MergeTemplate(FileMetadata target, FileMetadata? template)
    {
        if (template is null)
            return;

        if (string.IsNullOrWhiteSpace(target.Publisher) && !string.IsNullOrWhiteSpace(template.Publisher))
            target.Publisher = template.Publisher;

        if (string.IsNullOrWhiteSpace(target.AgeRating) && !string.IsNullOrWhiteSpace(template.AgeRating))
            target.AgeRating = template.AgeRating;

        if (string.IsNullOrWhiteSpace(target.Manga) && !string.IsNullOrWhiteSpace(template.Manga))
            target.Manga = template.Manga;

        if (string.IsNullOrWhiteSpace(target.Format) && !string.IsNullOrWhiteSpace(template.Format))
            target.Format = template.Format;

        if (string.IsNullOrWhiteSpace(target.Language) && !string.IsNullOrWhiteSpace(template.Language))
            target.Language = template.Language;

        foreach (var genre in template.Genres)
        {
            if (!target.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase))
                target.Genres.Add(genre);
        }

        foreach (var tag in template.Tags)
        {
            if (!target.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                target.Tags.Add(tag);
        }
    }

    private static FileMetadata MapTemplateToMetadata(XDocument doc)
    {
        var root = doc.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "ComicInfo", StringComparison.OrdinalIgnoreCase))
            return new FileMetadata();

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
                    meta.Genres.Add(genre);
            }
        }

        var tags = GetElement(root, "Tags");
        if (!string.IsNullOrWhiteSpace(tags))
        {
            foreach (var tag in tags.Split([',', '/', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(tag))
                    meta.Tags.Add(tag);
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
