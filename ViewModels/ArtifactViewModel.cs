using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Diagnostics;
using System;
using System.Threading.Tasks;

namespace Sharpwire.ViewModels;

/// <summary>
/// Uses explicit properties (not [ObservableProperty]) so analyzers/IDE match the compiler without relying on source-generated members.
/// </summary>
public class ArtifactViewModel : ObservableObject
{
    private string _fileName = string.Empty;
    private string _fullPath = string.Empty;
    private string _extension = string.Empty;
    private long _sizeBytes;
    private DateTime _lastModified;
    private bool _isDirectory;
    private bool _isExpanded;
    private Action? _pendingDirectoryLoad;

    /// <summary>Fake leaf so Fluent TreeView shows an expander before children are loaded (empty Children = no chevron).</summary>
    public bool IsPlaceholder { get; init; }

    public double LayoutMinHeight => IsPlaceholder ? 0 : 22;

    public string FileName
    {
        get => _fileName;
        set => SetProperty(ref _fileName, value);
    }

    public string FullPath
    {
        get => _fullPath;
        set => SetProperty(ref _fullPath, value);
    }

    public string Extension
    {
        get => _extension;
        set => SetProperty(ref _extension, value);
    }

    public long SizeBytes
    {
        get => _sizeBytes;
        set => SetProperty(ref _sizeBytes, value);
    }

    public DateTime LastModified
    {
        get => _lastModified;
        set => SetProperty(ref _lastModified, value);
    }

    public bool IsDirectory
    {
        get => _isDirectory;
        set => SetProperty(ref _isDirectory, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
                OnPropertyChanged(nameof(Icon));
        }
    }

    /// <summary>Runs once when the user expands this folder (loads real children, replacing the expand placeholder).</summary>
    public void SetPendingDirectoryLoad(Action? load) => _pendingDirectoryLoad = load;

    /// <summary>Invoked from the workspace <see cref="Avalonia.Controls.TreeViewItem"/> Expanded event (avoids two-way IsExpanded binding).</summary>
    public void RunPendingLoadIfAny()
    {
        if (_pendingDirectoryLoad == null) return;
        var load = _pendingDirectoryLoad;
        _pendingDirectoryLoad = null;
        load();
    }

    public ObservableCollection<ArtifactViewModel> Children { get; } = new();

    public string SizeFormatted
    {
        get
        {
            if (IsDirectory) return string.Empty;
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = SizeBytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }

    public string Icon => IsPlaceholder ? string.Empty : IsDirectory ? (IsExpanded ? "📂" : "📁") : "•";

    private IRelayCommand? _openFileCommand;
    public IRelayCommand OpenFileCommand => _openFileCommand ??= new RelayCommand(OpenFile);

    private IRelayCommand? _showInExplorerCommand;
    public IRelayCommand ShowInExplorerCommand => _showInExplorerCommand ??= new RelayCommand(ShowInExplorer);

    private IRelayCommand? _openWithCommand;
    public IRelayCommand OpenWithCommand => _openWithCommand ??= new RelayCommand(OpenWith);

    private IRelayCommand? _executeCommand;
    public IRelayCommand ExecuteCommand => _executeCommand ??= new RelayCommand(Execute);

    private IRelayCommand? _copyPathCommand;
    public IRelayCommand CopyPathCommand => _copyPathCommand ??= new RelayCommand(CopyPath);

    private IRelayCommand? _deleteCommand;
    public IRelayCommand DeleteCommand => _deleteCommand ??= new AsyncRelayCommand(DeleteAsync);
    public Func<Task<bool>>? ConfirmDeleteAsync { get; set; }

    public bool CanExecute => !IsDirectory && (Extension == ".py" || Extension == ".bat" || Extension == ".cmd" || Extension == ".ps1" || Extension == ".exe" || Extension == ".js");

    private void OpenFile()
    {
        if (IsPlaceholder || IsDirectory) return;
        try
        {
            Process.Start(new ProcessStartInfo(FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error opening file: {ex.Message}");
        }
    }

    private async Task DeleteAsync()
    {
        if (IsPlaceholder) return;
        if (ConfirmDeleteAsync is { } confirm)
        {
            var ok = await confirm().ConfigureAwait(true);
            if (!ok)
                return;
        }
        try
        {
            if (IsDirectory)
            {
                Directory.Delete(FullPath, true);
            }
            else
            {
                File.Delete(FullPath);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error deleting artifact: {ex.Message}");
        }
    }

    private void CopyPath()
    {
        if (IsPlaceholder) return;
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow?.Clipboard?.SetTextAsync(FullPath);
            }
        }
        catch { /* ignore */ }
    }

    private void ShowInExplorer()
    {
        if (IsPlaceholder) return;
        try
        {
            Process.Start("explorer.exe", $"/select,\"{FullPath}\"");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error showing in explorer: {ex.Message}");
        }
    }

    private void OpenWith()
    {
        if (IsPlaceholder || IsDirectory) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = $"shell32.dll,OpenAs_RunDLL {FullPath}",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error opening with: {ex.Message}");
        }
    }

    private void Execute()
    {
        if (IsPlaceholder || IsDirectory) return;
        try
        {
            if (Extension == ".py")
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/k python \"{FullPath}\"") { UseShellExecute = true });
            }
            else if (Extension == ".ps1")
            {
                Process.Start(new ProcessStartInfo("powershell.exe", $"-NoExit -File \"{FullPath}\"") { UseShellExecute = true });
            }
            else if (Extension == ".js")
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/k node \"{FullPath}\"") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"{FullPath}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error executing file: {ex.Message}");
        }
    }
}
