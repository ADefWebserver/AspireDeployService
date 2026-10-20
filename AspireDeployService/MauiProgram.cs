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

		return builder.Build();
	}
}
