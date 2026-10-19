using AspireDeployService.Services;
using Microsoft.Maui.LifecycleEvents;
using Windows.Storage.Pickers;

namespace AspireDeployService;

/// <summary>Windows-only pieces: the folder picker and the close guard (plan D15 part 2).</summary>
public static class WindowsShell
{
    private static bool _closeConfirmed;

    public static MauiAppBuilder AddWindowsShell(this MauiAppBuilder builder)
    {
        builder.ConfigureLifecycleEvents(events => events.AddWindows(w => w.OnWindowCreated(AttachCloseGuard)));
        return builder;
    }

    /// <summary>
    /// While a run is active, closing the window first asks, then cancels every run and waits up to 15 seconds.
    /// Window.Destroying cannot stop a close, so this hooks AppWindow.Closing. If the app dies instead, the
    /// Job Object kills the child processes.
    /// </summary>
    private static void AttachCloseGuard(Microsoft.UI.Xaml.Window window)
    {
        window.AppWindow.Closing += async (_, args) =>
        {
            var services = IPlatformApplication.Current?.Services;
            var coordinator = services?.GetService<RunCoordinator>();
            var dialogs = services?.GetService<DialogService>();
            if (_closeConfirmed || coordinator is null || dialogs is null || !coordinator.HasActive)
                return;

            args.Cancel = true;
            var stop = await dialogs.ConfirmAsync("Stop the running work?",
                "A run is still active. Closing stops it; a deployment may leave resources partly created. Close anyway?",
                "Stop and close", CancellationToken.None);
            if (!stop)
                return;

            await coordinator.CancelAllAsync(TimeSpan.FromSeconds(15));
            _closeConfirmed = true;
            window.Close();
        };
    }

    /// <summary>A native folder picker for an unpackaged WinUI app.</summary>
    public static async Task<string?> PickFolderAsync()
    {
        return await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as MauiWinUIWindow;
            if (window is null)
                return null;
            WinRT.Interop.InitializeWithWindow.Initialize(picker, window.WindowHandle);
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        });
    }
}
