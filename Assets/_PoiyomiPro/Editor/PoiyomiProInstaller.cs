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
    /// <summary>
    /// Poiyomi Pro Installer - Downloads and installs Pro shaders after Patreon authentication.
    /// 
    /// Authentication is handled entirely by the website (pro.poiyomi.com).
    /// No credentials or tokens are stored locally.
    /// </summary>
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
        private static bool autoStartTriggered = false;
        
        private const string API_BASE = "https://us-central1-poiyomi-pro-site.cloudfunctions.net";
        private const string WEB_BASE = "https://pro.poiyomi.com";
        private const string MARKER_FILE = "Assets/_PoiyomiPro/DO_NOT_DELETE.txt";
        
        // Version this installer targets - set at build time
        private const string TARGET_VERSION = "latest";
        
        // Key to track if user has seen the installer for this version
        private const string INSTALL_PROMPTED_KEY = "PoiyomiPro.InstallPrompted";
        
        static PoiyomiProInstaller()
        {
            EditorApplication.delayCall += CheckAndAutoStart;
        }
        
        static void CheckAndAutoStart()
        {
            // Only auto-start once per session and if not already installed
            if (!autoStartTriggered && !IsFullVersionInstalled())
            {
                autoStartTriggered = true;
                
                // Check if user has already been prompted for this version
                var promptedVersion = EditorPrefs.GetString(INSTALL_PROMPTED_KEY, "");
                if (promptedVersion != TARGET_VERSION)
                {
                    // Auto-start the download flow
                    var window = GetWindow<PoiyomiProInstaller>("Poiyomi Pro");
                    window.minSize = new Vector2(400, 300);
                    window.Show();
                    
                    // Mark as prompted
                    EditorPrefs.SetString(INSTALL_PROMPTED_KEY, TARGET_VERSION);
                    
                    // Auto-start authentication after a brief delay
                    EditorApplication.delayCall += () => {
                        if (!isAuthenticating && !isDownloading)
                        {
                            _ = window.StartAuthenticationAsync();
                        }
                    };
                }
            }
        }
        
        static bool IsFullVersionInstalled()
        {
            // Check for shader files that indicate the full version is installed
            var shaderGuids = AssetDatabase.FindAssets("Poiyomi Pro t:Shader");
            return shaderGuids.Length > 5; // More than just a few placeholder shaders
        }
        
        [MenuItem("Poi/Pro/Download & Update")]
        public static void ShowWindow()
        {
            var window = GetWindow<PoiyomiProInstaller>("Poiyomi Pro");
            window.minSize = new Vector2(400, 300);
            window.Show();
        }
        
        void OnGUI()
        {
            EditorGUILayout.Space(10);
            
            // Header
            var headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 24,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            EditorGUILayout.LabelField("Poiyomi Pro", headerStyle, GUILayout.Height(40));
            
            EditorGUILayout.Space(10);
            
            // Version info
            var versionText = TARGET_VERSION == "latest" ? "Latest Version" : $"Version {TARGET_VERSION}";
            EditorGUILayout.HelpBox(
                $"Download {versionText}\n" +
                "Requires an active Patreon subscription ($10+ tier).\n\n" +
                "Authentication is handled securely via the website.",
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
            
            // Main UI
            GUI.enabled = !isAuthenticating && !isDownloading;
            
            if (!isAuthenticating && !isDownloading)
            {
                if (GUILayout.Button($"Download Poiyomi Pro", GUILayout.Height(40)))
                {
                    _ = StartAuthenticationAsync();
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
                
                var minutes = authElapsedSeconds / 60;
                var seconds = authElapsedSeconds % 60;
                EditorGUILayout.LabelField($"Time elapsed: {minutes}:{seconds:D2}", EditorStyles.miniLabel);
                EditorGUILayout.Space(5);
                
                var progress = (float)((DateTime.Now.Second % 4) / 4.0f);
                EditorGUI.ProgressBar(
                    EditorGUILayout.GetControlRect(GUILayout.Height(20)),
                    progress,
                    "Waiting for authentication..."
                );
                
                EditorGUILayout.Space(10);
                
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
        
        public async Task StartAuthenticationAsync()
        {
            try
            {
                isAuthenticating = true;
                cancelRequested = false;
                authElapsedSeconds = 0;
                statusMessage = "Starting authentication...";
                Repaint();
                
                // Create auth session on server
                var sessionId = await CreateAuthSession();
                currentSessionId = sessionId;
                
                // Open browser - website handles all authentication
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
            var requestBody = new { data = new { version = TARGET_VERSION } };
            
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
            
            var wrapper = JsonConvert.DeserializeObject<CallableResponse<StartAuthResponse>>(content);
            return wrapper.result.sessionId;
        }
        
        private async Task PollForCompletion(string sessionId)
        {
            const int maxAttempts = 150; // 5 minutes
            const int delayMs = 2000;
            
            cancelRequested = false;
            authElapsedSeconds = 0;
            
            for (int i = 0; i < maxAttempts; i++)
            {
                if (cancelRequested)
                {
                    Debug.Log("[Poiyomi Pro] Authentication cancelled by user");
                    return;
                }
                
                await Task.Delay(delayMs);
                authElapsedSeconds += delayMs / 1000;
                Repaint();
                
                try
                {
                    var status = await CheckAuthStatus(sessionId);
                    
                    if (status.status == "completed")
                    {
                        statusMessage = "Authentication successful! Starting download...";
                        isAuthenticating = false;
                        Repaint();
                        
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
                    if (cancelRequested) return;
                    
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
            var requestBody = new { data = new { sessionId = sessionId } };
            
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
                        "Poiyomi Pro requires a $10+ Patreon tier.\n\nWould you like to upgrade?",
                        "Upgrade on Patreon",
                        "Cancel"
                    );
                    if (result) Application.OpenURL("https://www.patreon.com/join/poiyomi/checkout?rid=3426248");
                    statusMessage = "Error: Insufficient Patreon tier (requires $10+)";
                    break;
                    
                case "not_a_patron":
                    var joinResult = EditorUtility.DisplayDialog(
                        "Patreon Subscription Required",
                        "Poiyomi Pro requires an active Patreon subscription.\n\nWould you like to become a patron?",
                        "Join on Patreon",
                        "Cancel"
                    );
                    if (joinResult) Application.OpenURL("https://www.patreon.com/poiyomi");
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
                
                Debug.Log($"[Poiyomi Pro] Starting download...");
                
                // Download the package
                var packagePath = await PoiyomiProDownloader.DownloadPackage(downloadUrl);
                Debug.Log($"[Poiyomi Pro] Download completed: {packagePath}");
                
                statusMessage = "Installing to package directory...";
                Repaint();
                
                // Extract directly to this package's directory
                bool success = await PoiyomiProExtractor.ExtractToPackageDirectory(packagePath);
                
                if (!success)
                {
                    throw new Exception("Failed to extract package");
                }
                
                // Clean up downloaded file
                File.Delete(packagePath);
                
                statusMessage = "Installation complete!";
                Debug.Log("[Poiyomi Pro] Installation complete!");
                
                // Refresh asset database
                AssetDatabase.Refresh();
                
                // Show success dialog and close
                EditorUtility.DisplayDialog(
                    "Installation Complete",
                    "Poiyomi Pro has been installed successfully!\n\nThe shaders are now available in your project.",
                    "OK"
                );
                
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
        private class StartAuthResponse { public string sessionId; }
        
        [Serializable]
        private class AuthStatusResponse
        {
            public string status;
            public string downloadUrl;
            public string error;
        }
        
        [Serializable]
        private class CallableResponse<T> { public T result; }
        
        [Serializable]
        private class CallableErrorResponse { public CallableError error; }
        
        [Serializable]
        private class CallableError
        {
            public string message;
            public string status;
        }
    }
}
