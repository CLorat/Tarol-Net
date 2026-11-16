using System.Diagnostics.CodeAnalysis;
using Tarol.Persistence;
using Tarol.Queries;

namespace Tarol.Caching;

/// <summary>
/// Where list pages are cached (<see cref="MemoryPageCache"/> by default). An entry is stamped with the projection's
/// checkpoint read <b>before</b> its search, and answers only for that position: a batch during the search makes the
/// next request miss. Implementations are thread-safe and bound their memory.
/// </summary>
public interface IPageCache
{
    /// <summary>The page cached under <paramref name="key"/>, if it was stamped with <paramref name="position"/>.</summary>
    /// <typeparam name="TRow">The row record.</typeparam>
    /// <param name="key">The key.</param>
    /// <param name="position">The projection's current checkpoint.</param>
    /// <param name="page">The page.</param>
    /// <returns>Whether it was a hit.</returns>
    bool TryGet<TRow>(PageKey key, GlobalPosition position, [NotNullWhen(true)] out Page<TRow>? page)
        where TRow : class;

    /// <summary>Caches a page, stamped with the checkpoint read before its search (a later stamp already cached is kept).</summary>
    /// <typeparam name="TRow">The row record.</typeparam>
    /// <param name="key">The key.</param>
    /// <param name="position">The checkpoint read before the search.</param>
    /// <param name="page">The page.</param>
    /// <param name="size">The page's estimated size in bytes (for the byte cap).</param>
    void Put<TRow>(PageKey key, GlobalPosition position, Page<TRow> page, long size)
        where TRow : class;

    /// <summary>Removes every entry.</summary>
    void Clear();
}
