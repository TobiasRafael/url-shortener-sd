using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UrlShortener.Core.Analytics;
using UrlShortener.Core.Links;
using UrlShortener.Core.ShortCodes;
using UrlShortener.Core.Users;
using UrlShortener.Infrastructure.Analytics;
using UrlShortener.Infrastructure.Cosmos;
using UrlShortener.Infrastructure.InMemory;
using UrlShortener.Infrastructure.Options;

namespace Microsoft.Extensions.Hosting;

public static class InfrastructureExtensions
{
    /// <summary>
    /// Registers the domain services (<see cref="LinkService"/>, <see cref="UserService"/>, the
    /// short-code generator) and the storage provider chosen by <c>Storage:Provider</c>.
    /// </summary>
    public static IHostApplicationBuilder AddUrlShortenerCore(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddOptions<ShortCodeOptions>()
            .Bind(builder.Configuration.GetSection(ShortCodeOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton(new LinkServiceSettings());

        builder.Services.AddSingleton<IShortCodeGenerator>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ShortCodeOptions>>().Value;
            return new ShortCodeGenerator(sp.GetRequiredService<IIdRangeAllocator>(), options.Secret, options.BlockSize);
        });
        builder.Services.AddSingleton<LinkService>();
        builder.Services.AddSingleton<UserService>();

        return builder.AddUrlShortenerStorage();
    }

    /// <summary>Registers repositories and the stats store for the configured provider.</summary>
    public static IHostApplicationBuilder AddUrlShortenerStorage(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var section = builder.Configuration.GetSection(StorageOptions.SectionName);
        builder.Services.AddOptions<StorageOptions>().Bind(section).ValidateOnStart();
        var options = section.Get<StorageOptions>() ?? new StorageOptions();

        switch (options.Provider)
        {
            case StorageProvider.InMemory:
                builder.Services.TryAddSingleton<ILinkRepository, InMemoryLinkRepository>();
                builder.Services.TryAddSingleton<IDestinationIndex, InMemoryDestinationIndex>();
                builder.Services.TryAddSingleton<IUserRepository, InMemoryUserRepository>();
                builder.Services.TryAddSingleton<IIdRangeAllocator, InMemoryIdRangeAllocator>();
                builder.Services.TryAddSingleton<IStatsStore, InMemoryStatsStore>();
                break;

            case StorageProvider.Cosmos:
                builder.AddAzureCosmosClient(options.Cosmos.ConnectionName, configureClientOptions: CosmosJson.Configure);
                builder.Services.TryAddSingleton(sp => new CosmosContainers(
                    sp.GetRequiredService<Microsoft.Azure.Cosmos.CosmosClient>(),
                    sp.GetRequiredService<IOptions<StorageOptions>>().Value.Cosmos.DatabaseName));
                builder.Services.AddHostedService<CosmosInitializer>();
                builder.Services.TryAddSingleton<ILinkRepository, CosmosLinkRepository>();
                builder.Services.TryAddSingleton<IDestinationIndex, CosmosDestinationIndex>();
                builder.Services.TryAddSingleton<IUserRepository, CosmosUserRepository>();
                builder.Services.TryAddSingleton<IIdRangeAllocator, CosmosIdRangeAllocator>();
                builder.Services.TryAddSingleton<IStatsStore, CosmosStatsStore>();
                break;

            default:
                throw new InvalidOperationException($"Unknown storage provider '{options.Provider}'.");
        }

        return builder;
    }

    /// <summary>Registers the <see cref="IClickEventSink"/> chosen by <c>Analytics:Pipeline</c>.</summary>
    public static IHostApplicationBuilder AddClickEventSink(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var section = builder.Configuration.GetSection(AnalyticsOptions.SectionName);
        builder.Services.AddOptions<AnalyticsOptions>().Bind(section).ValidateDataAnnotations().ValidateOnStart();
        var options = section.Get<AnalyticsOptions>() ?? new AnalyticsOptions();

        switch (options.Pipeline)
        {
            case ClickPipeline.Direct:
                builder.Services.TryAddSingleton<IClickEventSink, DirectStatsSink>();
                break;

            case ClickPipeline.EventHubs:
                builder.AddAzureEventHubProducerClient(options.EventHubConnectionName);
                builder.Services.TryAddSingleton<IClickEventSink, EventHubsClickSink>();
                break;

            default:
                throw new InvalidOperationException($"Unknown click pipeline '{options.Pipeline}'.");
        }

        return builder;
    }
}
