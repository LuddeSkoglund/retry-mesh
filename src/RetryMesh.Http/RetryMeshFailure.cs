namespace RetryMesh;

public enum RetryMeshOutcome { Exhausted }

/// <summary>Describes one downstream failure whose retry policy was exhausted.</summary>
public sealed record RetryMeshFailure
{
    public required string FailureId { get; init; }
    public required string RetriedBy { get; init; }
    public required int Attempts { get; init; }
    public required RetryMeshOutcome Outcome { get; init; }
}
