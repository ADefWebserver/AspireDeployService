using AspireDeployService.Core.AI.Models;

namespace AspireDeployService.Core.AI;

/// <summary>The global AI Help switches (plan §6.1). <see cref="MaxLogTailLines"/> is clamped to 20..1000.</summary>
public sealed record AIOptions(bool EnableFileTools, int MaxLogTailLines)
{
    public const int DefaultMaxLogTailLines = 200;
    public static AIOptions Default { get; } = new(true, DefaultMaxLogTailLines);
}

/// <summary>One provider's values as the Settings card saves them.</summary>
/// <param name="NewApiKey">null keeps the saved key; "" clears it; any other value replaces it.</param>
public sealed record AIProviderEdit(AIServiceType Type, string AIModel, string Endpoint, string ApiVersion,
    string DeploymentPath, string ApiProtocol, string? NewApiKey);
