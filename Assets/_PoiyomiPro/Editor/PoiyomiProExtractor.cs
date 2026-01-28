using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Poiyomi.Pro
{
    public static class PoiyomiProExtractor
    {
        /// <summary>
        /// Imports a Unity package using Unity's built-in importer
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
                Debug.Log($"[Poiyomi Pro] Importing package: {fileInfo.Length / 1024.0 / 1024.0:F2} MB");

                // Use Unity's built-in package importer
                // The 'false' parameter means import silently without showing the dialog
                AssetDatabase.ImportPackage(packagePath, false);
                
                Debug.Log("[Poiyomi Pro] Package import initiated - Unity is processing...");
                
                // Give Unity a moment to start the import
                await Task.Delay(500);
                
                // Refresh to ensure changes are picked up
                AssetDatabase.Refresh();

                Debug.Log("[Poiyomi Pro] Package import completed successfully!");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Poiyomi Pro] Import failed: {e.Message}");
                Debug.LogException(e);
                return false;
            }
        }
    }
}