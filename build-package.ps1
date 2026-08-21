# Build script for Poiyomi Pro VPM package
# Creates a VPM-compatible zip that targets a specific Pro version

param(
    [Parameter(Mandatory=$true)]
    [string]$TargetVersion,

    # Allows packaging a dry-run build for field testing. CI never passes this, so a
    # debug build still cannot reach a release. Output is suffixed -debug.
    [switch]$AllowDebugBuild
)

$packageName = "com.poiyomi.pro"
$outputDir = "dist"

Write-Host "Building Poiyomi Pro VPM package for version $TargetVersion" -ForegroundColor Cyan

# Create output directory
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

# Create temporary directory for package contents
$tempDir = New-TemporaryFile | ForEach-Object { Remove-Item $_; New-Item -ItemType Directory -Path $_ }

# Copy package structure
Write-Host "Copying package files..." -ForegroundColor Yellow

# Copy Editor scripts
$editorSrc = "Assets\_PoiyomiPro\Editor"

# A dry-run build installs nothing - never let one reach a release by accident
$installerSource = Get-Content (Join-Path $editorSrc "PoiyomiProInstaller.cs") -Raw
$isDebugBuild = $installerSource -notmatch 'DEBUG_MODE\s*=\s*DebugMode\.Off\s*;'

if ($isDebugBuild -and -not $AllowDebugBuild) {
    Write-Error ("DEBUG_MODE is not DebugMode.Off in PoiyomiProInstaller.cs. " +
        "Refusing to build a dry-run package. Pass -AllowDebugBuild to package one for field testing.")
    exit 1
}

$editorDest = Join-Path $tempDir "Editor"
New-Item -ItemType Directory -Force -Path $editorDest | Out-Null

Get-ChildItem -Path $editorSrc -File | ForEach-Object {
    $destPath = Join-Path $editorDest $_.Name

    if ($_.Name -eq "PoiyomiProInstaller.cs") {
        # Stamp the TARGET_VERSION fallback in PoiyomiProConfig. At runtime the installer
        # prefers the version in package.json; this only matters if that can't be read.
        $content = Get-Content $_.FullName -Raw
        $content = $content -replace 'public const string TARGET_VERSION = "latest"', "public const string TARGET_VERSION = `"$TargetVersion`""
        Set-Content -Path $destPath -Value $content -NoNewline
        Write-Host "  - Updated PoiyomiProInstaller.cs with version $TargetVersion" -ForegroundColor Green
    }
    else {
        Copy-Item $_.FullName -Destination $destPath
    }
}

# Update and copy package.json with version
$packageJson = Get-Content "package.json" -Raw | ConvertFrom-Json
$packageJson.version = $TargetVersion
$packageJson | ConvertTo-Json -Depth 10 | Set-Content -Path (Join-Path $tempDir "package.json") -NoNewline
Write-Host "  - Set package version to $TargetVersion" -ForegroundColor Green

# Create the VPM zip package. Debug builds get their own filename so one can never be
# mistaken for - or uploaded in place of - a release.
$suffix = if ($isDebugBuild) { "-debug" } else { "" }
$outputPath = Join-Path $outputDir "$packageName-$TargetVersion$suffix.zip"

if ($isDebugBuild) {
    Write-Host "  ! DRY-RUN BUILD - this package installs nothing. Do not publish it." -ForegroundColor Red
}

if (Test-Path $outputPath) {
    Remove-Item $outputPath -Force
}

Write-Host "Creating zip package..." -ForegroundColor Yellow
Compress-Archive -Path "$tempDir\*" -DestinationPath $outputPath -Force

# Clean up temp directory
Remove-Item -Recurse -Force $tempDir

# Output summary
$fileSize = (Get-Item $outputPath).Length
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "VPM Package Built Successfully!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Package: $outputPath"
Write-Host "  Version: $TargetVersion"
Write-Host "  Size: $([math]::Round($fileSize / 1KB, 2)) KB"
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Upload this zip to your VPM repository"
Write-Host "  2. Update packages.json with the new version"
Write-Host ""
