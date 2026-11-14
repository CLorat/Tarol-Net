using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Tarol.Results;

namespace Tarol.Validations;

/// <summary>
///     Collects the field errors of one validation run and tracks the current input path (<c>Lines[2].Quantity</c>).
/// </summary>
/// <remarks>
///     <para>
///         A mutable struct: create it with <see langword="default" />, pass it by <see langword="ref" /> and read it with
///         <see cref="ToInvalid" />. A copy does not share errors with the original.
///     </para>
///     <para>
///         The path is a stack of segments (<see cref="Enter" />, <see cref="EnterIndex" />, <see cref="Exit" />) held
///         inline; it becomes a string only when an error is added, so a run without errors allocates nothing. Paths use the
///         C# member names; the web edge converts them to its JSON naming policy.
///     </para>
/// </remarks>
public struct ValidationErrors
{
    private const int InlineDepth = 8;

    private List<FieldError>? _errors;
    private Segments _segments;
    private List<Segment>? _deep;
    private int _depth;

    /// <summary>Whether no error has been added.</summary>
    public readonly bool IsValid => _errors is null;

    /// <summary>The number of errors added so far.</summary>
    public readonly int Count => _errors?.Count ?? 0;

    /// <summary>The current path, as it would appear in an error added now (empty at the root).</summary>
    public readonly string CurrentPath => BuildPath(null);

    /// <summary>Descends into the member <paramref name="member" /> (<c>Price</c> → <c>Price.Amount</c>).</summary>
    /// <param name="member">The member name.</param>
    /// <exception cref="ArgumentException"><paramref name="member" /> is <see langword="null" /> or empty.</exception>
    public void Enter(string member)
    {
        ArgumentException.ThrowIfNullOrEmpty(member);
        Push(new Segment(member, -1));
    }

    /// <summary>Descends into the element <paramref name="index" /> of the current collection (<c>Lines</c> → <c>Lines[2]</c>).</summary>
    /// <param name="index">The zero-based index.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index" /> is negative.</exception>
    public void EnterIndex(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        Push(new Segment(null, index));
    }

    /// <summary>Leaves the segment entered last.</summary>
    /// <exception cref="InvalidOperationException">No segment is entered.</exception>
    public void Exit()
    {
        if (_depth == 0)
            throw new InvalidOperationException("Exit() was called more often than Enter()/EnterIndex().");

        _depth--;
        if (_depth >= InlineDepth)
            _deep!.RemoveAt(_deep.Count - 1);
    }

    /// <summary>Adds an error at the current path.</summary>
    /// <param name="code">The rule's code, e.g. <see cref="ValidationCodes.Range" />.</param>
    /// <param name="detail">An optional detail, e.g. the limits <c>"1..100"</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="code" /> is <see langword="null" /> or empty.</exception>
    public void Add(string code, string? detail = null)
        => (_errors ??= new List<FieldError>(4)).Add(new FieldError(BuildPath(null), code, detail));

    /// <summary>Adds an error at the member <paramref name="member" /> below the current path, without entering it.</summary>
    /// <param name="member">The member name.</param>
    /// <param name="code">The rule's code.</param>
    /// <param name="detail">An optional detail.</param>
    /// <exception cref="ArgumentException"><paramref name="member" /> or <paramref name="code" /> is <see langword="null" /> or empty.</exception>
    public void AddAt(string member, string code, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(member);
        (_errors ??= new List<FieldError>(4)).Add(new FieldError(BuildPath(member), code, detail));
    }

    /// <summary>The collected errors as one <see cref="Invalid" />, or <see langword="null" /> when there are none.</summary>
    /// <returns>An <see cref="Invalid" /> in the order the errors were added, or <see langword="null" />.</returns>
    public readonly Invalid? ToInvalid() => _errors is null ? null : new Invalid(_errors);

    /// <summary>The collected errors as a <see cref="Result" />: success when there are none.</summary>
    /// <returns><see cref="Result.Success()" /> or the <see cref="Invalid" />.</returns>
    public readonly Result ToResult() => _errors is null ? Result.Success() : new Invalid(_errors);

    private void Push(Segment segment)
    {
        if (_depth < InlineDepth)
            _segments[_depth] = segment;
        else
            (_deep ??= []).Add(segment);

        _depth++;
    }

    private readonly string BuildPath(string? leaf)
    {
        if (_depth == 0)
            return leaf ?? string.Empty;

        var text = new StringBuilder(32);
        for (var i = 0; i < _depth; i++)
            Append(text, i < InlineDepth ? _segments[i] : _deep![i - InlineDepth]);

        if (leaf is not null)
            Append(text, new Segment(leaf, -1));

        return text.ToString();
    }

    private static void Append(StringBuilder text, Segment segment)
    {
        if (segment.Member is null)
        {
            text.Append('[').Append(segment.Index.ToString(CultureInfo.InvariantCulture)).Append(']');
            return;
        }

        if (text.Length > 0)
            text.Append('.');

        text.Append(segment.Member);
    }

    private readonly record struct Segment(string? Member, int Index);

    [InlineArray(InlineDepth)]
    private struct Segments
    {
        private Segment _element;
    }
}
