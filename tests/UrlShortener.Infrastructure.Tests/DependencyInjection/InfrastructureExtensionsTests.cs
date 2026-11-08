using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using UrlShortener.Core.Analytics;
using UrlShortener.Core.Links;
using UrlShortener.Core.ShortCodes;
using UrlShortener.Core.Users;
using UrlShortener.Infrastructure.Analytics;
using UrlShortener.Infrastructure.Cosmos;
using UrlShortener.Infrastructure.InMemory;

namespace UrlShortener.Infrastructure.Tests.DependencyInjection;

public sealed class InfrastructureExtensionsTests
{
    private const string Secret = "a-test-secret-of-sufficient-length";
    private const string EmulatorConnectionString =
        "AccountEndpoint=http://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HostApplicationBuilder Builder(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }

    [Fact]
    public async Task InMemory_provider_wires_a_working_link_service()
    {
        var builder = Builder(new() { ["ShortCodes:Secret"] = Secret });
        builder.AddUrlShortenerCore();
        using var host = builder.Build();

        var result = await host.Services.GetRequiredService<LinkService>()
            .CreateAsync(new("https://example.com/", null, null), Ct);

        result.Status.ShouldBe(CreateLinkStatus.Created);
        host.Services.GetRequiredService<ILinkRepository>().ShouldBeOfType<InMemoryLinkRepository>();
        host.Services.GetRequiredService<UserService>().ShouldNotBeNull();
    }

    [Fact]
    public void Cosmos_provider_registers_the_cosmos_stores_without_connecting()
    {
        var builder = Builder(new()
        {
            ["ShortCodes:Secret"] = Secret,
            ["Storage:Provider"] = "Cosmos",
            ["ConnectionStrings:cosmos"] = EmulatorConnectionString,
        });
        builder.AddUrlShortenerCore();
        using var host = builder.Build();

        host.Services.GetRequiredService<ILinkRepository>().ShouldBeOfType<CosmosLinkRepository>();
        host.Services.GetRequiredService<IDestinationIndex>().ShouldBeOfType<CosmosDestinationIndex>();
        host.Services.GetRequiredService<IUserRepository>().ShouldBeOfType<CosmosUserRepository>();
        host.Services.GetRequiredService<IIdRangeAllocator>().ShouldBeOfType<CosmosIdRangeAllocator>();
        host.Services.GetRequiredService<IStatsStore>().ShouldBeOfType<CosmosStatsStore>();
        host.Services.GetServices<IHostedService>().ShouldContain(s => s is CosmosInitializer);
    }

    [Fact]
    public void Cosmos_client_uses_system_text_json_with_web_defaults()
    {
        var builder = Builder(new()
        {
            ["ShortCodes:Secret"] = Secret,
            ["Storage:Provider"] = "Cosmos",
            ["ConnectionStrings:cosmos"] = EmulatorConnectionString,
        });
        builder.AddUrlShortenerCore();
        using var host = builder.Build();

        var client = host.Services.GetRequiredService<Microsoft.Azure.Cosmos.CosmosClient>();
        client.ClientOptions.UseSystemTextJsonSerializerWithOptions.ShouldBeSameAs(CosmosJson.Options);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("too-short")]
    public async Task Startup_fails_fast_without_a_strong_short_code_secret(string? secret)
    {
        var builder = Builder(new() { ["ShortCodes:Secret"] = secret });
        builder.AddUrlShortenerCore();
        using var host = builder.Build();

        await Should.ThrowAsync<OptionsValidationException>(() => host.StartAsync(Ct));
    }

    [Fact]
    public void Direct_pipeline_writes_clicks_to_the_stats_store()
    {
        var builder = Builder(new() { ["ShortCodes:Secret"] = Secret });
        builder.AddUrlShortenerCore();
        builder.AddClickEventSink();
        using var host = builder.Build();

        host.Services.GetRequiredService<IClickEventSink>().ShouldBeOfType<DirectStatsSink>();
    }

    [Fact]
    public void EventHubs_pipeline_publishes_clicks_to_event_hubs()
    {
        var builder = Builder(new()
        {
            ["ShortCodes:Secret"] = Secret,
            ["Analytics:Pipeline"] = "EventHubs",
            ["ConnectionStrings:clicks"] = "Endpoint=sb://localhost/;SharedAccessKeyName=test;SharedAccessKey=dGVzdA==;EntityPath=clicks;UseDevelopmentEmulator=true",
        });
        builder.AddUrlShortenerCore();
        builder.AddClickEventSink();
        using var host = builder.Build();

        host.Services.GetRequiredService<IClickEventSink>().ShouldBeOfType<EventHubsClickSink>();
    }

    [Fact]
    public void Hosts_can_override_link_settings_and_time()
    {
        var settings = new LinkServiceSettings { BlockedHosts = new HashSet<string>(["sho.rt"]) };
        var builder = Builder(new() { ["ShortCodes:Secret"] = Secret });
        builder.Services.AddSingleton(settings);
        builder.AddUrlShortenerCore();
        using var host = builder.Build();

        host.Services.GetRequiredService<LinkServiceSettings>().ShouldBeSameAs(settings);
        host.Services.GetRequiredService<IShortCodeGenerator>().ShouldBeOfType<ShortCodeGenerator>();
    }
}
