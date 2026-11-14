using Microsoft.Extensions.Logging.Abstractions;

namespace UrlShortener.AppHost.Tests;

/// <summary>
/// Checks the application model the AppHost builds, without starting anything: no Docker needed,
/// so these run everywhere and catch wiring mistakes in milliseconds.
/// </summary>
public sealed class AppModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<IDistributedApplicationTestingBuilder> CreateAsync(params string[] args) =>
        DistributedApplicationTestingBuilder.CreateAsync<Projects.UrlShortener_AppHost>(args, Ct);

    private static T Resource<T>(IDistributedApplicationTestingBuilder builder, string name) where T : IResource =>
        builder.Resources.OfType<T>().Where(r => r.Name == name).ShouldHaveSingleItem();

    /// <summary>The environment the resource would be deployed with, as manifest expressions.</summary>
    private static async Task<IReadOnlyDictionary<string, string>> PublishedEnvironmentAsync(IResource resource)
    {
        var configuration = await ExecutionConfigurationBuilder.Create(resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish), NullLogger.Instance, Ct);
        configuration.Exception.ShouldBeNull();
        return configuration.EnvironmentVariables.ToDictionary();
    }

    [Fact]
    public async Task The_api_gets_cosmos_redis_and_the_short_code_secret()
    {
        await using var builder = await CreateAsync();
        var api = Resource<ProjectResource>(builder, "api");

        var env = await PublishedEnvironmentAsync(api);

        env.ShouldContainKeyAndValue("Storage__Provider", "Cosmos");
        env.ShouldContainKeyAndValue("ShortCodes__Secret", "{shortcode-secret.value}");
        env.ShouldContainKey("ConnectionStrings__cosmos");
        env.ShouldContainKey("ConnectionStrings__cache");
    }

    [Fact]
    public async Task The_api_publishes_clicks_to_the_clicks_event_hub()
    {
        await using var builder = await CreateAsync();
        var api = Resource<ProjectResource>(builder, "api");

        var env = await PublishedEnvironmentAsync(api);

        env.ShouldContainKeyAndValue("Analytics__Pipeline", "EventHubs");
        // AnalyticsOptions.EventHubConnectionName defaults to "clicks", the hub resource's name.
        env.ShouldContainKey("ConnectionStrings__clicks");
    }

    [Fact]
    public async Task The_api_starts_only_after_its_dependencies_and_is_reachable_from_outside()
    {
        await using var builder = await CreateAsync();
        var api = Resource<ProjectResource>(builder, "api");

        api.TryGetAnnotationsOfType<WaitAnnotation>(out var waits).ShouldBeTrue();
        waits.Select(w => w.Resource.Name).ShouldBe(["cosmos", "cache", "eventhubs"], ignoreOrder: true);
        api.Annotations.OfType<EndpointAnnotation>().ShouldContain(e => e.IsExternal);
        api.Annotations.OfType<HealthCheckAnnotation>().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task The_worker_reads_its_consumer_group_checkpoints_to_blobs_and_writes_stats_to_cosmos()
    {
        await using var builder = await CreateAsync();
        var worker = Resource<ProjectResource>(builder, "analytics-worker");

        var env = await PublishedEnvironmentAsync(worker);

        env.ShouldContainKeyAndValue("Storage__Provider", "Cosmos");
        // The default connection names in ClickConsumerOptions and CosmosStorageOptions.
        env.ShouldContainKey("ConnectionStrings__clicks-analytics");
        env.ShouldContainKey("ConnectionStrings__checkpoints");
        env.ShouldContainKey("ConnectionStrings__cosmos");
        // It never generates codes, so it gets no short-code secret.
        env.ShouldNotContainKey("ShortCodes__Secret");
    }

    [Fact]
    public async Task The_worker_starts_after_its_dependencies_and_exposes_nothing()
    {
        await using var builder = await CreateAsync();
        var worker = Resource<ProjectResource>(builder, "analytics-worker");

        worker.TryGetAnnotationsOfType<WaitAnnotation>(out var waits).ShouldBeTrue();
        // Waiting for the blob service also waits for the storage account it belongs to.
        waits.Select(w => w.Resource.Name).ShouldBe(["eventhubs", "storage", "checkpoints", "cosmos"], ignoreOrder: true);
        worker.Annotations.OfType<EndpointAnnotation>().ShouldNotContain(e => e.IsExternal);
    }

    [Fact]
    public async Task Clicks_flow_through_a_hub_with_a_dedicated_consumer_group()
    {
        await using var builder = await CreateAsync();
        var eventHubs = Resource<AzureEventHubsResource>(builder, "eventhubs");
        var hub = Resource<AzureEventHubResource>(builder, "clicks");
        var group = Resource<AzureEventHubConsumerGroupResource>(builder, "clicks-analytics");

        eventHubs.IsEmulator.ShouldBeTrue();
        hub.HubName.ShouldBe("clicks");
        group.Parent.ShouldBeSameAs(hub);
        group.ConsumerGroupName.ShouldBe("analytics");
    }

    [Fact]
    public async Task Checkpoints_live_in_the_storage_emulator()
    {
        await using var builder = await CreateAsync();
        var storage = Resource<AzureStorageResource>(builder, "storage");
        var blobs = Resource<AzureBlobStorageResource>(builder, "checkpoints");

        storage.IsEmulator.ShouldBeTrue();
        blobs.Parent.ShouldBeSameAs(storage);
    }

    [Fact]
    public async Task Locally_cosmos_is_the_linux_vnext_emulator()
    {
        await using var builder = await CreateAsync();
        var cosmos = Resource<AzureCosmosDBResource>(builder, "cosmos");

        cosmos.IsEmulator.ShouldBeTrue();
        cosmos.TryGetLastAnnotation<ContainerImageAnnotation>(out var image).ShouldBeTrue();
        image.Tag.ShouldNotBeNull().ShouldContain("vnext");
    }

    [Fact]
    public async Task The_emulator_keeps_its_data_between_local_runs()
    {
        await using var builder = await CreateAsync();
        var cosmos = Resource<AzureCosmosDBResource>(builder, "cosmos");

        cosmos.TryGetLastAnnotation<ContainerLifetimeAnnotation>(out var lifetime).ShouldBeTrue();
        lifetime.Lifetime.ShouldBe(ContainerLifetime.Persistent);
        cosmos.Annotations.OfType<ContainerMountAnnotation>().ShouldContain(m => m.Type == ContainerMountType.Volume);
    }

    [Fact]
    public async Task Tests_can_ask_for_throwaway_containers()
    {
        await using var builder = await CreateAsync("AppHost:PersistentContainers=false");
        var cosmos = Resource<AzureCosmosDBResource>(builder, "cosmos");

        cosmos.Annotations.OfType<ContainerLifetimeAnnotation>()
            .ShouldNotContain(l => l.Lifetime == ContainerLifetime.Persistent);
        cosmos.Annotations.OfType<ContainerMountAnnotation>().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_short_code_secret_is_a_generated_secret_so_nobody_has_to_invent_one()
    {
        await using var builder = await CreateAsync();
        var secret = Resource<ParameterResource>(builder, "shortcode-secret");

        secret.Secret.ShouldBeTrue();
        // Generated on first run and persisted to user secrets, so the code permutation stays the same
        // across runs. That the value satisfies ShortCodeOptions is proven by the API starting in RunningAppTests.
        secret.Default.ShouldNotBeNull();
    }
}
