using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using UnityEngine;

namespace Poiyomi.Pro
{
    /// <summary>
    /// Handles downloading of Poiyomi Pro packages from authenticated URLs.
    /// </summary>
    public static class PoiyomiProDownloader
    {
        /// <summary>
        /// Downloads a package from the given URL and returns the local file path.
        /// </summary>
        public static async Task<string> DownloadPackage(string url)
        {
            var cacheDir = Path.Combine(Application.temporaryCachePath, "PoiyomiPro");
            if (!Directory.Exists(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
            }
            
            // Determine file extension from URL or default to .unitypackage
            var extension = ".unitypackage";
            if (url.Contains(".zip"))
            {
                extension = ".zip";
            }
            
            var fileName = $"PoiyomiPro_{DateTime.Now.Ticks}{extension}";
            var filePath = Path.Combine(cacheDir, fileName);
            
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromMinutes(10);
                
                var response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();
                
                using (var fileStream = File.Create(filePath))
                {
                    await response.Content.CopyToAsync(fileStream);
                }
            }
            
            return filePath;
        }
    }
}
