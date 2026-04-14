using System;
using System.IO;

namespace Sharpwire.Core;

public static class WorkspaceBootstrap
{
    public static AppPaths EnsureLayout()
    {
        var workspace = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workspace");
        Directory.CreateDirectory(workspace);
        var plugins = Path.Combine(workspace, "plugins");
        Directory.CreateDirectory(plugins);
        TrySeedBundledPluginSample(plugins);

        var globalDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sharpwire", "plugins");
        Directory.CreateDirectory(globalDir);

        return new AppPaths(workspace, plugins, globalDir);
    }

    /// <summary>First-time empty <c>plugins</c> folder: copy shipped <c>PluginSamples/ExampleToolsPlugin.cs</c>.</summary>
    private static void TrySeedBundledPluginSample(string pluginsDir)
    {
        try
        {
            if (Directory.GetFiles(pluginsDir, "*.cs", SearchOption.TopDirectoryOnly).Length > 0)
                return;
            var bundled = Path.Combine(AppContext.BaseDirectory, "PluginSamples", "ExampleToolsPlugin.cs");
            var dest = Path.Combine(pluginsDir, "ExampleToolsPlugin.cs");
            if (!File.Exists(bundled) || File.Exists(dest))
                return;
            File.Copy(bundled, dest);
        }
        catch
        {
            /* Bundled sample is optional. */
        }
    }
}
