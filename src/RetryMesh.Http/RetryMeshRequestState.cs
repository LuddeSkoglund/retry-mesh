using Microsoft.AspNetCore.Http;

namespace RetryMesh;

// Snapshots survive disposal of HttpResponseMessage. The feature is installed by middleware,
// never by pooled HttpClient handlers, and is confined to this incoming request.
internal sealed class RetryMeshRequestState
{
    private readonly object _gate = new();
    private readonly List<(RetryMeshFailure Failure, int Status)> _candidates = [];
    private bool _invalidated;
    internal bool ExplicitResult { get; set; }

    internal void Record(RetryMeshFailure failure, int status)
    {
        lock (_gate)
            if (!_invalidated) _candidates.Add((failure, status));
    }

    internal void Invalidate(bool onlyIfPending = false)
    {
        lock (_gate)
            if (!onlyIfPending || _candidates.Count > 0) _invalidated = true;
    }

    internal (RetryMeshFailure? Failure, string Reason) Select(int status)
    {
        lock (_gate)
        {
            if (_invalidated) return (null, "invalidated outcome");
            if (status < 400) return (null, "successful or non-failure response");
            var matching = _candidates.Where(c => c.Status == status).ToArray();
            return matching.Length switch
            {
                1 => (matching[0].Failure, "unique matching candidate"),
                0 => (null, "status mismatch or no candidate"),
                _ => (null, "ambiguous candidates")
            };
        }
    }

    internal static void Write(HttpResponse response, RetryMeshFailure failure)
    {
        using var metadata = new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
        RetryMeshHeaders.Write(metadata, failure);
        foreach (var header in metadata.Headers)
            response.Headers[header.Key] = header.Value.ToArray();
    }
}
