# Build script for Poiyomi Pro VPM package
# Creates a VPM-compatible zip that targets a specific Pro version

param(
    [Parameter(Mandatory=$true)]
    [string]$TargetVersion
)

$packageName = "com.poiyomi.pro"
$outputDir = "dist"

Write-Host "Building Poiyomi Pro VPM package for version $TargetVersion" -ForegroundColor Cyan

# Create output directory
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

# Create temporary directory for package contents
$tempDir = New-TemporaryFile | %{ Remove-Item $_; New-Item -ItemType Directory -Path $_ }

# Copy package structure
Write-Host "Copying package files..." -ForegroundColor Yellow

# Copy Editor scripts
$editorSrc = "Assets\_PoiyomiPro\Editor"
$editorDest = Join-Path $tempDir "Editor"
New-Item -ItemType Directory -Force -Path $editorDest | Out-Null

Get-ChildItem -Path $editorSrc -File | ForEach-Object {
    $destPath = Join-Path $editorDest $_.Name
    
    if ($_.Name -eq "PoiyomiProInstaller.cs") {
        # Replace TARGET_VERSION placeholder with actual version
        $content = Get-Content $_.FullName -Raw
        $content = $content -replace 'private const string TARGET_VERSION = "latest"', "private const string TARGET_VERSION = `"$TargetVersion`""
        Set-Content -Path $destPath -Value $content -NoNewline
        Write-Host "  - Updated PoiyomiProInstaller.cs with version $TargetVersion" -ForegroundColor Green
    }
    else {
        Copy-Item $_.FullName -Destination $destPath
    }
}

# Copy marker file
$markerSrc = "Assets\_PoiyomiPro\DO_NOT_DELETE.txt"
if (Test-Path $markerSrc) {
    Copy-Item $markerSrc -Destination $tempDir
}

# Update and copy package.json with version
$packageJson = Get-Content "package.json" -Raw | ConvertFrom-Json
$packageJson.version = $TargetVersion
$packageJson | ConvertTo-Json -Depth 10 | Set-Content -Path (Join-Path $tempDir "package.json") -NoNewline
Write-Host "  - Set package version to $TargetVersion" -ForegroundColor Green

# Update and copy CHANGELOG.md
$changelog = Get-Content "CHANGELOG.md" -Raw
$date = Get-Date -Format "yyyy-MM-dd"
$changelog = $changelog -replace '\[0\.0\.0\] - Template', "[$TargetVersion] - $date"
$changelog = $changelog -replace 'This version placeholder is replaced during the build process\.', "Downloads Poiyomi Pro $TargetVersion after Patreon authentication."
Set-Content -Path (Join-Path $tempDir "CHANGELOG.md") -Value $changelog -NoNewline

# Copy README
Copy-Item "README.md" -Destination $tempDir

# Create the VPM zip package
$outputPath = Join-Path $outputDir "$packageName-$TargetVersion.zip"

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
