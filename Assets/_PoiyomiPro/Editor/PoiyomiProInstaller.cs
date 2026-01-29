using UnityEngine;
using UnityEditor;
using System;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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
        private static HttpClient httpClient;
        private static bool autoStartTriggered = false;
        
        // Cache the resolved IPv4 URL to avoid repeated DNS lookups during polling
        private static string cachedIPv4CheckUrl = null;
        
        private const string API_BASE = "https://us-central1-poiyomi-pro-site.cloudfunctions.net";
        private const string WEB_BASE = "https://pro.poiyomi.com";
        
        // Version this installer targets - set at build time
        private const string TARGET_VERSION = "latest";
        
        static PoiyomiProInstaller()
        {
            // Configure networking to work around Unity Mono IPv6 issues
            ConfigureNetworking();
            
            EditorApplication.delayCall += CheckAndAutoStart;
        }
        
        /// <summary>
        /// Configure networking settings to work around common Unity/Mono DNS issues.
        /// Many users experience NameResolutionFailure due to IPv6 handling bugs in Mono.
        /// </summary>
        private static void ConfigureNetworking()
        {
            try
            {
                // Force DNS refresh to avoid stale cache issues
                ServicePointManager.DnsRefreshTimeout = 0;
                
                // Use TLS 1.2+ for security
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                
                // Increase connection limit for better performance
                ServicePointManager.DefaultConnectionLimit = 10;
                
                // Create HttpClient with custom handler
                var handler = new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                };
                
                httpClient = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(30)
                };
                
                Debug.Log("[Poiyomi Pro] Network configuration initialized");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Poiyomi Pro] Could not configure networking: {e.Message}");
                httpClient = new HttpClient();
            }
        }
        
        static void CheckAndAutoStart()
        {
            // Auto-start once per session if full version isn't installed
            if (!autoStartTriggered && !IsFullVersionInstalled())
            {
                autoStartTriggered = true;
                
                // Open window and auto-start authentication
                var window = GetWindow<PoiyomiProInstaller>("Poiyomi Pro");
                window.minSize = new Vector2(400, 300);
                window.Show();
                
                // Auto-start authentication after a brief delay
                EditorApplication.delayCall += () => {
                    if (!isAuthenticating && !isDownloading)
                    {
                        _ = window.StartAuthenticationAsync();
                    }
                };
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
        
        [MenuItem("Poi/Pro/Test Network Connection")]
        public static async void TestNetworkConnection()
        {
            Debug.Log("[Poiyomi Pro] Starting network diagnostics...");
            
            var host = "us-central1-poiyomi-pro-site.cloudfunctions.net";
            
            try
            {
                Debug.Log($"[Poiyomi Pro] Resolving {host}...");
                var addresses = await Dns.GetHostAddressesAsync(host);
                
                Debug.Log($"[Poiyomi Pro] Found {addresses.Length} addresses:");
                foreach (var addr in addresses)
                {
                    var type = addr.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
                    Debug.Log($"  - {addr} ({type})");
                }
                
                var ipv4 = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ipv4 != null)
                {
                    Debug.Log($"[Poiyomi Pro] ✓ IPv4 address available: {ipv4}");
                }
                else
                {
                    Debug.LogWarning("[Poiyomi Pro] ⚠ No IPv4 address found - this may cause issues!");
                }
                
                Debug.Log("[Poiyomi Pro] Testing HTTPS connection...");
                var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/");
                request.Headers.Add("User-Agent", "Unity/" + Application.unityVersion);
                
                var response = await httpClient.SendAsync(request);
                Debug.Log($"[Poiyomi Pro] ✓ HTTPS connection successful (status: {(int)response.StatusCode})");
                
                EditorUtility.DisplayDialog(
                    "Network Test Passed",
                    $"DNS Resolution: ✓\n" +
                    $"IPv4 Available: {(ipv4 != null ? "✓" : "✗")}\n" +
                    $"HTTPS Connection: ✓\n\n" +
                    "Your network should work with Poiyomi Pro.",
                    "OK"
                );
            }
            catch (Exception e)
            {
                Debug.LogError($"[Poiyomi Pro] Network test failed: {e.Message}");
                
                var message = "Network test failed!\n\n";
                
                if (e.Message.Contains("NameResolution") || e.Message.Contains("DNS"))
                {
                    message += "DNS RESOLUTION FAILED\n\n" +
                        "Try these fixes:\n" +
                        "1. Run 'ipconfig /flushdns' in Command Prompt\n" +
                        "2. Change DNS to 8.8.8.8 (Google) or 1.1.1.1 (Cloudflare)\n" +
                        "3. Disable IPv6 temporarily in Network Settings\n" +
                        "4. Check if your firewall is blocking Google Cloud";
                }
                else
                {
                    message += $"Error: {e.Message}";
                }
                
                EditorUtility.DisplayDialog("Network Test Failed", message, "OK");
            }
        }
        
        [MenuItem("Poi/Pro/Force IPv4 Mode")]
        public static void ForceIPv4Mode()
        {
            cachedIPv4CheckUrl = null;
            Debug.Log("[Poiyomi Pro] IPv4 mode enabled. DNS cache cleared.");
            EditorUtility.DisplayDialog(
                "IPv4 Mode",
                "IPv4 preference enabled.\n\n" +
                "The next authentication attempt will try to use IPv4 addresses first.",
                "OK"
            );
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
            var jsonBody = JsonConvert.SerializeObject(requestBody);
            
            // Try with normal resolution first, then fallback to IPv4-only if it fails
            Exception lastException = null;
            
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    string url = $"{API_BASE}/startUnityAuth";
                    
                    // On retry, try to resolve IPv4 explicitly
                    if (attempt > 0)
                    {
                        Debug.Log("[Poiyomi Pro] Retrying with IPv4 resolution...");
                        url = await ResolveToIPv4Url(url);
                    }
                    
                    var request = new HttpRequestMessage(HttpMethod.Post, url);
                    request.Headers.Add("User-Agent", "Unity/" + Application.unityVersion);
                    request.Headers.Host = "us-central1-poiyomi-pro-site.cloudfunctions.net";
                    request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                    
                    var response = await httpClient.SendAsync(request);
                    var content = await response.Content.ReadAsStringAsync();
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new Exception($"Failed to start authentication: {response.StatusCode} - {content}");
                    }
                    
                    var wrapper = JsonConvert.DeserializeObject<CallableResponse<StartAuthResponse>>(content);
                    return wrapper.result.sessionId;
                }
                catch (HttpRequestException e) when (e.InnerException is WebException webEx && 
                    webEx.Status == WebExceptionStatus.NameResolutionFailure)
                {
                    lastException = e;
                    Debug.LogWarning($"[Poiyomi Pro] DNS resolution failed (attempt {attempt + 1}): {e.Message}");
                    
                    if (attempt == 0)
                    {
                        statusMessage = "DNS issue detected, trying IPv4 fallback...";
                        Repaint();
                    }
                }
                catch (Exception)
                {
                    throw;
                }
            }
            
            throw new Exception(
                "DNS resolution failed. This is often caused by IPv6 issues.\n\n" +
                "Try these fixes:\n" +
                "1. Flush DNS: Run 'ipconfig /flushdns' in Command Prompt\n" +
                "2. Use Google DNS (8.8.8.8) or Cloudflare DNS (1.1.1.1)\n" +
                "3. Temporarily disable IPv6 in Network Adapter settings\n\n" +
                $"Technical details: {lastException?.Message}");
        }
        
        /// <summary>
        /// Resolves a URL to use an IPv4 address directly, working around Mono's IPv6 bugs.
        /// </summary>
        private async Task<string> ResolveToIPv4Url(string originalUrl)
        {
            try
            {
                var uri = new Uri(originalUrl);
                var host = uri.Host;
                
                var addresses = await Dns.GetHostAddressesAsync(host);
                var ipv4Address = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork);
                
                if (ipv4Address != null)
                {
                    Debug.Log($"[Poiyomi Pro] Resolved {host} to IPv4: {ipv4Address}");
                    
                    var builder = new UriBuilder(uri)
                    {
                        Host = ipv4Address.ToString()
                    };
                    
                    return builder.Uri.ToString();
                }
                else
                {
                    Debug.LogWarning($"[Poiyomi Pro] No IPv4 address found for {host}, using original URL");
                    return originalUrl;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Poiyomi Pro] IPv4 resolution failed: {e.Message}");
                return originalUrl;
            }
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
            var jsonBody = JsonConvert.SerializeObject(requestBody);
            string url = $"{API_BASE}/checkUnityAuth";
            
            // Use cached IPv4 URL if available (set after DNS failure recovery)
            if (!string.IsNullOrEmpty(cachedIPv4CheckUrl))
            {
                url = cachedIPv4CheckUrl;
            }
            
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        url = await ResolveToIPv4Url($"{API_BASE}/checkUnityAuth");
                        cachedIPv4CheckUrl = url;
                    }
                    
                    var request = new HttpRequestMessage(HttpMethod.Post, url);
                    request.Headers.Add("User-Agent", "Unity/" + Application.unityVersion);
                    request.Headers.Host = "us-central1-poiyomi-pro-site.cloudfunctions.net";
                    request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                    
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
                catch (HttpRequestException e) when (e.InnerException is WebException webEx && 
                    webEx.Status == WebExceptionStatus.NameResolutionFailure && attempt == 0)
                {
                    Debug.LogWarning($"[Poiyomi Pro] DNS resolution failed during poll, trying IPv4...");
                    continue;
                }
            }
            
            throw new Exception("DNS resolution failed during authentication check");
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
                
                // Just close - let Unity handle the import naturally without blocking popup
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
