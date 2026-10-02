using Microsoft.Extensions.Options;

namespace RetryMesh;

public enum RetryMeshPropagationMode { Automatic, Explicit }

public sealed class RetryMeshOptions
{
    public RetryMeshPropagationMode PropagationMode { get; set; } = RetryMeshPropagationMode.Automatic;
}

public sealed class RetryMeshClientOptions
{
    /// <summary>Accept exhaustion claims only from a trusted downstream service. Default: false.</summary>
    public bool TrustDownstreamMetadata { get; set; }
}

internal sealed class RetryMeshOptionsValidator : IValidateOptions<RetryMeshOptions>
{
    public ValidateOptionsResult Validate(string? name, RetryMeshOptions options) =>
        Enum.IsDefined(options.PropagationMode) ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Invalid propagation mode.");
}
