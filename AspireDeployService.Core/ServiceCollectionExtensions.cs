using AspireDeployService.Core.Runs;
using AspireDeployService.Core.Services;
using AspireDeployService.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AspireDeployService.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Core services as stateless singletons. The redactor and ProcessRunner are created per run
    /// by <see cref="IRunContextFactory"/> (plan §6.2), never resolved from the container.
    /// </summary>
    public static IServiceCollection AddAspireDeployerCore(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddLogging();
        services.TryAddSingleton<ISettingsLocator, SettingsLocator>();
        services.TryAddSingleton<ProfileStore>();
        services.TryAddSingleton<IProfileService, ProfileService>();
        services.TryAddSingleton<IRunContextFactory, RunContextFactory>();
        return services;
    }
}
