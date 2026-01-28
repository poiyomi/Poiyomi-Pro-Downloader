using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Poiyomi.Pro
{
    public static class PoiyomiProDownloader
    {
        public static async Task<string> DownloadPackage(string url)
        {
            var fileName = "PoiyomiPro_" + DateTime.Now.Ticks + ".unitypackage";
            var downloadPath = Path.Combine(Application.temporaryCachePath, fileName);
            
            Debug.Log($"[Poiyomi Pro] Starting download to: {downloadPath}");
            
            try
            {
                // Use UnityWebRequest which handles GitHub URLs better
                using (var request = UnityWebRequest.Get(url))
                {
                    // Set up download handler to write directly to file
                    request.downloadHandler = new DownloadHandlerFile(downloadPath);
                    
                    // Set timeout (5 minutes should be plenty)
                    request.timeout = 300;
                    
                    // Start the request
                    var operation = request.SendWebRequest();
                    
                    // Wait for completion with progress logging
                    float lastLoggedProgress = 0;
                    while (!operation.isDone)
                    {
                        await Task.Delay(100);
                        
                        // Log progress every 10%
                        if (request.downloadProgress - lastLoggedProgress >= 0.1f)
                        {
                            lastLoggedProgress = request.downloadProgress;
                            Debug.Log($"[Poiyomi Pro] Download progress: {request.downloadProgress:P0}");
                        }
                    }
                    
                    // Check for errors
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogError($"[Poiyomi Pro] Download failed: {request.error}");
                        Debug.LogError($"[Poiyomi Pro] Response code: {request.responseCode}");
                        throw new Exception($"Download failed: {request.error} (HTTP {request.responseCode})");
                    }
                    
                    Debug.Log($"[Poiyomi Pro] Download completed successfully");
                }
                
                // Verify the download
                if (!File.Exists(downloadPath))
                {
                    throw new Exception("Download failed - file not found after download");
                }
                
                var fileInfo = new FileInfo(downloadPath);
                if (fileInfo.Length == 0)
                {
                    throw new Exception("Download failed - file is empty");
                }
                
                Debug.Log($"[Poiyomi Pro] Package downloaded: {fileInfo.Length / 1024.0 / 1024.0:F2} MB");
                return downloadPath;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Poiyomi Pro] Download exception: {e}");
                
                // Clean up partial download
                if (File.Exists(downloadPath))
                {
                    try { File.Delete(downloadPath); } catch { }
                }
                
                throw new Exception($"Failed to download package: {e.Message}", e);
            }
        }
    }
}