using System.Diagnostics;
using ImageUrlValidator.Models;
using ImageUrlValidator.Services;

namespace ImageUrlValidator;

internal static class Program
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private static readonly object ConsoleLock = new();

    public static async Task<int> Main()
    {
        var urls = BuildDemoUrls();

        Log("INF", nameof(Program), $"Loaded {urls.Count} URLs for validation.");

        using var naiveValidator = new NaiveValidator(Log, RequestTimeout);
        var optimizedValidator = new OptimizedValidator(Log, maxConcurrency: 10, requestTimeout: RequestTimeout);

        var naiveSummary = await RunValidatorAsync(
            "NAIVE",
            urls.Count,
            token => naiveValidator.ValidateAsync(urls, token));

        WriteLine(string.Empty);

        var optimizedSummary = await RunValidatorAsync(
            "OPTIMIZED",
            urls.Count,
            token => optimizedValidator.ValidateAsync(urls, token));

        WriteLine(string.Empty);
        WriteLine("[COMPARISON]");
        WriteLine($"Optimized validator completed the same workload {naiveSummary.GetSpeedupOver(optimizedSummary):0.0}x faster.");

        return 0;
    }

    private static async Task<RunSummary> RunValidatorAsync(
        string label,
        int urlCount,
        Func<CancellationToken, Task<IReadOnlyList<ValidationResult>>> executeAsync)
    {
        WriteLine($"[{label}]");

        var stopwatch = Stopwatch.StartNew();
        var results = await executeAsync(CancellationToken.None);
        stopwatch.Stop();

        var summary = new RunSummary(label, results, stopwatch.Elapsed);

        WriteLine($"Processed {urlCount} urls in {FormatDuration(summary.Elapsed)}");
        WriteLine(summary.FormatCounts());

        return summary;
    }

    private static IReadOnlyList<string> BuildDemoUrls()
    {
        var urls = new List<string>(capacity: 50);

        for (var batch = 1; batch <= 10; batch++)
        {
            urls.Add($"https://httpbin.org/image/jpeg?slot={batch}-a");
            urls.Add($"https://httpbin.org/image/png?slot={batch}-b");
            urls.Add($"https://httpbin.org/status/404?slot={batch}");
            urls.Add($"https://httpbin.org/status/403?slot={batch}");
            urls.Add($"https://httpbin.org/delay/10?slot={batch}");
        }

        return urls;
    }

    private static string FormatDuration(TimeSpan elapsed)
    {
        return $"{elapsed.TotalSeconds:0.0}s";
    }

    private static void Log(string level, string component, string message)
    {
        WriteLine($"{DateTimeOffset.Now:O} [{level}] [{component}] {message}");
    }

    private static void WriteLine(string message)
    {
        lock (ConsoleLock)
        {
            Console.WriteLine(message);
        }
    }

    private sealed record RunSummary(string Label, IReadOnlyList<ValidationResult> Results, TimeSpan Elapsed)
    {
        public string FormatCounts()
        {
            return string.Join(
                " | ",
                Enum.GetValues<ValidationStatus>()
                    .Select(status => $"{status}: {Results.Count(result => result.Status == status)}"));
        }

        public double GetSpeedupOver(RunSummary other)
        {
            var denominator = Math.Max(other.Elapsed.TotalMilliseconds, 1);
            return Elapsed.TotalMilliseconds / denominator;
        }
    }
}
