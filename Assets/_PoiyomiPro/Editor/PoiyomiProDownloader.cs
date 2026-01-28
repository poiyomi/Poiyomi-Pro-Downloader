using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Poiyomi.Pro
{
    /// <summary>
    /// Downloads Poiyomi Pro package from authenticated URL.
    /// Supports both .zip and .unitypackage formats.
    /// </summary>
    public static class PoiyomiProDownloader
    {
        public static async Task<string> DownloadPackage(string url)
        {
            // Determine file extension from URL
            var extension = ".unitypackage";
            if (url.Contains(".zip"))
            {
                extension = ".zip";
            }
            
            var fileName = $"PoiyomiPro_{DateTime.Now.Ticks}{extension}";
            var downloadPath = Path.Combine(Application.temporaryCachePath, fileName);
            
            Debug.Log($"[Poiyomi Pro] Starting download to: {downloadPath}");
            
            try
            {
                using (var request = UnityWebRequest.Get(url))
                {
                    request.downloadHandler = new DownloadHandlerFile(downloadPath);
                    request.timeout = 300; // 5 minutes
                    
                    var operation = request.SendWebRequest();
                    
                    float lastLoggedProgress = 0;
                    while (!operation.isDone)
                    {
                        await Task.Delay(100);
                        
                        if (request.downloadProgress - lastLoggedProgress >= 0.1f)
                        {
                            lastLoggedProgress = request.downloadProgress;
                            Debug.Log($"[Poiyomi Pro] Download progress: {request.downloadProgress:P0}");
                        }
                    }
                    
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogError($"[Poiyomi Pro] Download failed: {request.error}");
                        throw new Exception($"Download failed: {request.error} (HTTP {request.responseCode})");
                    }
                    
                    Debug.Log($"[Poiyomi Pro] Download completed successfully");
                }
                
                // Verify the download
                if (!File.Exists(downloadPath))
                {
                    throw new Exception("Download failed - file not found");
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
                
                if (File.Exists(downloadPath))
                {
                    try { File.Delete(downloadPath); } catch { }
                }
                
                throw new Exception($"Failed to download package: {e.Message}", e);
            }
        }
    }
}
