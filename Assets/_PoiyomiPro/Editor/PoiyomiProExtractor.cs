using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Poiyomi.Pro
{
    /// <summary>
    /// Extracts downloaded Poiyomi Pro package to the VPM package directory.
    /// Supports both .zip and .unitypackage formats.
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
                    return false;
                }

                // Find this package's directory
                var packageDir = FindPackageDirectory();
                if (string.IsNullOrEmpty(packageDir))
                {
                    return await FallbackToAssetsImport(packagePath);
                }

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
                    return await FallbackToAssetsImport(packagePath);
                }
                
                // If extraction succeeded, delete the installer files
                if (success)
                {
                    DeleteInstallerFiles(packageDir);
                }
                
                return success;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Deletes the installer stub files after successful installation.
        /// </summary>
        private static void DeleteInstallerFiles(string packageDir)
        {
            var installerFiles = new[]
            {
                "Editor/PoiyomiProInstaller.cs",
                "Editor/PoiyomiProInstaller.cs.meta",
                "Editor/PoiyomiProDownloader.cs",
                "Editor/PoiyomiProDownloader.cs.meta",
                "Editor/PoiyomiProExtractor.cs",
                "Editor/PoiyomiProExtractor.cs.meta",
                "Editor/PoiyomiProAuth.cs",
                "Editor/PoiyomiProAuth.cs.meta",
                "Editor/PoiyomiProMenu.cs",
                "Editor/PoiyomiProMenu.cs.meta",
                "Editor/PoiyomiPro.Editor.asmdef",
                "Editor/PoiyomiPro.Editor.asmdef.meta",
            };
            
            foreach (var file in installerFiles)
            {
                var filePath = Path.Combine(packageDir, file);
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        Debug.Log($"[PoiyomiPro] Deleted installer file: {file}");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[PoiyomiPro] Could not delete {file}: {e.Message}");
                }
            }
            
            // Try to delete the Editor folder if it's empty
            var editorDir = Path.Combine(packageDir, "Editor");
            try
            {
                if (Directory.Exists(editorDir) && Directory.GetFiles(editorDir).Length == 0 && Directory.GetDirectories(editorDir).Length == 0)
                {
                    Directory.Delete(editorDir);
                    Debug.Log("[PoiyomiPro] Deleted empty Editor folder");
                    
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
        }

        /// <summary>
        /// Finds the directory where this VPM package is installed.
        /// </summary>
        private static string FindPackageDirectory()
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
        private static async Task<bool> ExtractUnityPackageToDirectory(string packagePath, string targetDir)
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
                    
                    return extractedFiles > 0;
                }
                finally
                {
                    // Clean up temp directory
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
            }
            catch
            {
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
                await Task.Run(() =>
                {
                    using (var archive = ZipFile.OpenRead(zipPath))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name))
                                continue;

                            var destinationPath = Path.Combine(targetDir, entry.FullName);
                            var destinationDir = Path.GetDirectoryName(destinationPath);

                            if (!Directory.Exists(destinationDir))
                                Directory.CreateDirectory(destinationDir);

                            entry.ExtractToFile(destinationPath, overwrite: true);
                        }
                    }
                });

                AssetDatabase.Refresh();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Fallback to Unity's built-in importer (extracts to Assets folder).
        /// </summary>
        private static async Task<bool> FallbackToAssetsImport(string packagePath)
        {
            try
            {
                AssetDatabase.ImportPackage(packagePath, false);
                await Task.Delay(500);
                AssetDatabase.Refresh();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
