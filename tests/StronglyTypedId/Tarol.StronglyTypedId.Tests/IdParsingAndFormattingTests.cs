using System.Text;

namespace Tarol.StronglyTypedId.Tests;

public class IdParsingAndFormattingTests
{
    private static readonly Guid KnownGuid = Guid.Parse("018f2b3c-1234-7abc-9def-0123456789ab");

    [Fact]
    public void ToString_Defaults_To_D_Format()
    {
        Id<UserMarker>.From(KnownGuid).ToString().ShouldBe(KnownGuid.ToString("D"));
    }

    [Theory]
    [InlineData("D")]
    [InlineData("N")]
    [InlineData("B")]
    [InlineData("P")]
    public void ToString_Format_Specifiers_Delegate_To_Guid(string format)
    {
        Id<UserMarker>.From(KnownGuid).ToString(format, null).ShouldBe(KnownGuid.ToString(format));
    }

    [Fact]
    public void TryFormat_Char_Span_Is_Identical_To_ToString()
    {
        var id = Id<UserMarker>.From(KnownGuid);
        Span<char> buffer = stackalloc char[36];

        id.TryFormat(buffer, out var written, default, null).ShouldBeTrue();

        written.ShouldBe(36);
        new string(buffer[..written]).ShouldBe(id.ToString());
    }

    [Fact]
    public void TryFormat_Utf8_Span_Is_Identical_To_ToString()
    {
        var id = Id<UserMarker>.From(KnownGuid);
        Span<byte> buffer = stackalloc byte[36];

        id.TryFormat(buffer, out var written, default, null).ShouldBeTrue();

        Encoding.UTF8.GetString(buffer[..written]).ShouldBe(id.ToString());
    }

    [Fact]
    public void TryFormat_Reports_Failure_On_Undersized_Buffer()
    {
        Span<char> tooSmall = stackalloc char[10];
        Id<UserMarker>.From(KnownGuid).TryFormat(tooSmall, out var written, default, null).ShouldBeFalse();
        written.ShouldBe(0);
    }

    [Theory]
    [InlineData("018f2b3c-1234-7abc-9def-0123456789ab")] // D
    [InlineData("018f2b3c12347abc9def0123456789ab")] // N
    [InlineData("{018f2b3c-1234-7abc-9def-0123456789ab}")] // B
    public void Parse_Accepts_All_Guid_Formats(string input)
    {
        Id<UserMarker>.Parse(input).Value.ShouldBe(KnownGuid);
    }

    [Fact]
    public void Parse_Span_Overload_Works()
    {
        Id<UserMarker>.Parse(KnownGuid.ToString().AsSpan(), null).Value.ShouldBe(KnownGuid);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData("018f2b3c-1234")]
    public void TryParse_Returns_False_And_Empty_On_Garbage(string input)
    {
        Id<UserMarker>.TryParse(input, out var result).ShouldBeFalse();
        result.ShouldBe(Id<UserMarker>.Empty);
    }

    [Fact]
    public void TryParse_Handles_Null_Without_Throwing()
    {
        Id<UserMarker>.TryParse(null, out _).ShouldBeFalse();
    }

    [Fact]
    public void Parse_Throws_FormatException_On_Garbage()
    {
        Should.Throw<FormatException>(() => Id<UserMarker>.Parse("garbage"));
    }

    // These two prove the static abstract interface members resolve correctly — the
    // exact dispatch path ASP.NET Core minimal API model binding uses.
    [Fact]
    public void IParsable_Interface_Dispatch_Works()
    {
        ParseViaInterface<Id<UserMarker>>(KnownGuid.ToString()).Value.ShouldBe(KnownGuid);
    }

    [Fact]
    public void ISpanParsable_Interface_Dispatch_Works()
    {
        SpanParseViaInterface<Id<UserMarker>>(KnownGuid.ToString()).Value.ShouldBe(KnownGuid);
    }

    private static TSelf ParseViaInterface<TSelf>(string s) where TSelf : IParsable<TSelf>
    {
        return TSelf.Parse(s, null);
    }

    private static TSelf SpanParseViaInterface<TSelf>(ReadOnlySpan<char> s) where TSelf : ISpanParsable<TSelf>
    {
        return TSelf.Parse(s, null);
    }
}
