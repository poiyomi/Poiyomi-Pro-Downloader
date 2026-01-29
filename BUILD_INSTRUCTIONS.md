# Building VPM Packages for Poiyomi Pro

## Overview

Each VPM package targets a specific version of Poiyomi Pro. When users install the package from VCC (VRChat Creator Companion), it will authenticate with Patreon and download that exact version.

## Manual Build

```powershell
.\build-package.ps1 -TargetVersion "10.2.0"
```

This creates: `dist/com.poiyomi.pro-10.2.0.zip`

## Automated Build (GitHub Action)

When you publish a new release on the PoiyomiPatreon repo, the GitHub Action automatically:
1. Builds the VPM package with that version
2. Uploads it to the VPM repository
3. Updates the package listing

## Package Structure

The VPM zip contains:
```
com.poiyomi.pro-X.X.X.zip
├── Editor/
│   ├── PoiyomiPro.Editor.asmdef
│   ├── PoiyomiProInstaller.cs (with version baked in)
│   ├── PoiyomiProAuth.cs
│   ├── PoiyomiProDownloader.cs
│   ├── PoiyomiProExtractor.cs
│   └── PoiyomiProMenu.cs
├── DO_NOT_DELETE.txt
├── package.json (version set to X.X.X)
├── CHANGELOG.md
└── README.md
```

## User Experience

1. User adds Poiyomi VPM repo to VCC
2. User sees "Poiyomi Pro 10.2.0" in available packages
3. User clicks Install
4. Package auto-runs, opens browser for Patreon auth
5. After auth, downloads Poiyomi Pro 10.2.0 specifically
6. User can downgrade by installing older version from VCC

## Version Strategy

- VPM package version = Pro shader version
- e.g., VPM package v10.2.0 downloads Pro v10.2.0
- Users always know exactly what version they're getting
