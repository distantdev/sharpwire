# Sharpwire Windows Releases + Auto-Update

This project ships Windows installer assets with Velopack and publishes them on GitHub Releases.

## Maintainer flow

1. Create and push a semantic version tag:
   - `git tag v0.1.0`
   - `git push origin v0.1.0`
2. GitHub Actions workflow `.github/workflows/release.yml` runs on tag push.
3. Workflow runs `scripts/build-velopack.ps1` and uploads installer/update assets.

## Local packaging

From repo root:

```powershell
./scripts/build-velopack.ps1 -Version 0.1.0 -Runtime win-x64 -Configuration Release
```

Output folder:

- `artifacts/velopack/release`

## App updater behavior

- Updater implementation: `Core/Update/VelopackUpdateService.cs`
- Settings model key:
  - `EnableAutoUpdateChecks`
- Update feed URL:
  - `https://github.com/distantdev/sharpwire`

In-app settings page (Updates section) allows:

- Check for updates
- Install update
- Enable/disable automatic background checks

On startup, when auto-check is enabled and a newer release is detected, a system chat note is posted.
