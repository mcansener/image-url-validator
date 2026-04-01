using System.Diagnostics;
using System.Net;
using ImageUrlValidator.Models;

namespace ImageUrlValidator.Services;

internal sealed class OptimizedValidator
{
    private static readonly HttpClient SharedHttpClient = CreateClient();

    private readonly Action<string, string, string> _log;
    private readonly int _maxConcurrency;
    private readonly TimeSpan _requestTimeout;

    public OptimizedValidator(Action<string, string, string> log, int maxConcurrency, TimeSpan requestTimeout)
    {
        _log = log;
        _maxConcurrency = maxConcurrency;
        _requestTimeout = requestTimeout;
    }

    public async Task<IReadOnlyList<ValidationResult>> ValidateAsync(
        IReadOnlyList<string> urls,
        CancellationToken cancellationToken = default)
    {
        using var semaphore = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);

        var tasks = urls
            .Select(url => ValidateWithConcurrencyAsync(url, semaphore, cancellationToken))
            .ToArray();

        return await Task.WhenAll(tasks);
    }

    private async Task<ValidationResult> ValidateWithConcurrencyAsync(
        string url,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);

        try
        {
            var result = await ValidateSingleAsync(url, cancellationToken);
            LogResult(result);
            return result;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<ValidationResult> ValidateSingleAsync(string url, CancellationToken cancellationToken)
    {
        var headResult = await SendAsync(url, HttpMethod.Head, cancellationToken);

        if (!ShouldFallbackToGet(headResult))
        {
            return headResult;
        }

        var getResult = await SendAsync(url, HttpMethod.Get, cancellationToken);
        var headStatusCode = headResult.StatusCode is null ? "-" : ((int)headResult.StatusCode).ToString();
        var detail = $"HEAD returned {headStatusCode}; retried with GET.";

        if (!string.IsNullOrWhiteSpace(getResult.Detail))
        {
            detail = $"{detail} {getResult.Detail}";
        }

        return getResult with
        {
            Method = "HEAD->GET",
            Duration = headResult.Duration + getResult.Duration,
            Detail = detail
        };
    }

    private async Task<ValidationResult> SendAsync(
        string url,
        HttpMethod method,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_requestTimeout);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await SharedHttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token);

            return new ValidationResult(
                url,
                Classify(response.StatusCode),
                response.StatusCode,
                method.Method,
                stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            return new ValidationResult(
                url,
                ValidationStatus.Timeout,
                null,
                method.Method,
                stopwatch.Elapsed,
                $"Request exceeded {_requestTimeout.TotalSeconds:0}s.");
        }
        catch (HttpRequestException exception)
        {
            return new ValidationResult(
                url,
                ValidationStatus.Other,
                null,
                method.Method,
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
            nameof(OptimizedValidator),
            $"{result.Method} {result.Url} -> {result.Status} ({statusCode}) in {result.Duration.TotalMilliseconds:0} ms{detail}");
    }

    private static bool ShouldFallbackToGet(ValidationResult result)
    {
        if (result.Status != ValidationStatus.Other || result.StatusCode is null)
        {
            return false;
        }

        return result.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented
            || (int)result.StatusCode >= 500;
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
