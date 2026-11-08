using UrlShortener.Core.Analytics;

namespace UrlShortener.Core.Tests.Analytics;

public sealed class ClickAggregatorTests
{
    private static readonly DateTimeOffset Morning = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Returns_nothing_for_an_empty_batch() =>
        ClickAggregator.Aggregate([]).ShouldBeEmpty();

    [Fact]
    public void Merges_clicks_per_link_and_day()
    {
        ClickEvent[] clicks =
        [
            new("abc1234", Morning),
            new("abc1234", Morning.AddHours(2)),
            new("abc1234", Morning.AddMinutes(30)),
            new("xyz9876", Morning.AddHours(1)),
        ];

        var aggregates = ClickAggregator.Aggregate(clicks).ToDictionary(a => a.Code);

        aggregates.Count.ShouldBe(2);
        aggregates["abc1234"].ShouldBe(new ClickAggregate("abc1234", new DateOnly(2026, 11, 7), 3, Morning.AddHours(2)));
        aggregates["xyz9876"].Count.ShouldBe(1);
    }

    [Fact]
    public void Splits_a_batch_that_crosses_midnight_utc()
    {
        var beforeMidnight = new DateTimeOffset(2026, 11, 7, 23, 59, 59, TimeSpan.Zero);

        var aggregates = ClickAggregator.Aggregate([new("abc1234", beforeMidnight), new("abc1234", beforeMidnight.AddSeconds(2))]);

        aggregates.Select(a => a.Day).ShouldBe([new DateOnly(2026, 11, 7), new DateOnly(2026, 11, 8)], ignoreOrder: true);
        aggregates.ShouldAllBe(a => a.Count == 1);
    }

    [Fact]
    public void Buckets_by_utc_day_not_by_the_callers_offset()
    {
        // 2026-11-07 22:00 at UTC-5 is 2026-11-08 03:00 UTC.
        var lateEveningInNewYork = new DateTimeOffset(2026, 11, 7, 22, 0, 0, TimeSpan.FromHours(-5));

        ClickAggregator.Aggregate([new("abc1234", lateEveningInNewYork)]).Single().Day.ShouldBe(new DateOnly(2026, 11, 8));
    }

    [Fact]
    public void Codes_are_case_sensitive()
    {
        ClickAggregator.Aggregate([new("Promo", Morning), new("promo", Morning)]).Count.ShouldBe(2);
    }

    [Fact]
    public void Total_count_is_preserved_for_large_batches()
    {
        var clicks = Enumerable.Range(0, 10_000)
            .Select(i => new ClickEvent($"code{i % 37}", Morning.AddMinutes(i % 2_000)))
            .ToList();

        var aggregates = ClickAggregator.Aggregate(clicks);

        aggregates.Sum(a => a.Count).ShouldBe(10_000);
        aggregates.Count.ShouldBeLessThanOrEqualTo(37 * 2); // 2,000 minutes spans at most 2 days
    }
}
