using System.Globalization;
using System.Net.Http.Headers;

namespace DistributedResilience;

/// <summary>The version 0.1 HTTP response protocol. Untrusted or incomplete metadata is ignored.</summary>
public static class DistributedRetryHeaders
{
    public const string Status = "Distributed-Retry-Status";
    public const string Attempts = "Distributed-Retry-Attempts";
    public const string RetriedBy = "Distributed-Retry-By";
    public const string FailureId = "Distributed-Retry-Failure-Id";

    public static DistributedRetryFailure? Read(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return null;
        var status = Single(response.Headers, Status);
        var by = Single(response.Headers, RetriedBy);
        var id = Single(response.Headers, FailureId);
        if (status != "exhausted" || !ValidToken(by) || !ValidToken(id) ||
            !int.TryParse(Single(response.Headers, Attempts), NumberStyles.None,
                CultureInfo.InvariantCulture, out var attempts) || attempts < 2)
            return null;
        return new() { FailureId = id!, RetriedBy = by!, Attempts = attempts, Outcome = DistributedRetryOutcome.Exhausted };
    }

    public static void Write(HttpResponseMessage response, DistributedRetryFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (!ValidToken(failure.FailureId) || !ValidToken(failure.RetriedBy) ||
            failure.Attempts < 2 || failure.Outcome != DistributedRetryOutcome.Exhausted)
            throw new ArgumentException("Invalid retry exhaustion metadata.", nameof(failure));
        Set(response.Headers, Status, "exhausted");
        Set(response.Headers, Attempts, failure.Attempts.ToString(CultureInfo.InvariantCulture));
        Set(response.Headers, RetriedBy, failure.RetriedBy);
        Set(response.Headers, FailureId, failure.FailureId);
    }

    internal static bool ValidToken(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static string? Single(HttpHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values)) return null;
        var items = values.Take(2).ToArray();
        return items.Length == 1 ? items[0] : null;
    }

    private static void Set(HttpHeaders headers, string name, string value)
    {
        headers.Remove(name);
        headers.Add(name, value);
    }
}
