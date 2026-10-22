using AspireDeployService.Core.AI;
using AspireDeployService.Core.AI.ModelCatalog;
using AspireDeployService.Core.AI.Providers;
using AspireDeployService.Core.Azure;
using AspireDeployService.Core.Infrastructure;
using AspireDeployService.Core.Prerequisites;
using AspireDeployService.Core.Runs;
using AspireDeployService.Core.Services;
using AspireDeployService.Core.Settings;
using AspireDeployService.Core.Sources;
using AspireDeployService.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AspireDeployService.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Core services as stateless singletons. The redactor and ProcessRunner are created per run
    /// by <see cref="IRunContextFactory"/> (plan §6.2), never resolved from the container. The host must
    /// register an <see cref="Interaction.IUserInteraction"/>.
    /// </summary>
    public static IServiceCollection AddAspireDeployerCore(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddLogging();
        services.AddHttpClient(GitHubSource.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(10));

        services.TryAddSingleton<ISettingsLocator, SettingsLocator>();
        services.TryAddSingleton<ProfileStore>();
        services.TryAddSingleton<IProfileService, ProfileService>();
        services.TryAddSingleton<IRunContextFactory, RunContextFactory>();
        services.TryAddSingleton<TrustStore>();
        services.TryAddSingleton<GitHubTokenResolver>();
        services.TryAddSingleton<IAspireCliProvisioner, AspireCliProvisioner>();
        services.TryAddSingleton<PrerequisiteChecker>();
        services.TryAddSingleton<DiscoveryPipeline>();
        services.TryAddSingleton<IAzureAccountService, AzureAccountService>();
        services.TryAddSingleton<IAzureResourceProvisioner, AzureResourceProvisioner>();
        services.TryAddSingleton<IDiscoveryService, DiscoveryService>();
        services.TryAddSingleton<ISetupService, SetupService>();
        services.TryAddSingleton<IDeployService, DeployService>();
        return services.AddAIHelpCore();
    }

    /// <summary>AI settings and AI Help (AI Help plan section 8). Catalogs use TryAddEnumerable: the registry takes all five.</summary>
    private static IServiceCollection AddAIHelpCore(this IServiceCollection services)
    {
        services.AddHttpClient(HttpModelCatalogBase.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAIModelCatalog, OpenAIModelCatalog>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAIModelCatalog, AzureOpenAIModelCatalog>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAIModelCatalog, AzureAIFoundryModelCatalog>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAIModelCatalog, AnthropicModelCatalog>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAIModelCatalog, GoogleAIModelCatalog>());
        services.TryAddSingleton<AIProviderRegistry>();
        services.TryAddSingleton<AIModelListCache>();
        services.TryAddSingleton<IChatClientProvider, ChatClientProvider>();
        services.TryAddSingleton<AIConnectionTester>();
        services.TryAddSingleton<IAISettingsService, AISettingsService>();
        services.TryAddSingleton<AIPromptScrubber>();
        services.TryAddSingleton<IAIChatService, AIChatService>();
        return services;
    }
}
