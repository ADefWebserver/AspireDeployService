// Copied from BlazorDataDeploymentWebsite.Core/Domain/Enums.cs (implementation plan D10):
// NotEntitled (billing) removed, Release renamed to Source.
namespace AspireDeployService.Core.Errors;

/// <summary>Deployment failure categories.</summary>
public enum ErrorCategory
{
    None,
    Permissions,
    Provider,
    Quota,
    Naming,
    Policy,
    Source,
    Canceled,
    Timeout,
    Unknown
}
