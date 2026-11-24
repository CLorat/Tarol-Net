using System.Net;
using System.Text.Json;
using static Tarol.Changes.AspNetCore.Tests.TestApp;

namespace Tarol.Changes.AspNetCore.Tests;

/// <summary>TN-7 over HTTP: a DateOnly and a TimeOnly filter from the query string, a date sort, the OpenAPI formats.</summary>
public sealed class DateFilterEndpointTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Base = new(TimeProvider.System.GetUtcNow().Date.AddDays(10), TimeSpan.Zero);

    private TestApp _app = null!;
    private string _last = "";

    public async ValueTask InitializeAsync()
    {
        _app = await Start(endpoints: o => o.Queries.ReadAfterTimeout = TimeSpan.FromSeconds(10));
        foreach (var (title, at) in new[] { ("Late", Base.AddDays(2).AddHours(18)), ("Early", Base.AddHours(9)), ("Noon", Base.AddDays(2).AddHours(12)) })
        {
            var body = $$"""{"title":"{{title}}","reserve":100,"endsAt":"{{at:O}}"}""";
            var created = await _app.Post("/auctions", body, Alice);
            created.Status.ShouldBe(HttpStatusCode.Created, created.Body);
            _last = created.Response.Headers.GetValues(Positions.PositionHeader).Single();
        }
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private Task<Answer> List(string path) => _app.Get(path, Alice, h => h.Add(Positions.ReadAfterHeader, _last));

    private static string[] Titles(Answer answer)
        => [.. JsonElement.Parse(answer.Body).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!)];

    [Fact]
    public async Task A_calendar_day_and_a_time_of_day_filter_and_the_day_sorts()
    {
        var day = DateOnly.FromDateTime(Base.AddDays(2).UtcDateTime).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        Titles(await List($"/auctions/by-day?endsOn={day}&sort=Id")).Order(StringComparer.Ordinal).ShouldBe(["Late", "Noon"]);
        Titles(await List($"/auctions/by-day?endsOn={day}&endsAtTime=12:00")).ShouldBe(["Noon"]);
        Titles(await List("/auctions/by-day?endsAtTime=18:00:00")).ShouldBe(["Late"]);
        Titles(await List("/auctions/by-day"))[0].ShouldBe("Early");
        var row = JsonElement.Parse((await List("/auctions/by-day?endsAtTime=09:00")).Body).GetProperty("items")[0];
        row.GetProperty("endsOn").GetString().ShouldBe(DateOnly.FromDateTime(Base.UtcDateTime).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        row.GetProperty("endsAtTime").GetString().ShouldBe("09:00:00");
    }

    [Theory]
    [InlineData("endsOn=24.11.2026")]
    [InlineData("endsOn=2026-11-24T00:00:00Z")]
    [InlineData("endsOn=11/24/2026")]
    [InlineData("endsAtTime=9pm")]
    [InlineData("endsAtTime=12:00:00%2B01:00")]
    public async Task A_day_or_time_in_another_format_is_a_field_error(string query)
    {
        var list = await List("/auctions/by-day?" + query);

        list.Status.ShouldBe(HttpStatusCode.BadRequest, list.Body);
        list.Body.ShouldContain("\"Format\"");
    }

    [Fact]
    public async Task The_document_names_the_formats_date_and_time()
    {
        var get = JsonElement.Parse(await _app.Client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken))
            .GetProperty("paths").GetProperty("/auctions/by-day").GetProperty("get");

        var parameters = get.GetProperty("parameters").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!, p => p.GetProperty("schema"));
        parameters["endsOn"].GetProperty("format").GetString().ShouldBe("date");
        parameters["endsAtTime"].GetProperty("format").GetString().ShouldBe("time");
        parameters["sort"].GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ShouldBe(["EndsOn", "Id"]);
    }
}
