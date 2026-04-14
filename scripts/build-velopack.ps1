param(
    [string]$Version,
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$ProjectPath = "Sharpwire.csproj",
    [string]$ArtifactsDir = "artifacts",
    [string]$PackId = "Sharpwire",
    [string]$PackTitle = "Sharpwire",
    [string]$MainExe = "Sharpwire.exe",
    [string]$IconPath = "Assets/icon.ico"
)

$ErrorActionPreference = "Stop"

function Resolve-Version([string]$inputVersion) {
    if (-not [string]::IsNullOrWhiteSpace($inputVersion)) {
        return $inputVersion.Trim().TrimStart("v")
    }

    if ($env:GITHUB_REF_TYPE -eq "tag" -and -not [string]::IsNullOrWhiteSpace($env:GITHUB_REF_NAME)) {
        return $env:GITHUB_REF_NAME.TrimStart("v")
    }

    throw "Version is required. Pass -Version x.y.z or run from a tag ref."
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$resolvedProjectPath = if ([System.IO.Path]::IsPathRooted($ProjectPath)) { $ProjectPath } else { Join-Path $repoRoot $ProjectPath }
$resolvedArtifacts = if ([System.IO.Path]::IsPathRooted($ArtifactsDir)) { $ArtifactsDir } else { Join-Path $repoRoot $ArtifactsDir }
$resolvedIconPath = if ([System.IO.Path]::IsPathRooted($IconPath)) { $IconPath } else { Join-Path $repoRoot $IconPath }
$version = Resolve-Version $Version

$publishDir = Join-Path $resolvedArtifacts "publish\$Runtime"
$releaseDir = Join-Path $resolvedArtifacts "velopack\release"

if (Test-Path $resolvedArtifacts) {
    Remove-Item $resolvedArtifacts -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

Write-Host "Publishing $resolvedProjectPath ($Configuration/$Runtime)..."
dotnet publish $resolvedProjectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=false `
    -p:PublishTrimmed=false `
    -o $publishDir `
    -p:Version=$version | Out-Host

if (-not (Test-Path $publishDir)) {
    throw "Publish output missing: $publishDir"
}

$vpk = Get-Command vpk -ErrorAction SilentlyContinue
if ($null -eq $vpk) {
    throw "Velopack CLI (vpk) not found in PATH. Install with: dotnet tool install --global vpk"
}

Write-Host "Packing Velopack installer..."
$vpkPackArgs = @(
    "pack"
    "--packId", $PackId
    "--packVersion", $version
    "--packTitle", $PackTitle
    "--packDir", $publishDir
    "--mainExe", $MainExe
    "--outputDir", $releaseDir
)

if (Test-Path $resolvedIconPath) {
    Write-Host "Using installer icon: $resolvedIconPath"
    $vpkPackArgs += @("--icon", $resolvedIconPath)
}
else {
    Write-Warning "Installer icon not found at '$resolvedIconPath'. Continuing without --icon."
}

& $vpk.Source @vpkPackArgs | Out-Host

Write-Host ""
Write-Host "Velopack artifacts:"
Get-ChildItem $releaseDir | ForEach-Object { Write-Host " - $($_.FullName)" }
