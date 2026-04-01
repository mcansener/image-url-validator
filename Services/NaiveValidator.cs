using System.Diagnostics;
using System.Net;
using ImageUrlValidator.Models;

namespace ImageUrlValidator.Services;

internal sealed class NaiveValidator : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Action<string, string, string> _log;
    private readonly TimeSpan _requestTimeout;

    public NaiveValidator(Action<string, string, string> log, TimeSpan requestTimeout)
    {
        _log = log;
        _requestTimeout = requestTimeout;
        _httpClient = CreateClient();
    }

    public async Task<IReadOnlyList<ValidationResult>> ValidateAsync(
        IReadOnlyList<string> urls,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ValidationResult>(urls.Count);

        foreach (var url in urls)
        {
            var result = await ValidateSingleAsync(url, cancellationToken);
            results.Add(result);
            LogResult(result);
        }

        return results;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private async Task<ValidationResult> ValidateSingleAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_requestTimeout);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token);

            return new ValidationResult(
                url,
                Classify(response.StatusCode),
                response.StatusCode,
                HttpMethod.Get.Method,
                stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            return new ValidationResult(
                url,
                ValidationStatus.Timeout,
                null,
                HttpMethod.Get.Method,
                stopwatch.Elapsed,
                $"Request exceeded {_requestTimeout.TotalSeconds:0}s.");
        }
        catch (HttpRequestException exception)
        {
            return new ValidationResult(
                url,
                ValidationStatus.Other,
                null,
                HttpMethod.Get.Method,
                stopwatch.Elapsed,
                exception.Message);
        }
    }

    private void LogResult(ValidationResult result)
    {
        var statusCode = result.StatusCode is null ? "-" : ((int)result.StatusCode).ToString();
        var detail = string.IsNullOrWhiteSpace(result.Detail) ? string.Empty : $" | {result.Detail}";

        _log(
            GetLevel(result.Status),
            nameof(NaiveValidator),
            $"{result.Method} {result.Url} -> {result.Status} ({statusCode}) in {result.Duration.TotalMilliseconds:0} ms{detail}");
    }

    private static ValidationStatus Classify(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.OK => ValidationStatus.Success,
            HttpStatusCode.NotFound => ValidationStatus.NotFound,
            HttpStatusCode.Forbidden => ValidationStatus.Forbidden,
            _ => ValidationStatus.Other
        };
    }

    private static string GetLevel(ValidationStatus status)
    {
        return status switch
        {
            ValidationStatus.Success => "INF",
            ValidationStatus.NotFound => "WRN",
            ValidationStatus.Forbidden => "WRN",
            ValidationStatus.Timeout => "WRN",
            _ => "ERR"
        };
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("ImageUrlValidator/1.0");

        return client;
    }
}
