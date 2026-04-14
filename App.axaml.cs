using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Sharpwire.Core;

namespace Sharpwire;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = WorkspaceBootstrap.EnsureLayout();
            var services = new ServiceCollection();
            services.AddSharpwireServices(paths);
            var provider = services.BuildServiceProvider();
            
            var mw = provider.GetRequiredService<MainWindow>();
            
            // If we have arguments, pass the first one as a one-shot prompt
            if (desktop.Args != null && desktop.Args.Length > 0)
            {
                mw.InitialPrompt = desktop.Args[0];
            }

            desktop.MainWindow = mw;
        }

        base.OnFrameworkInitializationCompleted();
    }
}