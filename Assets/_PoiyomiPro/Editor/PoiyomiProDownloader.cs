using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Poiyomi.Pro
{
    /// <summary>
    /// Handles downloading of Poiyomi packages.
    /// Uses UnityWebRequest for better performance in Unity's runtime.
    /// </summary>
    public static class PoiyomiProDownloader
    {
        // Progress reporting
        public static event Action<float, long, long> OnDownloadProgress;

        /// <summary>
        /// Downloads a package from the given URL and returns the local file path.
        /// Reports progress via OnDownloadProgress event when reportProgress is set.
        /// </summary>
        public static async Task<string> DownloadPackage(string url, CancellationToken cancel = default, bool reportProgress = true, int maxRetries = 3)
        {
            var cacheDir = Path.Combine(Application.temporaryCachePath, "PoiyomiPro");
            if (!Directory.Exists(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
            }

            // Determine file extension from URL using proper parsing
            var extension = GetFileExtensionFromUrl(url);

            var fileName = $"PoiyomiPro_{DateTime.Now.Ticks}{extension}";
            var filePath = Path.Combine(cacheDir, fileName);

            Exception lastException = null;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    // Wait before retry with exponential backoff
                    await Task.Delay(1000 * (int)Math.Pow(2, attempt), cancel);
                    Debug.Log($"[PoiyomiPro] Retry attempt {attempt + 1}/{maxRetries}");
                }

                try
                {
                    using (var request = UnityWebRequest.Get(url))
                    {
                        // Use DownloadHandlerFile for efficient direct-to-disk download
                        request.downloadHandler = new DownloadHandlerFile(filePath)
                        {
                            removeFileOnAbort = true
                        };

                        request.timeout = 1800;

                        var operation = request.SendWebRequest();

                        // Poll for progress
                        while (!operation.isDone)
                        {
                            if (cancel.IsCancellationRequested)
                            {
                                request.Abort();
                                cancel.ThrowIfCancellationRequested();
                            }

                            await Task.Delay(100);

                            var downloaded = (long)request.downloadedBytes;
                            // Calculate total from progress (progress is 0.0-1.0)
                            var total = request.downloadProgress > 0.001f
                                ? (long)(downloaded / request.downloadProgress)
                                : -1L;

                            if (reportProgress && request.downloadProgress > 0.001f)
                            {
                                OnDownloadProgress?.Invoke(request.downloadProgress, downloaded, total);
                            }
                        }

                        if (request.result != UnityWebRequest.Result.Success)
                        {
                            throw new Exception($"Download failed: {request.error} (HTTP {request.responseCode})");
                        }

                        if (reportProgress)
                        {
                            OnDownloadProgress?.Invoke(1f, (long)request.downloadedBytes, (long)request.downloadedBytes);
                        }

                        return filePath;
                    }
                }
                catch (OperationCanceledException)
                {
                    TryDelete(filePath);
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    Debug.LogWarning($"[PoiyomiPro] Download attempt {attempt + 1}/{maxRetries} failed: {ex.Message}");

                    // Clean up partial file
                    TryDelete(filePath);
                }
            }

            throw new Exception($"Download failed after {maxRetries} attempts: {lastException?.Message}", lastException);
        }

        /// <summary>
        /// Throws when the file doesn't match the SHA-256 from the VPM listing.
        /// </summary>
        public static void VerifySha256(string filePath, string expected)
        {
            if (string.IsNullOrEmpty(expected))
            {
                return;
            }

            string actual;
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            }

            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception($"{Path.GetFileName(filePath)} doesn't match its checksum on the listing. Nothing was installed.");
            }
        }

        private static void TryDelete(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (IOException)
            {
                // The cache folder is cleared with Poi > Pro > Clear Download Cache
            }
        }

        /// <summary>
        /// Extracts file extension from URL using proper parsing.
        /// </summary>
        private static string GetFileExtensionFromUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                var path = uri.AbsolutePath;

                // Remove query string if present in path
                var queryIndex = path.IndexOf('?');
                if (queryIndex >= 0)
                {
                    path = path.Substring(0, queryIndex);
                }

                var extension = Path.GetExtension(path);
                if (!string.IsNullOrEmpty(extension))
                {
                    return extension.ToLowerInvariant();
                }
            }
            catch
            {
                // Fall through to default
            }

            // Default to .unitypackage
            return ".unitypackage";
        }
    }
}
