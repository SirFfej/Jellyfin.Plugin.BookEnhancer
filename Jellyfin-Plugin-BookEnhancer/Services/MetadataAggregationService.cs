using Jellyfin.Plugin.BookEnhancer.Models.Shared;

namespace Jellyfin.Plugin.BookEnhancer.Services;

public class MetadataAggregationService
{
    private static readonly string[] TextFormats = ["EPUB", "PDF", "Comic"];

    public FileMetadata Aggregate(IEnumerable<FileMetadata> formatMetadataList)
    {
        var list = formatMetadataList.ToList();
        if (list.Count == 0)
            return new FileMetadata();

        if (list.Count == 1)
            return list[0];

        var result = new FileMetadata();
        var textFormats = list.Where(m => TextFormats.Contains(m.FileFormat)).ToList();
        var audioFormats = list.Where(m => m.FileFormat == "Audio").ToList();

        var bestOverall = textFormats.Count > 0 ? textFormats[0] : list[0];

        result.Title = PickBest(list, m => m.Title);
        result.Subtitle = PickBest(list, m => m.Subtitle);
        result.Description = PickLongest(list, m => m.Description);
        result.Publisher = PickBest(list, m => m.Publisher);
        result.Isbn = PickBest(list, m => m.Isbn);
        result.Asin = PickBest(list, m => m.Asin);
        result.Language = PickBest(list, m => m.Language);
        result.SeriesName = PickBest(list, m => m.SeriesName);
        result.SeriesIndex = PickBest(list, m => m.SeriesIndex);
        result.SeriesNumber = PickBest(list, m => m.SeriesNumber);
        result.Volume = PickBest(list, m => m.Volume);
        result.AgeRating = PickBest(list, m => m.AgeRating);
        result.StoryArc = PickBest(list, m => m.StoryArc);
        result.Format = PickBest(list, m => m.Format);
        result.Manga = PickBest(list, m => m.Manga);

        result.PublishDate = PickBest(list, m => m.PublishDate);
        result.PublishYear = PickBest(list, m => m.PublishYear);

        result.PageCount = PickBest(list, m => m.PageCount);
        result.DurationMs = PickBest(list, m => m.DurationMs);

        result.Authors = MergeLists(list.SelectMany(m => m.Authors));
        result.Narrators = MergeLists(list.SelectMany(m => m.Narrators));
        result.Genres = MergeLists(list.SelectMany(m => m.Genres));
        result.Tags = MergeLists(list.SelectMany(m => m.Tags));

        result.ComicPeople = MergeComicPeople(list.SelectMany(m => m.ComicPeople));
        result.Chapters = PickLongestList(list, m => m.Chapters);

        var coverEntry = textFormats.Count > 0
            ? textFormats.FirstOrDefault(m => m.HasCover) ?? audioFormats.FirstOrDefault(m => m.HasCover)
            : list.FirstOrDefault(m => m.HasCover);

        if (coverEntry != null)
        {
            result.HasCover = coverEntry.HasCover;
            result.CoverBytes = coverEntry.CoverBytes;
            result.CoverMimeType = coverEntry.CoverMimeType;
            result.CoverUrl = coverEntry.CoverUrl;
        }

        result.FilePath = bestOverall.FilePath;
        result.FileFormat = "Grouped";

        return result;
    }

    private static T? PickBest<T>(List<FileMetadata> list, Func<FileMetadata, T?> selector)
        where T : class
    {
        foreach (var meta in list)
        {
            var val = selector(meta);
            if (val != null)
                return val;
        }

        return null;
    }

    private static T? PickBest<T>(List<FileMetadata> list, Func<FileMetadata, T?> selector)
        where T : struct
    {
        foreach (var meta in list)
        {
            var val = selector(meta);
            if (val.HasValue)
                return val;
        }

        return null;
    }

    private static string? PickLongest(List<FileMetadata> list, Func<FileMetadata, string?> selector)
    {
        return list
            .Select(selector)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .OrderByDescending(s => s!.Length)
            .FirstOrDefault();
    }

    private static List<T> MergeLists<T>(IEnumerable<T> items)
    {
        return items
            .Where(i => i != null && !(i is string s && string.IsNullOrWhiteSpace(s)))
            .Distinct()
            .ToList();
    }

    private static List<ChapterInfo> PickLongestList(List<FileMetadata> list, Func<FileMetadata, List<ChapterInfo>> selector)
    {
        return list
            .Select(selector)
            .OrderByDescending(l => l.Count)
            .FirstOrDefault() ?? new();
    }

    private static List<ComicPersonInfo> MergeComicPeople(IEnumerable<ComicPersonInfo> people)
    {
        return people
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .GroupBy(p => (p.Name, p.Role))
            .Select(g => g.First())
            .ToList();
    }
}
