using System.ComponentModel;

namespace Tarol.Changes.AspNetCore;

/// <summary>
/// A member of a nested input record for <see cref="ChangeEndpoints.Missing"/> (TN-23): its C# name (the error path),
/// its own JSON name, whether it may be missing, whether it is a list, and the members of the record it holds.
/// </summary>
/// <param name="Name">The C# name.</param>
/// <param name="Json">The pinned JSON name, or <see langword="null"/> for the naming policy's.</param>
/// <param name="Optional">Whether it may be missing (nullable or with a default).</param>
/// <param name="List">Whether it is a list of records.</param>
/// <param name="Members">The members of the record it holds, or <see langword="null"/>.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record RequiredMember(string Name, string? Json, bool Optional, bool List, RequiredMember[]? Members);
