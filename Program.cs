using Avalonia;
using System;
using Velopack;

namespace Sharpwire;

class Program
{
    // Velopack must run first so installs/updates/uninstall hooks work and `vpk pack` can verify the app.
    // Avoid other third-party or SynchronizationContext-reliant code here until the host is ready.
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
