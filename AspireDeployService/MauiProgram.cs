using AspireDeployService.Core;
using AspireDeployService.Core.Interaction;
using AspireDeployService.Services;
using Microsoft.Extensions.Logging;

namespace AspireDeployService;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();

		// Plan §6.2: Core services, the dialog host's IUserInteraction, and the run owner.
		builder.Services.AddAspireDeployerCore();
		builder.Services.AddSingleton<DialogService>();
		builder.Services.AddSingleton<IUserInteraction>(sp => sp.GetRequiredService<DialogService>());
		builder.Services.AddSingleton<RunCoordinator>();
		builder.Services.AddSingleton<AIHelpService>();
		builder.AddWindowsShell();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		var app = builder.Build();
		CleanupOverlayLeftovers(app.Services);
		return app;
	}

	/// <summary>
	/// Resource pickers plan D3: a deploy killed mid-run can leave a temporary <c>*.ads-deploy.*</c> AppHost copy next to
	/// the user's AppHost. Remove those (only files that start with the marker line) for every local profile folder.
	/// </summary>
	private static void CleanupOverlayLeftovers(IServiceProvider services)
	{
		var store = services.GetRequiredService<AspireDeployService.Core.Settings.ProfileStore>();
		var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("AspireDeployService.Startup");
		_ = Task.Run(async () =>
		{
			try
			{
				var snapshot = await store.LoadAsync(CancellationToken.None);
				var folders = snapshot.Document.AspireDeploy.Profiles.Values
					.Where(p => p.Source.Kind == AspireDeployService.Core.Sources.SourceKind.LocalFolder && Directory.Exists(p.Source.Location))
					.Select(p => p.Source.Location)
					.Distinct(StringComparer.OrdinalIgnoreCase);
				foreach (var folder in folders)
				{
					foreach (var removed in AspireDeployService.Core.Deploy.SiblingOverlay.CleanupLeftovers(folder))
						logger.LogInformation("Removed a leftover temporary AppHost file: {Path}", removed);
				}
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex, "Could not clean up temporary AppHost files.");
			}
		});
	}
}
