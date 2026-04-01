using System.Net;

namespace ImageUrlValidator.Models;

internal enum ValidationStatus
{
    Success,
    NotFound,
    Forbidden,
    Timeout,
    Other
}

internal sealed record ValidationResult(
    string Url,
    ValidationStatus Status,
    HttpStatusCode? StatusCode,
    string Method,
    TimeSpan Duration,
    string? Detail = null);
