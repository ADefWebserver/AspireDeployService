// Copied from <ref>/src/BlazorDataOrchestrator.Core/Services/AIConfigurationException.cs (Blazor-Data-Orchestrator). Re-sync changes from there.
namespace AspireDeployService.Core.AI.Providers;

/// <summary>
/// Thrown for AI configuration problems detected before any network call,
/// such as a malformed endpoint or a non-https URL.
/// </summary>
public sealed class AIConfigurationException : Exception
{
    public AIConfigurationException(string message) : base(message)
    {
    }
}
