using System;
using System.IO;
using System.Security;
using System.Threading.Tasks;
using System.Linq;
using System.ComponentModel;
using Sharpwire.Core;
using Sharpwire.Core.Tools;

namespace Sharpwire.Core.Plugins;

public class FileIOPlugin
{
    private readonly string _workspaceRoot;
    private readonly ToolApprovalCoordinator? _approval;

    public FileIOPlugin(string workspacePath, ToolApprovalCoordinator? approval = null)
    {
        _workspaceRoot = WorkspacePathGuard.NormalizeWorkspaceRoot(workspacePath);
        _approval = approval;
    }

    [Description("Writes content to a file in the workspace directory.")]
    public async Task<string> WriteFile(
        [Description("Relative path from workspace root.")] string path,
        [Description("The content to write.")] string content)
    {
        try
        {
            var fullPath = WorkspacePathGuard.GetFullPathUnderRoot(_workspaceRoot, path);

            if (_approval != null)
            {
                var ok = await _approval.EnsureApprovedAsync("WriteFile",
                    $"Write file: {path} ({content.Length} chars)").ConfigureAwait(false);
                if (!ok)
                    return "Write cancelled — user did not approve this high-impact action.";
            }

            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(fullPath, content);
            return $"Successfully wrote to {path}";
        }
        catch (SecurityException ex)
        {
            return $"Error: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error writing file: {ex.Message}";
        }
    }

    [Description("Reads content from a file in the workspace directory.")]
    public async Task<string> ReadFile(
        [Description("Relative path from workspace root.")] string path)
    {
        try
        {
            var fullPath = WorkspacePathGuard.GetFullPathUnderRoot(_workspaceRoot, path);

            if (!File.Exists(fullPath)) return $"Error: File not found: {path}";

            return await File.ReadAllTextAsync(fullPath);
        }
        catch (SecurityException ex)
        {
            return $"Error: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error reading file: {ex.Message}";
        }
    }

    [Description("Lists files and directories in the workspace directory.")]
    public string ListFiles(
        [Description("Relative path to directory to list.")] string path = ".")
    {
        try
        {
            var fullPath = WorkspacePathGuard.GetFullPathUnderRoot(_workspaceRoot, path);

            if (!Directory.Exists(fullPath)) return $"Error: Directory not found: {path}";

            var rootTrim = _workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var entries = Directory.GetFileSystemEntries(fullPath)
                .Select(e => Path.GetRelativePath(rootTrim, e));

            return string.Join("\n", entries);
        }
        catch (SecurityException ex)
        {
            return $"Error: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error listing files: {ex.Message}";
        }
    }
}
