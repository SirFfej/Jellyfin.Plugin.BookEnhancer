using Jellyfin.Plugin.BookEnhancer.Clients;
using Jellyfin.Plugin.BookEnhancer.Controllers;
using Jellyfin.Plugin.BookEnhancer.Services;
using Jellyfin.Plugin.BookEnhancer.Services.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddMemoryCache();
        serviceCollection.AddHttpClient();

        serviceCollection.AddTransient<FileMetadataExtractor>();
        serviceCollection.AddTransient<HardcoverApiClient>();
        serviceCollection.AddTransient<GoogleBooksApiClient>();
        serviceCollection.AddTransient<OpenLibraryApiClient>();
        serviceCollection.AddTransient<ComicVineApiClient>();
        serviceCollection.AddTransient<MetronApiClient>();
        serviceCollection.AddTransient<VerseDbApiClient>();
        serviceCollection.AddTransient<GrandComicsDbApiClient>();
        serviceCollection.AddSingleton<ComicApiCache>();
        serviceCollection.AddSingleton<MetadataEnrichmentService>();

        serviceCollection.AddSingleton<BookGroupingService>(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            var logger = sp.GetRequiredService<ILogger<BookGroupingService>>();
            var dbDir = Path.Combine(paths.DataPath, "plugins", "BookEnhancer");
            return new BookGroupingService(dbDir, logger);
        });

        serviceCollection.AddSingleton<TaskCheckpointService>(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            var logger = sp.GetRequiredService<ILogger<TaskCheckpointService>>();
            var checkpointDir = Path.Combine(paths.DataPath, "plugins", "BookEnhancer", "checkpoints");
            return new TaskCheckpointService(checkpointDir, logger);
        });

        serviceCollection.AddTransient<GroupingPostProcessingService>();
        serviceCollection.AddSingleton<LibraryOrganizationService>();
        serviceCollection.AddTransient<BookIngestionService>();
        serviceCollection.AddTransient<LibraryCleanupService>();
        serviceCollection.AddTransient<CbrToCbzService>();
        serviceCollection.AddTransient<IFileMetadataWriter, FileMetadataWriter>();
        // Controllers are auto-discovered by Jellyfin via GetApiPluginAssemblies()

        serviceCollection.AddSingleton<IScheduledTask, IngestionScanTask>();
        serviceCollection.AddSingleton<IScheduledTask, GroupingProcessTask>();
        serviceCollection.AddSingleton<IScheduledTask, FullMaintenanceTask>();
        serviceCollection.AddSingleton<IScheduledTask, LibraryCleanupTask>();
        serviceCollection.AddSingleton<IScheduledTask, MetadataEnrichmentTask>();
        serviceCollection.AddSingleton<IScheduledTask, PostOrganizationEnrichmentTask>();
    }
}
