using System.Globalization;
using System.Net;
using System.Text.Json;
using static Tarol.Changes.AspNetCore.Tests.TestApp;

namespace Tarol.Changes.AspNetCore.Tests;

/// <summary>TN-60 (Tatalu FB-F17.3-6, FB-F13.2-4): Tarol-Device and Tarol-Captured-At reach the operation context; an ended device is refused.</summary>
public sealed class CaptureTests : IAsyncLifetime
{
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() => _app = await Start();

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private static string Stamp(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    private Task<Answer> Create(string? device, string? capturedAt) => _app.Post("/auctions", Auction("Captured"), Alice, h =>
    {
        if (device is not null)
            h.Add("Tarol-Device", device);
        if (capturedAt is not null)
            h.Add("Tarol-Captured-At", capturedAt);
    });

    [Fact]
    public async Task The_device_and_the_capture_time_are_recorded_with_the_events()
    {
        var now = TimeProvider.System.GetUtcNow();
        var at = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero).AddHours(-3).ToOffset(TimeSpan.FromHours(1));

        var created = await Create("tab-7", Stamp(at));
        var plain = await Create(null, null);

        created.Status.ShouldBe(HttpStatusCode.Created, created.Body);
        var id = Guid.Parse(JsonElement.Parse(created.Body).GetProperty("id").GetString()!);
        var capture = _app.Store.Recorded.Single(r => r.StreamId == id).Context.Capture.ShouldNotBeNull();
        capture.Device.ShouldBe("tab-7");
        capture.At.ShouldBe(at);
        capture.At!.Value.Offset.ShouldBe(TimeSpan.Zero);                                     // stored in UTC
        var plainId = Guid.Parse(JsonElement.Parse(plain.Body).GetProperty("id").GetString()!);
        _app.Store.Recorded.Single(r => r.StreamId == plainId).Context.Capture.ShouldBeNull();
    }

    [Theory]
    [InlineData("future", "Future")]
    [InlineData("old", "TooOld")]
    [InlineData("yesterday", "Format")]
    [InlineData("2026-11-26T07:00:00", "Format")]                                              // no offset: whose 7 o'clock?
    public async Task A_capture_time_out_of_bounds_or_without_an_offset_is_a_field_error(string value, string code)
    {
        var now = TimeProvider.System.GetUtcNow();
        var stamp = value switch { "future" => Stamp(now.AddHours(1)), "old" => Stamp(now.AddDays(-40)), _ => value };

        var answer = await Create("tab-7", stamp);

        answer.Status.ShouldBe(HttpStatusCode.BadRequest, answer.Body);
        answer.Body.ShouldContain("\"Tarol-Captured-At\"");
        answer.Body.ShouldContain($"\"{code}\"");
    }

    [Fact]
    public async Task An_ended_device_is_refused_with_401_and_a_device_id_with_spaces_is_malformed()
    {
        var ended = await Create("lost-1", null);
        var spaced = await Create("tab 7", null);
        var read = await _app.Get("/auctions", Alice, h => h.Add("Tarol-Device", "lost-1"));

        ended.Status.ShouldBe(HttpStatusCode.Unauthorized, ended.Body);
        ended.Response.Headers.WwwAuthenticate.ToString().ShouldContain("error_description=\"Device.Ended\"");
        read.Status.ShouldBe(HttpStatusCode.Unauthorized);                                     // reads too: the gate is a middleware
        spaced.Status.ShouldBe(HttpStatusCode.BadRequest, spaced.Body);
    }
}
