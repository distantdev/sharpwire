using Microsoft.Extensions.DependencyInjection;
using Sharpwire.Core;
using Sharpwire.Core.Agents;
using Sharpwire.Core.MetaToolbox;
using Sharpwire.Core.Secrets;
using Sharpwire.Core.Session;
using Sharpwire.Core.Tools;
using Sharpwire.Core.Update;

namespace Sharpwire;

public static class ServiceRegistration
{
    public static IServiceCollection AddSharpwireServices(this IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton<LlmApiKeyStore>();
        services.AddSingleton(_ => new SettingsManager(paths.WorkspaceDirectory));
        services.AddSingleton<IStateStore, FileStateStore>();
        services.AddSingleton<AgentSession>();
        services.AddSingleton<ToolApprovalCoordinator>();
        services.AddSingleton(_ => new GitBackstop(paths.WorkspaceDirectory));
        services.AddSingleton(_ => new PluginCompilerService(new[] { paths.PluginsDirectory, paths.GlobalPluginsDirectory }));
        services.AddSingleton<IAppUpdateService, VelopackUpdateService>();
        services.AddSingleton<AgentService>();
        services.AddSingleton<MainWindow>();
        return services;
    }
}
