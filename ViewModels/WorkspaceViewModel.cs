using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;

namespace Sharpwire.ViewModels;

public class WorkspaceViewModel : IDisposable
{
    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false
    };

    public ObservableCollection<ArtifactViewModel> RootItems { get; } = new();

    public string WorkspacePath => _workspacePath;

    private readonly string _workspacePath;
    private readonly Func<ArtifactViewModel, Task<bool>>? _confirmDeleteAsync;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _refreshDebounceTimer;
    private bool _rootLoaded;

    public WorkspaceViewModel(string workspacePath, Func<ArtifactViewModel, Task<bool>>? confirmDeleteAsync = null)
    {
        _workspacePath = Path.GetFullPath(workspacePath);
        _confirmDeleteAsync = confirmDeleteAsync;
        if (!Directory.Exists(_workspacePath)) Directory.CreateDirectory(_workspacePath);
    }

    private IRelayCommand? _refreshWorkspaceCommand;
    public IRelayCommand RefreshWorkspaceCommand => _refreshWorkspaceCommand ??= new RelayCommand(() => RefreshWorkspace());

    /// <summary>Populate the tree and start the filesystem watcher. Call after the main window has a DataContext.</summary>
    public void LoadRootItems()
    {
        if (_rootLoaded)
            return;
        _rootLoaded = true;

        RootItems.Clear();
        PopulateOneLevel(_workspacePath, RootItems);
        InitializeWatcher();
    }

    private void RefreshWorkspace()
    {
        void ScheduleDebounce()
        {
            _refreshDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _refreshDebounceTimer.Stop();
            _refreshDebounceTimer.Tick -= OnDebouncedRefreshTick;
            _refreshDebounceTimer.Tick += OnDebouncedRefreshTick;
            _refreshDebounceTimer.Start();
        }

        if (Dispatcher.UIThread.CheckAccess())
            ScheduleDebounce();
        else
            Dispatcher.UIThread.Post(ScheduleDebounce);
    }

    private void OnDebouncedRefreshTick(object? sender, EventArgs e)
    {
        _refreshDebounceTimer?.Stop();
        _refreshDebounceTimer!.Tick -= OnDebouncedRefreshTick;
        RootItems.Clear();
        PopulateOneLevel(_workspacePath, RootItems);
    }

    /// <summary>One directory level; nested folders load when expanded.</summary>
    private void PopulateOneLevel(string path, ObservableCollection<ArtifactViewModel> collection)
    {
        if (!Directory.Exists(path))
            return;

        List<string> subdirPaths;
        List<string> filePaths;
        try
        {
            subdirPaths = Directory.EnumerateDirectories(path, "*", EnumerationOptions)
                .Where(p => !ShouldSkip(Path.GetFileName(p)))
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .ToList();
            filePaths = Directory.EnumerateFiles(path, "*", EnumerationOptions)
                .Where(p => !ShouldSkip(Path.GetFileName(p)))
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Workspace: cannot enumerate {path}: {ex.Message}");
            return;
        }

        foreach (var dirPath in subdirPaths)
        {
            try
            {
                var vm = CreateArtifact(dirPath, true);
                if (MayHaveEntries(dirPath))
                {
                    vm.Children.Add(CreateExpandPlaceholder());
                    var captured = vm;
                    var p = dirPath;
                    vm.SetPendingDirectoryLoad(() =>
                    {
                        captured.Children.Clear();
                        PopulateOneLevel(p, captured.Children);
                    });
                }

                collection.Add(vm);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Workspace: skip directory {dirPath}: {ex.Message}");
            }
        }

        foreach (var filePath in filePaths)
        {
            try
            {
                collection.Add(CreateArtifact(filePath, false));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Workspace: skip file {filePath}: {ex.Message}");
            }
        }
    }

    private static bool MayHaveEntries(string dirPath)
    {
        try
        {
            using var e = Directory.EnumerateFileSystemEntries(dirPath, "*", EnumerationOptions).GetEnumerator();
            return e.MoveNext();
        }
        catch
        {
            return false;
        }
    }

    private static ArtifactViewModel CreateExpandPlaceholder() =>
        new()
        {
            IsPlaceholder = true,
            FileName = string.Empty,
            FullPath = string.Empty,
            IsDirectory = false
        };

    private bool ShouldSkip(string name) => name.StartsWith(".") || name == "bin" || name == "obj";

    private ArtifactViewModel CreateArtifact(string path, bool isDir)
    {
        var info = new FileInfo(path);
        var vm = new ArtifactViewModel
        {
            FileName = info.Name,
            FullPath = info.FullName,
            IsDirectory = isDir,
            Extension = isDir ? "" : info.Extension.ToLower(),
            SizeBytes = isDir ? 0 : info.Length,
            LastModified = info.LastWriteTime
        };
        if (_confirmDeleteAsync != null)
            vm.ConfirmDeleteAsync = () => _confirmDeleteAsync(vm);
        return vm;
    }

    private void InitializeWatcher()
    {
        _watcher = new FileSystemWatcher(_workspacePath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };

        _watcher.Created += (_, _) => RefreshWorkspace();
        _watcher.Changed += (_, _) => RefreshWorkspace();
        _watcher.Deleted += (_, _) => RefreshWorkspace();
        _watcher.Renamed += (_, _) => RefreshWorkspace();
    }

    public void Dispose()
    {
        _refreshDebounceTimer?.Stop();
        _watcher?.Dispose();
    }
}
