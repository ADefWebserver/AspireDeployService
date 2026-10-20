// Copied from <ref>/src/BlazorDataOrchestrator.Core/Services/AIConnectionTester.cs (Blazor-Data-Orchestrator). Re-sync changes from there.
// Changes: the client comes from IChatClientProvider (AI-Help-Plan D16) so tests can fake it.
using System.Diagnostics;
using AspireDeployService.Core.AI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AspireDeployService.Core.AI.Providers;

/// <summary>
/// Outcome of a single round trip to the configured AI provider.
/// </summary>
public sealed record AIConnectionTestResult(
    bool Success,
    string OperationUrl,
    string Protocol,
    int? StatusCode,
    string Message,
    TimeSpan Elapsed);

/// <summary>
/// Sends one minimal prompt so the admin can verify a provider configuration
/// without leaving the settings screen.
/// </summary>
public sealed class AIConnectionTester
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly IChatClientProvider _clients;
    private readonly ILogger<AIConnectionTester>? _logger;

    public AIConnectionTester(IChatClientProvider? clients = null, ILogger<AIConnectionTester>? logger = null)
    {
        _clients = clients ?? new ChatClientProvider();
        _logger = logger;
    }

    public async Task<AIConnectionTestResult> TestAsync(AIProviderSettings settings, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        FoundryEndpointResolution? resolution = null;

        try
        {
            if (!settings.IsConfigured)
            {
                throw new AIConfigurationException("Enter an API key before testing the connection.");
            }

            resolution = TryResolve(settings);

            using var client = _clients.Create(settings);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(Timeout);

            var options = new ChatOptions { MaxOutputTokens = 32 };

            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Reply with OK")],
                options,
                timeoutSource.Token);

            stopwatch.Stop();

            // Reasoning models can spend the whole budget on reasoning, so empty text still means success.
            var text = string.IsNullOrWhiteSpace(response.Text) ? "(no text returned)" : response.Text.Trim();

            return Build(settings, resolution, true, null, $"Connection succeeded. Response: {text}", stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return Build(settings, resolution, false, null,
                $"The request timed out after {Timeout.TotalSeconds:0} seconds.", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger?.LogWarning(ex, "AI connection test failed for {ServiceType}.", settings.ServiceType);

            return Build(settings, resolution, false, AIErrorTranslator.ExtractStatus(ex),
                AIErrorTranslator.Translate(ex, resolution, settings), stopwatch.Elapsed);
        }
    }

    private static FoundryEndpointResolution? TryResolve(AIProviderSettings settings)
    {
        var usesFoundryRouting = settings.ServiceType == AIServiceType.AzureAIFoundry
            || (settings.ServiceType == AIServiceType.AzureOpenAI
                && FoundryEndpointResolver.ShouldUseV1Routing(settings.Endpoint));

        return usesFoundryRouting ? ChatClientFactory.ResolveFoundry(settings) : null;
    }

    private static AIConnectionTestResult Build(
        AIProviderSettings settings,
        FoundryEndpointResolution? resolution,
        bool success,
        int? status,
        string message,
        TimeSpan elapsed)
        => new(
            success,
            resolution?.OperationUrl ?? (string.IsNullOrWhiteSpace(settings.Endpoint) ? "(provider default)" : settings.Endpoint),
            resolution is null ? AIServiceTypes.ToDisplayName(settings.ServiceType) : FoundryEndpointResolver.Describe(resolution.Protocol),
            status,
            message,
            elapsed);
}
