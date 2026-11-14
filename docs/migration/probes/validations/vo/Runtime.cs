// Candidate runtime of Tarol.Validations (what generated code calls). Probe only.
using System.Numerics;
using System.Text.RegularExpressions;
using Tarol.Results;
namespace Probe;

public static class Codes { public const string Required = "Required", Range = "Range", Length = "Length", Pattern = "Pattern", Email = "Email", Count = "Count"; }

/// Collects field errors; the path is a stack of segments, turned into a string only when an error is added,
/// so the valid path allocates nothing.
public struct Errors
{
    private List<FieldError>? _list;
    private Segments _segments; private int _depth;
    private List<(string?, int)>? _deep;
    public void Enter(string name) => Push((name, -1));
    public void EnterIndex(int index) => Push((null, index));
    public void Exit() { _depth--; if (_depth >= Segments.Length) _deep!.RemoveAt(_deep.Count - 1); }
    private void Push((string?, int) s) { if (_depth < Segments.Length) _segments[_depth] = s; else (_deep ??= new()).Add(s); _depth++; }
    public void Add(string code, string? detail = null) => (_list ??= new(4)).Add(new FieldError(Path(null), code, detail));
    public void Add(string field, string code, string? detail) => (_list ??= new(4)).Add(new FieldError(Path(field), code, detail));
    private readonly string Path(string? leaf)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < _depth; i++) Append(sb, i < Segments.Length ? _segments[i] : _deep![i - Segments.Length]);
        if (leaf is not null) Append(sb, (leaf, -1));
        return sb.ToString();
    }
    private static void Append(System.Text.StringBuilder sb, (string? Name, int Index) s)
    { if (s.Name is null) sb.Append('[').Append(s.Index).Append(']'); else { if (sb.Length > 0) sb.Append('.'); sb.Append(s.Name); } }
    public readonly bool IsValid => _list is null;
    public readonly Invalid? ToInvalid() => _list is null ? null : new Invalid(_list);
}
[System.Runtime.CompilerServices.InlineArray(Length)]
public struct Segments { public const int Length = 8; private (string?, int) _e; }

public static class Rules
{
    public static bool InRange<T>(T v, T min, T max) where T : IComparisonOperators<T, T, bool> => v >= min && v <= max;
    public static bool Length(string? v, int min, int max) => v is not null && v.Length >= min && v.Length <= max;
    public static bool NotBlank(string? v) => !string.IsNullOrWhiteSpace(v);
    /// A regex timeout is a failure, not an exception.
    public static bool Matches(Regex regex, string? v) { if (v is null) return false; try { return regex.IsMatch(v); } catch (RegexMatchTimeoutException) { return false; } }
}

public interface IValidatable { void Validate(ref Errors errors); }
