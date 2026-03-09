#Requires -Version 5.0
$ErrorActionPreference = 'Stop'

$Repo    = "pawhite999/Emby-Commercial-Detection-and-Processing"
$Binary  = "commdetect"
$ApiUrl  = "https://api.github.com/repos/$Repo/releases/latest"

# ── Detect architecture ───────────────────────────────────────────────────────
$Arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }

$AssetName   = "$Binary-win-$Arch.exe"
$InstallDir  = Join-Path $env:LOCALAPPDATA "Programs\commdetect"
$InstallPath = Join-Path $InstallDir "$Binary.exe"

Write-Host "Detected platform: win-$Arch"
Write-Host "Fetching latest release info..."

# ── Get release metadata ──────────────────────────────────────────────────────
$Release = Invoke-RestMethod -Uri $ApiUrl -UseBasicParsing
$Asset   = $Release.assets | Where-Object { $_.name -eq $AssetName }

if (-not $Asset) {
    Write-Error "No release asset found for '$AssetName'."
    Write-Host "Available assets:"
    $Release.assets | ForEach-Object { Write-Host "  $($_.name)" }
    exit 1
}

# ── Download ──────────────────────────────────────────────────────────────────
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

Write-Host "Downloading $AssetName..."
Invoke-WebRequest -Uri $Asset.browser_download_url -OutFile $InstallPath -UseBasicParsing

# ── Add to user PATH if needed ────────────────────────────────────────────────
$UserPath = [System.Environment]::GetEnvironmentVariable("PATH", "User")
if ($UserPath -notlike "*$InstallDir*") {
    [System.Environment]::SetEnvironmentVariable("PATH", "$InstallDir;$UserPath", "User")
    Write-Host ""
    Write-Host "Added $InstallDir to your user PATH."
    Write-Host "Restart your terminal for it to take effect."
}

Write-Host ""
Write-Host "Installed: $InstallPath"
Write-Host ""
Write-Host "Run 'commdetect --help' to get started."
