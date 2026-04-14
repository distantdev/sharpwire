using System;
using System.IO;
using System.Diagnostics;

namespace Sharpwire.Core;

public class GitBackstop
{
    private readonly string _workspacePath;
    private readonly string _gitDir;

    public GitBackstop(string workspacePath)
    {
        _workspacePath = Path.GetFullPath(workspacePath);
        _gitDir = Path.Combine(_workspacePath, ".sharpwire", "git");
    }

    public void Initialize()
    {
        try
        {
            if (!Directory.Exists(_gitDir))
            {
                var dirName = Path.GetDirectoryName(_gitDir);
                if (dirName != null) Directory.CreateDirectory(dirName);
                RunGit("init");
                // Set user name/email if not set globally to avoid errors
                RunGit("config user.name \"Sharpwire\"");
                RunGit("config user.email \"sharpwire@local\"");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Git initialization failed: {ex.Message}");
        }
    }

    public void Commit(string message)
    {
        try
        {
            RunGit("add .");
            RunGit($"commit -m \"{message}\" --allow-empty");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Git commit failed: {ex.Message}");
        }
    }

    private void RunGit(string args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"--git-dir=\"{_gitDir}\" --work-tree=\"{_workspacePath}\" {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workspacePath
        };

        using var process = Process.Start(startInfo);
        process?.WaitForExit();
    }
}
