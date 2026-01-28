using UnityEngine;
using UnityEditor;
using System;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text;
using System.IO;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Poiyomi.Pro
{
    [InitializeOnLoad]
    public class PoiyomiProInstaller : EditorWindow
    {
        private static string currentSessionId;
        private static bool isAuthenticating = false;
        private static bool isDownloading = false;
        private static bool cancelRequested = false;
        private static string statusMessage = "";
        private static int authElapsedSeconds = 0;
        private static readonly HttpClient httpClient = new HttpClient();
        
        private const string API_BASE = "https://us-central1-poiyomi-pro-site.cloudfunctions.net";
        private const string WEB_BASE = "https://pro.poiyomi.com";
        private const string MARKER_FILE = "Assets/_PoiyomiPro/DO_NOT_DELETE.txt";
        
        // Version this installer targets - set at build time or default to "latest"
        private const string TARGET_VERSION = "latest";
        
        static PoiyomiProInstaller()
        {
            // Auto-run on import to help users get started
            EditorApplication.delayCall += CheckAndShowInstaller;
        }
        
        static void CheckAndShowInstaller()
        {
            // Only show if Poiyomi Pro shaders aren't already installed
            if (!IsFullVersionInstalled())
            {
                ShowWindow();
            }
        }
        
        static bool IsFullVersionInstalled()
        {
            // Look for specific files that only exist in the full version
            return AssetDatabase.FindAssets("PoiyomiToonShader", new[] { "Assets/Poiyomi" }).Length > 0;
        }
        
        [MenuItem("Poi/Pro/Download & Update")]
        public static void ShowWindow()
        {
            var window = GetWindow<PoiyomiProInstaller>("Poiyomi Pro Download");
            window.minSize = new Vector2(400, 300);
            window.Show();
        }
        
        void OnGUI()
        {
            EditorGUILayout.Space(10);
            
            // Logo/Header
            var headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 24,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            EditorGUILayout.LabelField("Poiyomi Pro", headerStyle, GUILayout.Height(40));
            
            EditorGUILayout.Space(10);
            
            // Info text
            EditorGUILayout.HelpBox(
                $"Download Poiyomi Pro {TARGET_VERSION}\n" +
                "Requires an active Patreon subscription ($10+ tier).",
                MessageType.Info
            );
            
            EditorGUILayout.Space(20);
            
            // Status message
            if (!string.IsNullOrEmpty(statusMessage))
            {
                var messageType = statusMessage.Contains("Error") ? MessageType.Error : MessageType.Info;
                EditorGUILayout.HelpBox(statusMessage, messageType);
                EditorGUILayout.Space(10);
            }
            
            // Authentication section
            GUI.enabled = !isAuthenticating && !isDownloading;
            
            if (!isAuthenticating && !isDownloading)
            {
                if (GUILayout.Button($"Download Poiyomi Pro {TARGET_VERSION}", GUILayout.Height(40)))
                {
                    _ = StartAuthentication();
                }
                
                EditorGUILayout.Space(10);
                
                if (GUILayout.Button("Get Patreon Subscription", GUILayout.Height(25)))
                {
                    Application.OpenURL("https://www.patreon.com/poiyomi");
                }
            }
            else if (isAuthenticating)
            {
                EditorGUILayout.LabelField("Authenticating... Check your browser", EditorStyles.boldLabel);
                EditorGUILayout.Space(5);
                
                // Show time elapsed
                var minutes = authElapsedSeconds / 60;
                var seconds = authElapsedSeconds % 60;
                EditorGUILayout.LabelField($"Time elapsed: {minutes}:{seconds:D2}", EditorStyles.miniLabel);
                EditorGUILayout.Space(5);
                
                // Show progress bar
                var progress = (float)((DateTime.Now.Second % 4) / 4.0f);
                EditorGUI.ProgressBar(
                    EditorGUILayout.GetControlRect(GUILayout.Height(20)),
                    progress,
                    "Waiting for authentication..."
                );
                
                EditorGUILayout.Space(10);
                
                // Cancel button
                GUI.enabled = true;
                if (GUILayout.Button("Cancel", GUILayout.Height(25)))
                {
                    cancelRequested = true;
                    statusMessage = "Authentication cancelled";
                    isAuthenticating = false;
                }
            }
            else if (isDownloading)
            {
                EditorGUILayout.LabelField("Downloading Poiyomi Pro...", EditorStyles.boldLabel);
                EditorGUI.ProgressBar(
                    EditorGUILayout.GetControlRect(GUILayout.Height(20)),
                    0.5f,
                    "Please wait..."
                );
            }
            
            GUI.enabled = true;
            
            // Footer
            EditorGUILayout.Space(20);
            EditorGUILayout.LabelField("Need help? Join our Discord", EditorStyles.centeredGreyMiniLabel);
            if (GUILayout.Button("Discord Support", GUILayout.Height(20)))
            {
                Application.OpenURL("https://discord.gg/poiyomi");
            }
        }
        
        private async Task StartAuthentication()
        {
            try
            {
                isAuthenticating = true;
                cancelRequested = false;
                authElapsedSeconds = 0;
                statusMessage = "Starting authentication...";
                Repaint();
                
                // Start auth session
                var sessionId = await CreateAuthSession();
                currentSessionId = sessionId;
                
                // Open browser with version parameter
                Application.OpenURL($"{WEB_BASE}/unity-auth?sessionId={sessionId}&version={TARGET_VERSION}");
                statusMessage = "Please complete authentication in your browser";
                Repaint();
                
                // Poll for completion
                await PollForCompletion(sessionId);
            }
            catch (Exception e)
            {
                statusMessage = $"Error: {e.Message}";
                Debug.LogError($"[Poiyomi Pro] Authentication error: {e}");
            }
            finally
            {
                isAuthenticating = false;
                Repaint();
            }
        }
        
        private async Task<string> CreateAuthSession()
        {
            // Firebase callable function format
            var requestBody = new
            {
                data = new
                {
                    version = TARGET_VERSION
                }
            };
            
            var request = new HttpRequestMessage(HttpMethod.Post, $"{API_BASE}/startUnityAuth");
            request.Headers.Add("User-Agent", "Unity/" + Application.unityVersion);
            request.Content = new StringContent(
                JsonConvert.SerializeObject(requestBody),
                Encoding.UTF8,
                "application/json"
            );
            
            var response = await httpClient.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Failed to start authentication: {response.StatusCode} - {content}");
            }
            
            // Firebase callable returns { result: { ... } }
            var wrapper = JsonConvert.DeserializeObject<CallableResponse<StartAuthResponse>>(content);
            return wrapper.result.sessionId;
        }
        
        private async Task PollForCompletion(string sessionId)
        {
            const int maxAttempts = 150; // 5 minutes
            const int delayMs = 2000; // 2 seconds
            
            cancelRequested = false;
            authElapsedSeconds = 0;
            
            for (int i = 0; i < maxAttempts; i++)
            {
                // Check for cancellation
                if (cancelRequested)
                {
                    Debug.Log("[Poiyomi Pro] Authentication cancelled by user");
                    return;
                }
                
                await Task.Delay(delayMs);
                authElapsedSeconds += delayMs / 1000;
                Repaint(); // Update UI with elapsed time
                
                try
                {
                    var status = await CheckAuthStatus(sessionId);
                    
                    if (status.status == "completed")
                    {
                        statusMessage = "Authentication successful! Starting download...";
                        isAuthenticating = false; // Stop showing auth UI
                        Repaint();
                        
                        Debug.Log($"[Poiyomi Pro] Download URL received: {status.downloadUrl?.Substring(0, Math.Min(100, status.downloadUrl?.Length ?? 0))}...");
                        
                        if (string.IsNullOrEmpty(status.downloadUrl))
                        {
                            throw new Exception("Server returned empty download URL");
                        }
                        
                        await DownloadAndInstall(status.downloadUrl);
                        return;
                    }
                    else if (status.status == "failed")
                    {
                        HandleAuthError(status.error);
                        return;
                    }
                }
                catch (Exception e)
                {
                    // Check if cancelled
                    if (cancelRequested)
                    {
                        Debug.Log("[Poiyomi Pro] Authentication cancelled by user");
                        return;
                    }
                    
                    // Continue polling on transient errors
                    if (i == maxAttempts - 1)
                    {
                        throw new Exception("Authentication timed out. Please try again.");
                    }
                    
                    Debug.LogWarning($"[Poiyomi Pro] Poll error (will retry): {e.Message}");
                }
            }
            
            throw new Exception("Authentication timed out. Please try again.");
        }
        
        private async Task<AuthStatusResponse> CheckAuthStatus(string sessionId)
        {
            // Firebase callable function format
            var requestBody = new
            {
                data = new
                {
                    sessionId = sessionId
                }
            };
            
            var request = new HttpRequestMessage(HttpMethod.Post, $"{API_BASE}/checkUnityAuth");
            request.Headers.Add("User-Agent", "Unity/" + Application.unityVersion);
            request.Content = new StringContent(
                JsonConvert.SerializeObject(requestBody),
                Encoding.UTF8,
                "application/json"
            );
            
            var response = await httpClient.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                // Parse error response
                try
                {
                    var errorResponse = JsonConvert.DeserializeObject<CallableErrorResponse>(content);
                    return new AuthStatusResponse 
                    { 
                        status = "failed", 
                        error = errorResponse?.error?.message ?? "Unknown error" 
                    };
                }
                catch
                {
                    throw new Exception($"Failed to check status: {response.StatusCode}");
                }
            }
            
            // Firebase callable returns { result: { ... } }
            var wrapper = JsonConvert.DeserializeObject<CallableResponse<AuthStatusResponse>>(content);
            return wrapper.result;
        }
        
        private void HandleAuthError(string error)
        {
            switch (error)
            {
                case "insufficient_tier":
                    var result = EditorUtility.DisplayDialog(
                        "Upgrade Required",
                        "Poiyomi Pro requires a $10+ Patreon tier.\n\n" +
                        "Would you like to upgrade your tier?",
                        "Upgrade on Patreon",
                        "Cancel"
                    );
                    
                    if (result)
                    {
                        Application.OpenURL("https://www.patreon.com/join/poiyomi/checkout?rid=3426248");
                    }
                    
                    statusMessage = "Error: Insufficient Patreon tier (requires $10+)";
                    break;
                    
                case "not_a_patron":
                    var joinResult = EditorUtility.DisplayDialog(
                        "Patreon Subscription Required",
                        "Poiyomi Pro requires an active Patreon subscription.\n\n" +
                        "Would you like to become a patron?",
                        "Join on Patreon",
                        "Cancel"
                    );
                    
                    if (joinResult)
                    {
                        Application.OpenURL("https://www.patreon.com/poiyomi");
                    }
                    
                    statusMessage = "Error: Active Patreon subscription required";
                    break;
                    
                default:
                    statusMessage = $"Error: {error}";
                    break;
            }
        }
        
        private async Task DownloadAndInstall(string downloadUrl)
        {
            try
            {
                isDownloading = true;
                statusMessage = "Downloading package... (this may take a few minutes)";
                Repaint();
                
                Debug.Log($"[Poiyomi Pro] Starting download from: {downloadUrl}");
                
                // Download the package
                var packagePath = await PoiyomiProDownloader.DownloadPackage(downloadUrl);
                Debug.Log($"[Poiyomi Pro] Download completed to: {packagePath}");
                
                statusMessage = "Installing package...";
                Repaint();
                
                // Extract to package directory instead of importing
                bool extractSuccess = await PoiyomiProExtractor.ExtractToPackageDirectory(packagePath);
                
                if (!extractSuccess)
                {
                    throw new Exception("Failed to extract package");
                }
                
                // Clean up downloaded file
                System.IO.File.Delete(packagePath);
                
                // Don't delete the installer - it now contains the shaders
                // Update the package.json to reflect it's now the full version
                UpdatePackageMetadata();
                
                // Just close the window - Unity's import will show the assets
                Debug.Log("[Poiyomi Pro] Installation complete!");
                Close();
            }
            catch (Exception e)
            {
                statusMessage = $"Download failed: {e.Message}";
                Debug.LogError($"[Poiyomi Pro] Download error: {e}");
            }
            finally
            {
                isDownloading = false;
                Repaint();
            }
        }
        
        [Serializable]
        private class StartAuthResponse
        {
            public string sessionId;
        }
        
        [Serializable]
        private class AuthStatusResponse
        {
            public string status;
            public string downloadUrl;
            public string error;
        }
        
        // Firebase callable response wrapper
        [Serializable]
        private class CallableResponse<T>
        {
            public T result;
        }
        
        [Serializable]
        private class CallableErrorResponse
        {
            public CallableError error;
        }
        
        [Serializable]
        private class CallableError
        {
            public string message;
            public string status;
        }
        
        private void UpdatePackageMetadata()
        {
            try
            {
                // Find package.json
                var packageJsonPath = Path.Combine(Application.dataPath, "..", "Packages", "com.poiyomi.pro.installer", "package.json");
                if (!File.Exists(packageJsonPath))
                {
                    // Try PackageCache
                    var packageCache = Path.Combine(Application.dataPath, "..", "Library", "PackageCache");
                    var dirs = Directory.GetDirectories(packageCache, "com.poiyomi.pro.installer@*");
                    if (dirs.Length > 0)
                    {
                        packageJsonPath = Path.Combine(dirs[0], "package.json");
                    }
                }
                
                if (File.Exists(packageJsonPath))
                {
                    var json = File.ReadAllText(packageJsonPath);
                    var packageData = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                    
                    // Update display name to show it's the full version
                    packageData["displayName"] = $"Poiyomi Pro {TARGET_VERSION}";
                    packageData["description"] = $"Poiyomi Pro {TARGET_VERSION} - Full shader package";
                    
                    // Write back
                    var updatedJson = JsonConvert.SerializeObject(packageData, Formatting.Indented);
                    File.WriteAllText(packageJsonPath, updatedJson);
                    
                    Debug.Log($"[Poiyomi Pro] Updated package metadata to reflect full version");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Poiyomi Pro] Could not update package metadata: {e.Message}");
            }
        }
    }
}