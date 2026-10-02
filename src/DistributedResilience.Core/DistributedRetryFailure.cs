namespace DistributedResilience;

public enum DistributedRetryOutcome { Exhausted }

/// <summary>Describes one downstream failure whose retry policy was exhausted.</summary>
public sealed record DistributedRetryFailure
{
    public required string FailureId { get; init; }
    public required string RetriedBy { get; init; }
    public required int Attempts { get; init; }
    public required DistributedRetryOutcome Outcome { get; init; }
}
