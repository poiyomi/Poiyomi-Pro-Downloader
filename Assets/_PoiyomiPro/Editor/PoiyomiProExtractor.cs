using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace Poiyomi.Pro
{
    /// <summary>
    /// Extracts downloaded Poiyomi Pro package to the VPM package directory.
    /// Supports both .zip and .unitypackage formats.
    /// </summary>
    public static class PoiyomiProExtractor
    {
        private static string cachedPackageDir = null;

        /// <summary>
        /// Sub-path (relative to the shaders root) that must not be extracted.
        /// ThryEditor is provided by its own VCC package, so the copy bundled inside
        /// the Poiyomi Pro package is skipped to avoid duplicate/conflicting scripts.
        /// </summary>
        private const string ExcludedSubPath = "Scripts/ThryEditor";

        /// <summary>
        /// Determines whether an asset path falls inside the excluded ThryEditor
        /// directory and should therefore be skipped during extraction. Handles both
        /// path separators and treats a ".meta" companion the same as its asset.
        /// 
        /// The asset path from the archive. May be absolute-under-Assets
        /// (e.g. "Assets/_PoiyomiShaders/Scripts/ThryEditor/...") or relative to the
        /// package root (e.g. "_PoiyomiShaders/Scripts/ThryEditor/...").
        /// </summary>
        private static bool ShouldExcludeFromExtraction(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return false;

            // Normalize separators so the check works for zip and unitypackage inputs.
            var normalized = assetPath.Replace('\\', '/');

            // Treat the ".meta" sidecar the same as the asset (or folder) it describes,
            // so the excluded folder's own meta file is dropped too.
            if (normalized.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(0, normalized.Length - ".meta".Length);

            // Exclude the ThryEditor folder itself and everything beneath it, whether or
            // not the path carries a leading segment (e.g. "Assets/" or "_PoiyomiShaders/").
            return normalized.Equals(ExcludedSubPath, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(ExcludedSubPath + "/", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/" + ExcludedSubPath, StringComparison.OrdinalIgnoreCase)
                || normalized.IndexOf("/" + ExcludedSubPath + "/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Extracts the downloaded package into this installer's package directory.
        /// </summary>
        public static async Task<bool> ExtractToPackageDirectory(string packagePath, bool deleteInstaller = true)
        {
            try
            {
                if (!File.Exists(packagePath))
                {
                    Debug.LogError($"[PoiyomiPro] Package file not found: {packagePath}");
                    return false;
                }

                // Find this package's directory
                var packageDir = FindPackageDirectory();
                if (string.IsNullOrEmpty(packageDir))
                {
                    Debug.LogWarning("[PoiyomiPro] Could not find package directory, falling back to Assets import");
                    return await FallbackToAssetsImport(packagePath);
                }

                // Cache the package directory for later deletion
                cachedPackageDir = packageDir;

                bool success = false;

                // Handle based on file type
                if (packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    success = await ExtractZipToDirectory(packagePath, packageDir);
                }
                else if (packagePath.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase))
                {
                    success = await ExtractUnityPackageToDirectory(packagePath, packageDir);
                }
                else
                {
                    Debug.LogWarning($"[PoiyomiPro] Unknown package format: {Path.GetExtension(packagePath)}, falling back to Assets import");
                    return await FallbackToAssetsImport(packagePath);
                }

                // If extraction succeeded and deleteInstaller is true, delete the installer files
                if (success && deleteInstaller)
                {
                    DeleteInstallerFiles();
                }

                return success;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PoiyomiPro] Failed to extract package: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// Reads the "version" field from a package.json inside a downloaded .zip.
        /// Returns null for other formats, or when the archive carries no readable manifest.
        /// </summary>
        public static string ReadPackageVersionFromArchive(string packagePath)
        {
            if (!packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            try
            {
                using (var archive = ZipFile.OpenRead(packagePath))
                {
                    // Prefer the manifest at the archive root, otherwise the shallowest one
                    ZipArchiveEntry manifest = null;
                    foreach (var entry in archive.Entries)
                    {
                        if (!entry.Name.Equals("package.json", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (entry.FullName.Equals("package.json", StringComparison.OrdinalIgnoreCase))
                        {
                            manifest = entry;
                            break;
                        }

                        if (manifest == null || EntryDepth(entry.FullName) < EntryDepth(manifest.FullName))
                        {
                            manifest = entry;
                        }
                    }

                    if (manifest == null)
                    {
                        return null;
                    }

                    using (var reader = new StreamReader(manifest.Open()))
                    {
                        return (string)JObject.Parse(reader.ReadToEnd())["version"];
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PoiyomiPro] Could not read version from downloaded package: {ex.Message}");
                return null;
            }
        }

        private static int EntryDepth(string entryPath)
        {
            var depth = 0;
            foreach (var c in entryPath)
            {
                if (c == '/') depth++;
            }
            return depth;
        }
        
        /// <summary>
        /// Deletes the installer stub files after successful installation.
        /// Can be called separately after closing the installer window.
        /// </summary>
        public static void DeleteInstallerFiles()
        {
            var packageDir = cachedPackageDir ?? FindPackageDirectory();
            if (string.IsNullOrEmpty(packageDir))
            {
                Debug.LogWarning("[PoiyomiPro] Could not find package directory to delete installer files");
                return;
            }

            var editorDir = Path.Combine(packageDir, "Editor");
            try
            {
                if (Directory.Exists(editorDir))
                {
                    Directory.Delete(editorDir, recursive: true);
                    Debug.Log("[PoiyomiPro] Deleted Editor folder");

                    // Also delete the meta file
                    var editorMetaPath = editorDir + ".meta";
                    if (File.Exists(editorMetaPath))
                    {
                        File.Delete(editorMetaPath);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PoiyomiPro] Could not delete Editor folder: {e.Message}");
            }

            // Trigger asset database refresh to pick up the changes
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// Finds the directory where this VPM package is installed.
        /// </summary>
        public static string FindPackageDirectory()
        {
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
        /// Extracts a .unitypackage file to the specified directory.
        /// Unity packages are gzipped tar archives with a specific structure.
        /// </summary>
        private static async Task<bool> ExtractUnityPackageToDirectory(string packagePath, string targetDir, bool allowFallback = true)
        {
            try
            {
                // Create temp directory for extraction
                var tempDir = Path.Combine(Path.GetTempPath(), "PoiyomiProExtract_" + DateTime.Now.Ticks);
                Directory.CreateDirectory(tempDir);

                try
                {
                    // .unitypackage is a gzipped tar archive
                    await Task.Run(() => ExtractTarGz(packagePath, tempDir));

                    // Process extracted content
                    var extractedFiles = 0;
                    var skippedFiles = 0;
                    var guidFolders = Directory.GetDirectories(tempDir);

                    foreach (var guidFolder in guidFolders)
                    {
                        var pathnamePath = Path.Combine(guidFolder, "pathname");
                        var assetPath = Path.Combine(guidFolder, "asset");

                        if (!File.Exists(pathnamePath) || !File.Exists(assetPath))
                            continue;

                        // Read the original asset path
                        var originalPath = File.ReadAllText(pathnamePath).Trim();

                        // Skip if not under Assets/ (shouldn't happen but safety check)
                        if (!originalPath.StartsWith("Assets/"))
                            continue;

                        // Skip ThryEditor - it's supplied by its own VCC package.
                        if (ShouldExcludeFromExtraction(originalPath))
                        {
                            skippedFiles++;
                            continue;
                        }

                        // Convert Assets/... path to package directory path
                        // e.g., "Assets/_PoiyomiShaders/..." -> "{packageDir}/_PoiyomiShaders/..."
                        var relativePath = originalPath.Substring("Assets/".Length);
                        var destPath = Path.Combine(targetDir, relativePath);
                        var destDir = Path.GetDirectoryName(destPath);
                        
                        // Create directory if needed
                        if (!Directory.Exists(destDir))
                        {
                            Directory.CreateDirectory(destDir);
                        }
                        
                        // Copy the asset file
                        File.Copy(assetPath, destPath, overwrite: true);
                        extractedFiles++;
                        
                        // Also copy .meta file if it exists
                        var metaPath = Path.Combine(guidFolder, "asset.meta");
                        if (File.Exists(metaPath))
                        {
                            File.Copy(metaPath, destPath + ".meta", overwrite: true);
                        }
                    }
                    
                    // Refresh to pick up new files
                    AssetDatabase.Refresh();

                    if (skippedFiles > 0)
                    {
                        Debug.Log($"[PoiyomiPro] Skipped {skippedFiles} {ExcludedSubPath} file(s); provided by ThryEditor's VCC package");
                    }

                    if (extractedFiles == 0)
                    {
                        Debug.LogWarning("[PoiyomiPro] No files were extracted from unitypackage");
                    }
                    return extractedFiles > 0;
                }
                finally
                {
                    // Clean up temp directory
                    if (Directory.Exists(tempDir))
                    {
                        try
                        {
                            Directory.Delete(tempDir, true);
                        }
                        catch (Exception cleanupEx)
                        {
                            Debug.LogWarning($"[PoiyomiPro] Failed to clean up temp directory: {cleanupEx.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // When invoked from the fallback itself, don't recurse - let the caller handle it.
                if (!allowFallback)
                    throw;
                Debug.LogWarning($"[PoiyomiPro] Failed to extract unitypackage: {ex.Message}, falling back to Assets import");
                return await FallbackToAssetsImport(packagePath);
            }
        }

        /// <summary>
        /// Extracts a tar.gz archive to a directory.
        /// </summary>
        private static void ExtractTarGz(string gzipPath, string outputDir)
        {
            using (var fileStream = File.OpenRead(gzipPath))
            using (var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress))
            {
                ExtractTar(gzipStream, outputDir);
            }
        }

        /// <summary>
        /// Extracts a tar archive from a stream.
        /// </summary>
        private static void ExtractTar(Stream stream, string outputDir)
        {
            var buffer = new byte[512];
            
            while (true)
            {
                // Read header
                var bytesRead = stream.Read(buffer, 0, 512);
                if (bytesRead < 512)
                    break;
                
                // Check for end of archive (two consecutive zero blocks)
                var allZero = true;
                for (int i = 0; i < 512; i++)
                {
                    if (buffer[i] != 0) { allZero = false; break; }
                }
                if (allZero)
                    break;
                
                // Parse filename (first 100 bytes, null-terminated)
                var nameBytes = new byte[100];
                Array.Copy(buffer, 0, nameBytes, 0, 100);
                var name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
                
                if (string.IsNullOrEmpty(name))
                    break;
                
                // Parse file size (octal, bytes 124-135)
                var sizeStr = Encoding.ASCII.GetString(buffer, 124, 11).Trim('\0', ' ');
                long size = 0;
                if (!string.IsNullOrEmpty(sizeStr))
                {
                    try { size = Convert.ToInt64(sizeStr, 8); } catch { }
                }
                
                // Type flag (byte 156)
                var typeFlag = (char)buffer[156];
                
                var outputPath = Path.Combine(outputDir, name);
                
                if (typeFlag == '5' || name.EndsWith("/"))
                {
                    // Directory
                    if (!Directory.Exists(outputPath))
                        Directory.CreateDirectory(outputPath);
                }
                else if (typeFlag == '0' || typeFlag == '\0')
                {
                    // Regular file
                    var dir = Path.GetDirectoryName(outputPath);
                    if (!Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    
                    using (var fileStream = File.Create(outputPath))
                    {
                        var remaining = size;
                        var fileBuffer = new byte[4096];
                        while (remaining > 0)
                        {
                            var toRead = (int)Math.Min(remaining, fileBuffer.Length);
                            var read = stream.Read(fileBuffer, 0, toRead);
                            if (read == 0) break;
                            fileStream.Write(fileBuffer, 0, read);
                            remaining -= read;
                        }
                    }
                }
                
                // Skip to next 512-byte boundary
                var remainder = size % 512;
                if (remainder > 0)
                {
                    var skip = 512 - remainder;
                    stream.Read(new byte[skip], 0, (int)skip);
                }
            }
        }

        /// <summary>
        /// Extracts a zip file directly to the package directory.
        /// </summary>
        private static async Task<bool> ExtractZipToDirectory(string zipPath, string targetDir)
        {
            try
            {
                var extractedCount = 0;
                var skippedCount = 0;
                await Task.Run(() =>
                {
                    using (var archive = ZipFile.OpenRead(zipPath))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name))
                                continue;

                            // Skip ThryEditor - it's supplied by its own VCC package.
                            if (ShouldExcludeFromExtraction(entry.FullName))
                            {
                                skippedCount++;
                                continue;
                            }

                            var destinationPath = Path.Combine(targetDir, entry.FullName);
                            var destinationDir = Path.GetDirectoryName(destinationPath);

                            if (!Directory.Exists(destinationDir))
                                Directory.CreateDirectory(destinationDir);

                            entry.ExtractToFile(destinationPath, overwrite: true);
                            extractedCount++;
                        }
                    }
                });

                Debug.Log($"[PoiyomiPro] Extracted {extractedCount} files from zip");
                if (skippedCount > 0)
                {
                    Debug.Log($"[PoiyomiPro] Skipped {skippedCount} {ExcludedSubPath} file(s); provided by ThryEditor VCC package");
                }
                AssetDatabase.Refresh();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PoiyomiPro] Failed to extract zip: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// Fallback used when the VPM package directory can't be found, or when the
        /// primary extraction failed: extracts the package manually into the Assets
        /// folder, always applying the ThryEditor exclusion.
        ///
        /// This never hands the archive to AssetDatabase.ImportPackage. That importer has
        /// no per-asset filter, and stripping ThryEditor after the fact is unreliable
        /// because importing its scripts triggers a domain reload that discards our
        /// callbacks. Copying the files ourselves means ThryEditor is simply never written.
        ///
        /// Dispatch: ".zip" uses the zip extractor; every other extension (including the
        /// downloader's default of ".unitypackage") is treated as a unitypackage. If that
        /// fails we fail closed - returning false rather than risk an unfiltered import.
        /// </summary>
        private static async Task<bool> FallbackToAssetsImport(string packagePath)
        {
            var assetsDir = Path.GetFullPath(Application.dataPath);

            try
            {
                if (packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.Log($"[PoiyomiPro] Extracting zip into Assets (excluding {ExcludedSubPath})");
                    return await ExtractZipToDirectory(packagePath, assetsDir);
                }

                Debug.Log($"[PoiyomiPro] Extracting unitypackage into Assets (excluding {ExcludedSubPath})");
                return await ExtractUnityPackageToDirectory(packagePath, assetsDir, allowFallback: false);
            }
            catch (Exception ex)
            {
                // Fail closed: never fall back to an unfiltered import that could pull in ThryEditor.
                Debug.LogError($"[PoiyomiPro] Manual extraction into Assets failed (ThryEditor exclusion enforced): {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }
    }
}
