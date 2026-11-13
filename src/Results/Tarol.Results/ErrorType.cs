using System.Text.Json.Serialization;

namespace Tarol.Results;

/// <summary>
/// The kind of a light, non-fatal <see cref="Error"/>. Each value maps to one HTTP status code
/// (<see cref="ErrorTypeExtensions.ToHttpStatusCode(ErrorType)"/>).
/// </summary>
/// <remarks>
/// Validation problems are not an <see cref="ErrorType"/>: they are an <see cref="Invalid"/> violation with a
/// list of <see cref="FieldError"/>s. JSON writes the member name (<c>"NotFound"</c>), never the number.
/// The default value is <see cref="Internal"/>, so an unset type maps to HTTP 500 rather than to a client error.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ErrorType>))]
public enum ErrorType : byte
{
    /// <summary>An unexpected server-side failure. HTTP 500.</summary>
    Internal = 0,

    /// <summary>The requested resource does not exist. HTTP 404.</summary>
    NotFound,

    /// <summary>The request conflicts with the current state (duplicate, concurrency). HTTP 409.</summary>
    Conflict,

    /// <summary>Authenticated, but not allowed to do this. HTTP 403.</summary>
    Forbidden,

    /// <summary>Authentication is missing or invalid. HTTP 401.</summary>
    Unauthorized,

    /// <summary>The resource existed and has been removed permanently. HTTP 410.</summary>
    Gone,

    /// <summary>Well-formed input that breaks a business rule. HTTP 422.</summary>
    UnprocessableEntity,

    /// <summary>A rate limit was exceeded. HTTP 429.</summary>
    TooManyRequests,

    /// <summary>A dependency or the service is temporarily unavailable. HTTP 503.</summary>
    Unavailable,

    /// <summary>A dependency did not answer in time. HTTP 504.</summary>
    Timeout,
}

/// <summary>HTTP mapping for <see cref="ErrorType"/>.</summary>
public static class ErrorTypeExtensions
{
    /// <summary>Returns the HTTP status code for <paramref name="type"/>; unknown values map to 500.</summary>
    /// <param name="type">The error type.</param>
    /// <returns>The status code, for example 404 for <see cref="ErrorType.NotFound"/>.</returns>
    public static int ToHttpStatusCode(this ErrorType type) => type switch
    {
        ErrorType.NotFound => 404,
        ErrorType.Conflict => 409,
        ErrorType.Forbidden => 403,
        ErrorType.Unauthorized => 401,
        ErrorType.Gone => 410,
        ErrorType.UnprocessableEntity => 422,
        ErrorType.TooManyRequests => 429,
        ErrorType.Unavailable => 503,
        ErrorType.Timeout => 504,
        _ => 500,
    };
}
