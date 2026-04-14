using System;
using System.IO;
using System.Security;

namespace Sharpwire.Core;

public static class WorkspacePathGuard
{
    public static string NormalizeWorkspaceRoot(string workspacePath)
    {
        var trimmed = workspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(trimmed);
        return full + Path.DirectorySeparatorChar;
    }

    /// <summary>Resolves a user-supplied path segment under the workspace; throws if it escapes the root.</summary>
    public static string GetFullPathUnderRoot(string normalizedRootWithTrailingSep, string relativePath)
    {
        var root = normalizedRootWithTrailingSep.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootFull = Path.GetFullPath(root);
        var combined = Path.Combine(rootFull, relativePath);
        var candidate = Path.GetFullPath(combined);

        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (candidate.Equals(rootFull, comparison) || candidate.StartsWith(prefix, comparison))
            return candidate;

        throw new SecurityException("Path resolves outside the workspace root.");
    }
}
