using Jellyfin.Plugin.BookEnhancer.Clients;
using Jellyfin.Plugin.BookEnhancer.Controllers;
using Jellyfin.Plugin.BookEnhancer.Logging;
using Jellyfin.Plugin.BookEnhancer.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookEnhancer;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ILoggerProvider>(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            return new BooksFileLoggerProvider(paths.LogDirectoryPath);
        });

        serviceCollection.AddMemoryCache();
        serviceCollection.AddHttpClient();

        serviceCollection.AddTransient<FileMetadataExtractor>();
        serviceCollection.AddTransient<HardcoverApiClient>();
        serviceCollection.AddTransient<GoogleBooksApiClient>();
        serviceCollection.AddTransient<OpenLibraryApiClient>();
        serviceCollection.AddSingleton<MetadataEnrichmentService>();

        serviceCollection.AddSingleton<BookGroupingService>(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            var logger = sp.GetRequiredService<ILogger<BookGroupingService>>();
            var dbDir = Path.Combine(paths.DataPath, "plugins", "BookEnhancer");
            return new BookGroupingService(dbDir, logger);
        });

        serviceCollection.AddSingleton<MetadataAggregationService>();

        serviceCollection.AddTransient<GroupingPostProcessingService>();
        serviceCollection.AddSingleton<LibraryOrganizationService>();
        serviceCollection.AddTransient<BookIngestionService>();
        serviceCollection.AddTransient<GroupingController>();
        serviceCollection.AddTransient<IngestionController>();
    }
}
