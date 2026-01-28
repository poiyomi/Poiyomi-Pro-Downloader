# Poiyomi Pro Downloader

VPM installer package for Poiyomi Pro shaders.

## How it works

1. Install this package via VCC (VRChat Creator Companion)
2. Open Unity - the installer window appears automatically
3. Authenticate with your Patreon account ($10+ tier required)
4. The full Poiyomi Pro shader package is downloaded and installed

## Requirements

- Unity 2019.4 or later
- VRChat Creator Companion
- Patreon account with active $10+ pledge to Poiyomi

## For Developers

This repo hosts the installer that authenticates users and downloads the actual Pro shaders from the private repository.

### Manual Build

```powershell
.\build-package.ps1 -TargetVersion "10.2.0"
```

### Automated Builds

Releases are automatically created when a new version of Poiyomi Pro is published. The GitHub Action:

1. Receives trigger from PoiyomiPatreon release
2. Builds installer zip targeting that version
3. Creates a release with the zip attached
4. Triggers VPM repo rebuild

## Support

For shader issues, visit [poiyomi.com](https://poiyomi.com)
