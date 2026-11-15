using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using UrlShortener.Core.Analytics;

namespace UrlShortener.Infrastructure.Analytics;

/// <summary>
/// Wire format for clicks on Event Hubs. Each event carries a batch of clicks
/// (<c>{"v":1,"clicks":[{"c":"abc1234","t":1762516800000}]}</c>), so 8,000 redirects per second
/// become a few dozen events per second, not 8,000.
/// </summary>
public static class ClickEventSerializer
{
    public const int CurrentVersion = 1;
    public const string ContentType = "application/vnd.urlshortener.clicks+json";

    public static BinaryData Serialize(IEnumerable<ClickEvent> clicks)
    {
        ArgumentNullException.ThrowIfNull(clicks);

        var envelope = new ClickEnvelope(CurrentVersion, [.. clicks.Select(c => new ClickWire(c.Code, c.Timestamp.ToUnixTimeMilliseconds()))]);
        return BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(envelope, ClickJsonContext.Default.ClickEnvelope));
    }

    public static IReadOnlyList<ClickEvent> Deserialize(BinaryData body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var envelope = JsonSerializer.Deserialize(body.ToMemory().Span, ClickJsonContext.Default.ClickEnvelope)
            ?? throw new JsonException("Empty click envelope.");
        if (envelope.V != CurrentVersion)
        {
            throw new NotSupportedException($"Unsupported click envelope version {envelope.V}.");
        }

        // The serializer leaves missing properties null, so check the shape here: the consumer has
        // to be able to tell a malformed event (skip it) from a failing database (retry).
        if (envelope.Clicks is null || envelope.Clicks.Any(c => c is null || string.IsNullOrEmpty(c.C)))
        {
            throw new JsonException("Click envelope is missing its clicks or a click's code.");
        }

        return [.. envelope.Clicks.Select(c => new ClickEvent(c.C, DateTimeOffset.FromUnixTimeMilliseconds(c.T)))];
    }

    internal sealed record ClickEnvelope(int V, IReadOnlyList<ClickWire> Clicks);

    internal sealed record ClickWire(string C, long T);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ClickEventSerializer.ClickEnvelope))]
internal sealed partial class ClickJsonContext : JsonSerializerContext;

/// <summary>Aggregates in-process and writes straight to the stats store. Single-instance and dev setups.</summary>
public sealed class DirectStatsSink(IStatsStore store) : IClickEventSink
{
    public Task PublishAsync(IReadOnlyList<ClickEvent> clicks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clicks);
        return clicks.Count == 0
            ? Task.CompletedTask
            : store.ApplyAsync(ClickAggregator.Aggregate(clicks), cancellationToken);
    }
}

/// <summary>
/// Publishes clicks to Azure Event Hubs, where the analytics worker aggregates them. This takes
/// stats writes off the API, and a database slowdown can never back up into redirects.
/// </summary>
public sealed class EventHubsClickSink(EventHubProducerClient producer) : IClickEventSink
{
    /// <summary>About 500 × 40 bytes = 20 KB per event, well inside the 1 MB batch limit.</summary>
    public const int ClicksPerEvent = 500;

    public async Task PublishAsync(IReadOnlyList<ClickEvent> clicks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clicks);
        if (clicks.Count == 0)
        {
            return;
        }

        var batch = await producer.CreateBatchAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var chunk in clicks.Chunk(ClicksPerEvent))
            {
                var eventData = new EventData(ClickEventSerializer.Serialize(chunk)) { ContentType = ClickEventSerializer.ContentType };
                if (batch.TryAdd(eventData))
                {
                    continue;
                }

                if (batch.Count == 0)
                {
                    throw new InvalidOperationException("A single click event exceeds the Event Hubs batch size.");
                }

                await producer.SendAsync(batch, cancellationToken).ConfigureAwait(false);
                batch.Dispose();
                batch = await producer.CreateBatchAsync(cancellationToken).ConfigureAwait(false);

                if (!batch.TryAdd(eventData))
                {
                    throw new InvalidOperationException("A single click event exceeds the Event Hubs batch size.");
                }
            }

            if (batch.Count > 0)
            {
                await producer.SendAsync(batch, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            batch.Dispose();
        }
    }
}
