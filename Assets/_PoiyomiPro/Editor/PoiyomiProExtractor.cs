using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Poiyomi.Pro
{
    /// <summary>
    /// Extracts downloaded Poiyomi Pro package to the VPM package directory.
    /// This keeps all shader files within the package rather than polluting the Assets folder.
    /// </summary>
    public static class PoiyomiProExtractor
    {
        /// <summary>
        /// Extracts the downloaded package into this installer's package directory.
        /// </summary>
        public static async Task<bool> ExtractToPackageDirectory(string packagePath)
        {
            try
            {
                if (!File.Exists(packagePath))
                {
                    Debug.LogError($"[Poiyomi Pro] Package file not found: {packagePath}");
                    return false;
                }

                var fileInfo = new FileInfo(packagePath);
                Debug.Log($"[Poiyomi Pro] Processing package: {fileInfo.Length / 1024.0 / 1024.0:F2} MB");

                // Find this package's directory
                var packageDir = FindPackageDirectory();
                if (string.IsNullOrEmpty(packageDir))
                {
                    Debug.LogWarning("[Poiyomi Pro] Could not find package directory, falling back to Assets import");
                    return await FallbackToAssetsImport(packagePath);
                }

                Debug.Log($"[Poiyomi Pro] Extracting to package directory: {packageDir}");

                // Check if it's a zip or unitypackage
                if (packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    return await ExtractZipToDirectory(packagePath, packageDir);
                }
                else if (packagePath.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase))
                {
                    // For .unitypackage files, we need to use Unity's importer
                    // as they have a complex internal structure
                    Debug.Log("[Poiyomi Pro] Unitypackage detected - using Unity importer");
                    return await FallbackToAssetsImport(packagePath);
                }
                else
                {
                    Debug.LogWarning($"[Poiyomi Pro] Unknown package format: {packagePath}");
                    return await FallbackToAssetsImport(packagePath);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Poiyomi Pro] Extraction failed: {e.Message}");
                Debug.LogException(e);
                return false;
            }
        }

        /// <summary>
        /// Finds the directory where this VPM package is installed.
        /// </summary>
        private static string FindPackageDirectory()
        {
            // Check Packages folder first (local packages)
            var packagesPath = Path.Combine(Application.dataPath, "..", "Packages");
            var possibleNames = new[] { "com.poiyomi.pro", "com.poiyomi.pro.installer" };
            
            foreach (var name in possibleNames)
            {
                var localPath = Path.Combine(packagesPath, name);
                if (Directory.Exists(localPath))
                {
                    return Path.GetFullPath(localPath);
                }
            }

            // Check PackageCache (cached VPM packages)
            var packageCachePath = Path.Combine(Application.dataPath, "..", "Library", "PackageCache");
            if (Directory.Exists(packageCachePath))
            {
                foreach (var name in possibleNames)
                {
                    var dirs = Directory.GetDirectories(packageCachePath, $"{name}@*");
                    if (dirs.Length > 0)
                    {
                        return Path.GetFullPath(dirs[0]);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Extracts a zip file directly to the package directory.
        /// </summary>
        private static async Task<bool> ExtractZipToDirectory(string zipPath, string targetDir)
        {
            try
            {
                await Task.Run(() =>
                {
                    using (var archive = ZipFile.OpenRead(zipPath))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            // Skip directory entries
                            if (string.IsNullOrEmpty(entry.Name))
                                continue;

                            var destinationPath = Path.Combine(targetDir, entry.FullName);
                            var destinationDir = Path.GetDirectoryName(destinationPath);

                            // Ensure directory exists
                            if (!Directory.Exists(destinationDir))
                            {
                                Directory.CreateDirectory(destinationDir);
                            }

                            // Extract file (overwrite if exists)
                            entry.ExtractToFile(destinationPath, overwrite: true);
                        }
                    }
                });

                Debug.Log("[Poiyomi Pro] Zip extraction completed successfully!");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Poiyomi Pro] Zip extraction failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Fallback to Unity's built-in package importer (extracts to Assets folder).
        /// </summary>
        private static async Task<bool> FallbackToAssetsImport(string packagePath)
        {
            try
            {
                Debug.Log("[Poiyomi Pro] Using Unity's package importer...");
                
                // Use Unity's built-in importer
                AssetDatabase.ImportPackage(packagePath, false);
                
                Debug.Log("[Poiyomi Pro] Package import initiated");
                
                await Task.Delay(500);
                AssetDatabase.Refresh();

                Debug.Log("[Poiyomi Pro] Import completed successfully!");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Poiyomi Pro] Import failed: {e.Message}");
                return false;
            }
        }
    }
}
