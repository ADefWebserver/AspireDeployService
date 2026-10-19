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
        services.TryAddSingleton<IDiscoveryService, DiscoveryService>();
        services.TryAddSingleton<IDoctorService, DoctorService>();
        services.TryAddSingleton<IDeployService, DeployService>();
        return services;
    }
}
