using System.Reflection;
using RetryMesh;

namespace RetryMesh.Http.Tests;

public class ReleaseGateApiTests
{
    [Fact]
    public void OnlyConsumerFacingTypesAreExportedAndNoStaticFailureRegistryExists()
    {
        var assembly = typeof(RetryMeshFailure).Assembly;
        Assert.Equal(new[] { "RetryMeshClientOptions", "RetryMeshExtensions", "RetryMeshFailure", "RetryMeshFailureResult",
            "RetryMeshHeaders", "RetryMeshOptions", "RetryMeshOutcome", "RetryMeshPropagationMode" },
            assembly.GetExportedTypes().Select(t => t.Name).Order());
        var staticFields = assembly.GetTypes().SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));
        Assert.DoesNotContain(staticFields, f => typeof(System.Collections.IDictionary).IsAssignableFrom(f.FieldType)
            || typeof(System.Collections.ICollection).IsAssignableFrom(f.FieldType));
    }
}
