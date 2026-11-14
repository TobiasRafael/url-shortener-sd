using Azure.Messaging.EventHubs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace UrlShortener.AnalyticsWorker.Tests;

public sealed class WorkerHostingTests
{
    // Emulator-style connection strings: enough to register the clients, nothing is contacted.
    private static readonly Dictionary<string, string?> Connections = new()
    {
        ["ConnectionStrings:clicks-analytics"] =
            "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;EntityPath=clicks;ConsumerGroup=analytics",
        ["ConnectionStrings:checkpoints"] = "UseDevelopmentStorage=true",
    };

    private static HostApplicationBuilder CreateBuilder(Dictionary<string, string?>? settings = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(Connections);
        if (settings is not null)
        {
            builder.Configuration.AddInMemoryCollection(settings);
        }
        return builder;
    }

    [Fact]
    public void Registers_the_processor_the_handler_and_the_hosted_service()
    {
        var builder = CreateBuilder();

        builder.AddClickConsumer();

        var services = builder.Services;
        services.ShouldContain(d => d.ServiceType == typeof(EventProcessorClient));
        services.ShouldContain(d => d.ServiceType == typeof(ClickBatchHandler));
        services.ShouldContain(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ClickProcessorService));
    }

    [Fact]
    public void Binds_the_consumer_options_from_configuration()
    {
        var builder = CreateBuilder(new()
        {
            ["ClickConsumer:FlushInterval"] = "00:00:05",
            ["ClickConsumer:MaxBatchClicks"] = "1234",
        });
        builder.AddClickConsumer();
        using var provider = builder.Services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ClickConsumerOptions>>().Value;

        options.FlushInterval.ShouldBe(TimeSpan.FromSeconds(5));
        options.MaxBatchClicks.ShouldBe(1234);
    }

    [Fact]
    public void Rejects_a_batch_size_of_zero_at_startup()
    {
        var builder = CreateBuilder(new() { ["ClickConsumer:MaxBatchClicks"] = "0" });
        builder.AddClickConsumer();
        using var provider = builder.Services.BuildServiceProvider();

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ClickConsumerOptions>>().Value);
    }
}
