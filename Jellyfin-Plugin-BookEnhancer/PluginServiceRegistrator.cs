using Jellyfin.Plugin.BookEnhancer.Clients;
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
    }
}
