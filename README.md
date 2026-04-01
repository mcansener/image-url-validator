# Image URL Validator

This repository demonstrates a practical pattern for validating large batches of image URLs without turning the validator into the bottleneck.

## Problem

At small scale, checking image URLs looks trivial: issue an HTTP request, inspect the response, log the result. That approach breaks down quickly when the workload grows from a handful of URLs to thousands:

- Sequential validation makes overall runtime roughly equal to the sum of every request latency.
- Slow endpoints dominate wall-clock time.
- Broken links still consume network and thread time.
- The validator itself becomes an operational cost in batch jobs, ingestion pipelines, or media audits.

## Why the naive approach fails

`NaiveValidator` processes URLs one at a time with `GET` requests. The implementation is intentionally simple and useful as a baseline, but it inherits the worst possible characteristic for throughput-sensitive workloads: every slow request blocks everything behind it.

With the demo workload in this project, ten slow endpoints are enough to push the sequential run toward the 30-second range because each timeout is paid serially.

The baseline uses the same `3` second timeout budget as the optimized path so the comparison stays focused on execution strategy rather than allowing one implementation to run indefinitely.

## Why unbounded parallelism also fails

Launching thousands of requests concurrently is not a fix. It shifts the failure mode:

- Outbound sockets spike and connection pools churn.
- Remote hosts can rate limit or blacklist the caller.
- Local CPU spends more time scheduling and cleaning up work than doing useful validation.
- Timeouts arrive in waves, amplifying retries and log noise.

The result is an unstable validator that is fast in ideal conditions and unpredictable in real ones.

## Final solution

`OptimizedValidator` uses a bounded-concurrency model:

- A singleton `HttpClient` keeps connection reuse predictable.
- `SemaphoreSlim` caps active requests at `10`.
- `HEAD` is attempted first to avoid downloading bodies when headers are enough.
- `GET` is used as a fallback when `HEAD` is unsupported or unreliable.
- A per-request timeout of `3` seconds prevents slow endpoints from consuming the entire run.
- Responses are classified into `Success`, `NotFound`, `Forbidden`, `Timeout`, or `Other`.

The sample workload is constructed to make the concurrency difference obvious:

- 20 successful image responses
- 10 `404` responses
- 10 `403` responses
- 10 slow endpoints that exceed the timeout budget

That usually produces output in the shape below:

```text
[NAIVE]
Processed 50 urls in 30s

[OPTIMIZED]
Processed 50 urls in 3s
```

Exact numbers vary by network conditions, but the order-of-magnitude improvement should remain clear.

## Structure

```text
Program.cs
Services/
  NaiveValidator.cs
  OptimizedValidator.cs
Models/
  ValidationResult.cs
```

## Run

```powershell
dotnet run
```

The application emits per-request logs and a summary for both validators so the throughput difference is visible without attaching a profiler.
