// Copied from <ref>/src/BlazorDataOrchestrator.Core/Models/AIServiceType.cs (Blazor-Data-Orchestrator). Re-sync changes from there.
namespace AspireDeployService.Core.AI.Models;

/// <summary>
/// The supported AI service providers.
/// </summary>
public enum AIServiceType
{
    OpenAI = 0,
    AzureOpenAI = 1,
    Anthropic = 2,
    GoogleAI = 3,
    AzureAIFoundry = 4
}
