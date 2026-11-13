namespace Tarol.Results;

/// <summary>
/// The one-reference state shared by <see cref="Result"/> and <see cref="Result{T}"/>:
/// <see langword="null"/> means <c>default</c> (read as <see cref="Error.Uninitialized"/>), <see cref="Ok"/> means
/// success, anything else is the violation. One reference instead of a flag plus a reference keeps
/// <c>Result&lt;Guid&gt;</c> at 24 bytes (docs/migration/results.md §3.1).
/// </summary>
internal static class ResultState
{
    /// <summary>Private sentinel for success; never handed out.</summary>
    internal static readonly Violation Ok = new Error(ErrorType.Internal, "Result.Success");

    /// <summary>Combines failures: the first <see cref="Error"/> wins, otherwise all <see cref="Invalid"/> field errors are concatenated.</summary>
    internal struct Accumulator
    {
        private Error? _error;
        private List<FieldError>? _fields;

        public void Add(Violation violation)
        {
            if (_error is not null)
            {
                return;
            }

            switch (violation)
            {
                case Error error:
                    _error = error;
                    break;
                case Invalid invalid:
                    (_fields ??= []).AddRange(invalid.Errors);
                    break;
            }
        }

        public readonly Violation? Result => _error is not null ? _error : _fields is null ? null : new Invalid(_fields);
    }
}
