using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Sharpwire.Core.Update;

public sealed record UpdateCheckResult(bool IsUpdateAvailable, string CurrentVersion, string LatestVersion, string Message);
public sealed record UpdateApplyResult(bool Started, string Message);

public interface IAppUpdateService
{
    bool IsSupported { get; }
    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct);
    Task<UpdateApplyResult> ApplyUpdatesAsync(CancellationToken ct);
}

public sealed class VelopackUpdateService : IAppUpdateService
{
    private const string UpdateRepoUrl = "https://github.com/distantdev/sharpwire";
    private UpdateInfo? _pendingUpdate;

    public VelopackUpdateService() { }

    public bool IsSupported => OperatingSystem.IsWindows();

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct)
    {
        var current = NormalizeVersion(GetCurrentVersion());
        if (!IsSupported)
            return new UpdateCheckResult(false, current, current, "Auto-update is currently supported on Windows only.");

        var source = new GithubSource(UpdateRepoUrl, "", false, null);
        var mgr = new UpdateManager(source);
        var update = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
        _pendingUpdate = update;
        if (update == null)
            return new UpdateCheckResult(false, current, current, $"You are up to date ({current}).");

        var latest = NormalizeVersion(update.TargetFullRelease.Version.ToString());
        return new UpdateCheckResult(true, current, latest, $"Update available: {latest} (current: {current}).");
    }

    public async Task<UpdateApplyResult> ApplyUpdatesAsync(CancellationToken ct)
    {
        if (!IsSupported)
            return new UpdateApplyResult(false, "Auto-update apply is supported on Windows only.");

        var source = new GithubSource(UpdateRepoUrl, "", false, null);
        var mgr = new UpdateManager(source);
        try
        {
            var update = _pendingUpdate ?? await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update == null)
                return new UpdateApplyResult(false, "No update available.");

            await mgr.DownloadUpdatesAsync(update, null, ct).ConfigureAwait(false);
            mgr.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: true, Array.Empty<string>());
            return new UpdateApplyResult(true, "Update downloaded. App will restart to apply.");
        }
        catch (Exception ex)
        {
            return new UpdateApplyResult(false, $"Failed to apply update: {ex.Message}");
        }
    }

    private static string GetCurrentVersion() =>
        NormalizeVersion(typeof(VelopackUpdateService).Assembly.GetName().Version?.ToString() ?? "0.0.0");

    private static string NormalizeVersion(string value)
    {
        var v = (value ?? string.Empty).Trim();
        if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            v = v[1..];
        return v;
    }
}
