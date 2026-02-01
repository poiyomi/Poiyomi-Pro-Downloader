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
            
            try
            {
                using (var request = UnityWebRequest.Get(url))
                {
                    request.downloadHandler = new DownloadHandlerFile(downloadPath);
                    request.timeout = 300; // 5 minutes
                    
                    var operation = request.SendWebRequest();
                    
                    while (!operation.isDone)
                    {
                        await Task.Delay(100);
                    }
                    
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        throw new Exception($"Download failed: {request.error} (HTTP {request.responseCode})");
                    }
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
                
                return downloadPath;
            }
            catch (Exception e)
            {
                if (File.Exists(downloadPath))
                {
                    try { File.Delete(downloadPath); } catch { }
                }
                
                throw new Exception($"Failed to download package: {e.Message}", e);
            }
        }
    }
}
