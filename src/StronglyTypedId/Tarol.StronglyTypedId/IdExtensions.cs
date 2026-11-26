using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Tarol.StronglyTypedId;

/// <summary>
///     Provides extension behavior for <see cref="Id{T}" /> using C# 14 extension members.
/// </summary>
public static class IdExtensions
{
    // Instance extensions
    extension<T>(Id<T> id)
    {
        /// <summary>
        ///     Gets a value indicating whether the ID is empty (<see cref="Guid.Empty" />).
        /// </summary>
        public bool IsEmpty => id.Value == Guid.Empty;

        /// <summary>
        ///     Throws if the ID is empty; otherwise returns the ID unchanged.
        ///     Useful as a guard clause in constructors and command handlers:
        ///     <c>_ownerId = ownerId.ThrowIfEmpty();</c>
        /// </summary>
        /// <param name="paramName">Automatically captured caller argument expression.</param>
        /// <exception cref="ArgumentException">The ID is <see cref="Id{T}.Empty" />.</exception>
        [StackTraceHidden]
        public Id<T> ThrowIfEmpty([CallerArgumentExpression(nameof(id))] string? paramName = null)
        {
            if (id.Value == Guid.Empty)
                throw new ArgumentException($"Id<{typeof(T).Name}> must not be empty.", paramName);
            return id;
        }
    }

    // Static extensions
    extension<T>(Id<T>)
    {
        /// <summary>
        ///     Generates a new unique <see cref="Id{T}" /> using <see cref="Guid.CreateVersion7()" />.
        ///     <para>
        ///         Version 7 Guids are time-ordered (sortable). This reduces database index fragmentation
        ///         and allows IDs to be used effectively as sorting keys.
        ///     </para>
        ///     <para>
        ///         This is a method rather than a property: a property returning a different value on
        ///         every read violates framework design guidelines and confuses debugger watch windows.
        ///     </para>
        /// </summary>
        /// <returns>A new, time-sortable ID.</returns>
        public static Id<T> New()
        {
            return new Id<T>(Guid.CreateVersion7());
        }

        /// <summary>
        ///     Generates a new <see cref="Id{T}" /> whose version-7 timestamp component is derived
        ///     from the supplied instant. Essential for deterministic ordering in tests and for
        ///     backfilling historical data with correctly-sorted IDs.
        /// </summary>
        /// <param name="timestamp">The instant to embed in the ID's timestamp bits.</param>
        /// <returns>A new time-sortable ID anchored to <paramref name="timestamp" />.</returns>
        public static Id<T> New(DateTimeOffset timestamp)
        {
            return new Id<T>(Guid.CreateVersion7(timestamp));
        }

        /// <summary>
        ///     Generates a new <see cref="Id{T}" /> whose version-7 timestamp comes from <paramref name="timeProvider" />.
        ///     Use it where the rest of the code already takes a <see cref="TimeProvider" />, so that tests with a
        ///     fake clock get deterministic id order.
        /// </summary>
        /// <param name="timeProvider">The clock to read the timestamp from.</param>
        /// <returns>A new time-sortable ID.</returns>
        public static Id<T> New(TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(timeProvider);
            return new Id<T>(Guid.CreateVersion7(timeProvider.GetUtcNow()));
        }

        /// <summary>
        ///     A deterministic id from a business key (TN-42, Tatalu FB-F04.1-3: a stream per child and month): the same
        ///     <paramref name="scope"/> and parts always give the same id, on every machine and release (a UUIDv8 over
        ///     SHA-256 of a typed, length-prefixed encoding). The scope is a free string (owner decision CR-4b): choose one per
        ///     kind of stream and never change it, e.g. <c>Id&lt;AttendanceMonth&gt;.Derive("attendance-month", child, 2026, 11)</c>.
        /// </summary>
        /// <param name="scope">The key's namespace, e.g. <c>"attendance-month"</c>.</param>
        /// <param name="parts">The key's parts (<see cref="Guid"/>, <see cref="string"/>, <see cref="int"/>, <see cref="long"/>, <see cref="DateOnly"/>).</param>
        /// <returns>The derived id.</returns>
        public static Id<T> Derive(string scope, params ReadOnlySpan<IdPart> parts) => new(IdPart.Derive(scope, parts));

        /// <summary>
        ///     Creates an instance of <see cref="Id{T}" /> from an existing <see cref="Guid" />.
        /// </summary>
        /// <param name="value">The existing Guid.</param>
        /// <returns>A strongly typed ID.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Id<T> From(Guid value)
        {
            return new Id<T>(value);
        }

        /// <summary>
        ///     Parses a string representation of a Guid into a strongly-typed <see cref="Id{T}" />.
        /// </summary>
        /// <param name="value">The string containing the Guid to convert.</param>
        /// <returns>A strongly typed ID.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="value" /> is <c>null</c>.</exception>
        /// <exception cref="FormatException">
        ///     <paramref name="value" /> is not in a recognized Guid format (<c>D</c>, <c>N</c>,
        ///     <c>B</c>, <c>P</c>, or <c>X</c>).
        /// </exception>
        [StackTraceHidden]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Id<T> Parse(string value)
        {
            return Id<T>.Parse(value, null);
        }

        /// <summary>
        ///     Tries to parse a string representation of a Guid into a strongly-typed <see cref="Id{T}" />.
        ///     Does not throw exceptions on failure.
        /// </summary>
        /// <param name="value">The string containing the Guid to convert.</param>
        /// <param name="id">
        ///     When this method returns, contains the parsed ID if successful, or <see cref="Id{T}.Empty" /> if
        ///     failed.
        /// </param>
        /// <returns><c>true</c> if the parse was successful; otherwise, <c>false</c>.</returns>
        public static bool TryParse([NotNullWhen(true)] string? value, out Id<T> id)
        {
            return Id<T>.TryParse(value, null, out id);
        }
    }
}
