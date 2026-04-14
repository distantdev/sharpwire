using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.ComponentModel;
using Microsoft.Extensions.AI;
using Sharpwire.Core.Hooks;

namespace Sharpwire.Core.MetaToolbox;

public class PluginCompilerService
{
    private readonly List<string> _pluginsPaths;
    private readonly object _resolverLock = new();
    private Dictionary<string, string> _pluginDependencyAssemblyPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _resolverRegistered;

    public PluginCompilerService(IEnumerable<string> pluginsPaths)
    {
        _pluginsPaths = pluginsPaths.Select(Path.GetFullPath).ToList();
        foreach (var path in _pluginsPaths)
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        }

        EnsureAssemblyResolverRegistered();
    }

    public Assembly? CompilePlugins()
    {
        var files = _pluginsPaths
            .SelectMany(path => Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories))
            .ToArray();
        if (files.Length == 0) return null;

        var syntaxTrees = files.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f))).ToArray();

        var referencePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var references = new List<MetadataReference>();

        void AddAssembly(Assembly? assembly)
        {
            if (assembly == null || assembly.IsDynamic || string.IsNullOrWhiteSpace(assembly.Location))
                return;
            if (!referencePaths.Add(assembly.Location))
                return;
            references.Add(MetadataReference.CreateFromFile(assembly.Location));
        }

        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            AddAssembly(a);

        AddAssembly(typeof(Console).Assembly);
        AddAssembly(typeof(ILifecycleHookMiddleware).Assembly);

        var pluginDependencyPaths = DiscoverPluginDependencyDlls(files);
        foreach (var dllPath in pluginDependencyPaths)
        {
            if (!referencePaths.Add(dllPath))
                continue;

            references.Add(MetadataReference.CreateFromFile(dllPath));
        }

        var compilation = CSharpCompilation.Create(
            $"SharpwirePlugins_{Guid.NewGuid():N}",
            syntaxTrees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        if (!result.Success)
        {
            var errors = result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.GetMessage());
            
            throw new Exception($"Compilation failed: {string.Join("; ", errors)}");
        }

        UpdateDependencyResolverMap(pluginDependencyPaths);

        ms.Seek(0, SeekOrigin.Begin);
        return Assembly.Load(ms.ToArray());
    }

    public (Dictionary<string, AITool> Tools, List<PluginSettingInfo> PluginSettings, Dictionary<Type, object> InstancesByType, List<ILifecycleHookMiddleware> LifecycleHooks) ExtractPlugins(Assembly assembly)
    {
        var tools = new Dictionary<string, AITool>(StringComparer.OrdinalIgnoreCase);
        var settingsList = new List<PluginSettingInfo>();
        var instancesByType = new Dictionary<Type, object>();
        var lifecycleHooks = new List<ILifecycleHookMiddleware>();

        foreach (var type in assembly.GetTypes())
        {
            object? sharedInstance = null;

            object? GetOrCreateInstance()
            {
                if (sharedInstance != null)
                    return sharedInstance;

                if (instancesByType.TryGetValue(type, out var existing))
                {
                    sharedInstance = existing;
                    return sharedInstance;
                }

                sharedInstance = Activator.CreateInstance(type);
                if (sharedInstance != null)
                    instancesByType[type] = sharedInstance;
                return sharedInstance;
            }

            var methods = type.GetMethods().Where(m => m.GetCustomAttribute<DescriptionAttribute>() != null).ToList();
            if (methods.Count > 0)
            {
                try
                {
                    var instance = GetOrCreateInstance();
                    if (instance != null)
                    {
                        foreach (var method in methods)
                        {
                            var tool = AIFunctionFactory.Create(method, instance);
                            tools[tool.Name] = tool;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to instantiate plugin type {type.Name} for tools: {ex.Message}");
                }
            }

            var settingsAttr = type.GetCustomAttribute<PluginSettingsAttribute>();
            if (settingsAttr != null)
            {
                var info = new PluginSettingInfo
                {
                    PluginName = settingsAttr.PluginName,
                    TypeName = type.FullName ?? type.Name
                };

                foreach (var prop in type.GetProperties())
                {
                    var propAttr = prop.GetCustomAttribute<PluginSettingAttribute>();
                    if (propAttr != null)
                    {
                        info.Properties.Add(new PluginPropertyInfo
                        {
                            PropertyName = prop.Name,
                            Label = propAttr.Label,
                            Description = propAttr.Description,
                            IsSecret = propAttr.IsSecret,
                            PropertyType = prop.PropertyType
                        });
                    }
                }

                if (info.Properties.Count > 0)
                {
                    settingsList.Add(info);
                    if (!instancesByType.ContainsKey(type) && typeof(IPluginWithSettings).IsAssignableFrom(type))
                    {
                        try
                        {
                            GetOrCreateInstance();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to instantiate plugin type {type.Name} for settings: {ex.Message}");
                        }
                    }
                }
            }

            if (typeof(ILifecycleHookMiddleware).IsAssignableFrom(type))
            {
                try
                {
                    var inst = GetOrCreateInstance();
                    if (inst is ILifecycleHookMiddleware hook)
                        lifecycleHooks.Add(hook);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to instantiate plugin type {type.Name} for lifecycle hooks: {ex.Message}");
                }
            }
        }

        return (tools, settingsList, instancesByType, lifecycleHooks);
    }

    private static IReadOnlyList<string> DiscoverPluginDependencyDlls(IEnumerable<string> pluginSourceFiles)
    {
        var dlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourceFile in pluginSourceFiles)
        {
            var sourceDir = Path.GetDirectoryName(sourceFile);
            if (string.IsNullOrWhiteSpace(sourceDir))
                continue;

            var libDir = Path.Combine(sourceDir, "lib");
            if (!Directory.Exists(libDir))
                continue;

            foreach (var dll in Directory.GetFiles(libDir, "*.dll", SearchOption.TopDirectoryOnly))
                dlls.Add(Path.GetFullPath(dll));
        }

        return dlls.ToList();
    }

    private void EnsureAssemblyResolverRegistered()
    {
        lock (_resolverLock)
        {
            if (_resolverRegistered)
                return;

            AssemblyLoadContext.Default.Resolving += ResolvePluginDependency;
            _resolverRegistered = true;
        }
    }

    private Assembly? ResolvePluginDependency(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        var simpleName = assemblyName.Name;
        if (string.IsNullOrWhiteSpace(simpleName))
            return null;

        string? path = null;
        lock (_resolverLock)
        {
            _pluginDependencyAssemblyPaths.TryGetValue(simpleName, out path);
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            return context.LoadFromAssemblyPath(path);
        }
        catch
        {
            return null;
        }
    }

    private void UpdateDependencyResolverMap(IReadOnlyList<string> pluginDependencyPaths)
    {
        var updated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dllPath in pluginDependencyPaths)
        {
            var simpleName = Path.GetFileNameWithoutExtension(dllPath);
            if (string.IsNullOrWhiteSpace(simpleName))
                continue;
            updated[simpleName] = dllPath;
        }

        lock (_resolverLock)
        {
            _pluginDependencyAssemblyPaths = updated;
        }
    }
}

public class PluginSettingInfo
{
    public string PluginName { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public List<PluginPropertyInfo> Properties { get; } = new();
}

public class PluginPropertyInfo
{
    public string PropertyName { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsSecret { get; set; }
    public Type PropertyType { get; set; } = typeof(string);
}
