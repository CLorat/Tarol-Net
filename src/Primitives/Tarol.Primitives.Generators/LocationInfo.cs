using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Tarol.Primitives.Generators;

/// <summary>An equatable, symbol-free copy of a <see cref="Location" /> for use in cached pipeline models.</summary>
internal sealed record LocationInfo(string FilePath, TextSpan Span, LinePositionSpan LineSpan)
{
    public static LocationInfo? From(Location location)
        => location.SourceTree is null
            ? null
            : new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);

    public Location ToLocation() => Location.Create(FilePath, Span, LineSpan);
}
