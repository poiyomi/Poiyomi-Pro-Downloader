using UnityEngine;
using UnityEditor;
using System;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Poiyomi.Pro
{
    /// <summary>
    /// Dry-run switch for field testing. Deliberately not exposed in the editor - it has
    /// to be changed in source and recompiled, so a dry-run build cannot be produced by
    /// flipping a toggle, and build-package.ps1 refuses to package anything but Off.
    /// </summary>
    public enum DebugMode
    {
        /// <summary>Normal operation: authenticate, download, install.</summary>
        Off,

        /// <summary>
        /// No network at all. Pretends authentication succeeded and reports the version
        /// that would have been requested. Nothing is downloaded or installed.
        /// </summary>
        DryRunOffline,

        /// <summary>
        /// Runs the real authentication, then stops before downloading and reports the URL
        /// the server handed back. This is how you confirm the server honours the version
        /// this package pins. Nothing is downloaded or installed.
        /// </summary>
        DryRunLiveAuth,
    }
    
    /// <summary>
    /// Centralized configuration for Poiyomi Pro installer.
    /// </summary>
    public static class PoiyomiProConfig
    {
        // API endpoints
        public const string API_BASE = "https://us-central1-poiyomi-pro-site.cloudfunctions.net";
        public const string WEB_BASE = "https://pro.poiyomi.com";
        public const string API_HOST = "us-central1-poiyomi-pro-site.cloudfunctions.net";

        // External URLs
        public const string PATREON_URL = "https://www.patreon.com/poiyomi";
        public const string PATREON_JOIN_URL = "https://www.patreon.com/join/poiyomi/checkout?rid=3426248";
        public const string DISCORD_URL = "https://discord.gg/poiyomi";

        // Sentinel meaning "whatever the newest release is"
        public const string LATEST_VERSION = "latest";

        // Fallback version this installer targets - set at build time by build-package.ps1.
        // package.json takes priority over this, see TargetVersion.
        public const string TARGET_VERSION = "latest";

        // Networking settings
        public const int AUTH_POLL_INTERVAL_MS = 2000;
        public const int AUTH_MAX_ATTEMPTS = 150; // 5 minutes at 2s intervals
        public const int HTTP_TIMEOUT_SECONDS = 30;

        // Enable verbose logging (disable for release builds)
#if POIYOMI_DEBUG
        public const bool VERBOSE_LOGGING = true;
#else
        public const bool VERBOSE_LOGGING = false;
#endif

        // Dry-run switch for field testing. Set this to DebugMode.DryRunOffline or
        // DebugMode.DryRunLiveAuth, recompile, and the installer reports what it would
        // have done instead of downloading and installing.
        //
        // Kept as static readonly rather than const on purpose: a const false would make
        // every guarded branch unreachable and fill the console with CS0162 warnings.
        public static readonly DebugMode DEBUG_MODE = DebugMode.Off;

        /// <summary>True when this build reports instead of installing.</summary>
        public static bool IsDryRun
        {
            get { return DEBUG_MODE != DebugMode.Off; }
        }

        private static string _resolvedVersion;
        private static string _versionSource;

        /// <summary>
        /// The Pro version to download. Resolved from the installed package.json so that
        /// downgrading through VCC requests the pinned version instead of always pulling
        /// the newest release. Falls back to the build-stamped TARGET_VERSION when
        /// package.json is missing or still carries the unstamped dev placeholder.
        /// </summary>
        public static string TargetVersion
        {
            get
            {
                EnsureVersionResolved();
                return _resolvedVersion;
            }
        }

        /// <summary>
        /// True when we're targeting a specific version rather than whatever is newest.
        /// </summary>
        public static bool IsVersionPinned
        {
            get { return TargetVersion != LATEST_VERSION; }
        }

        /// <summary>
        /// Where TargetVersion was resolved from. For debug output only.
        /// </summary>
        public static string VersionSource
        {
            get
            {
                EnsureVersionResolved();
                return _versionSource;
            }
        }

        /// <summary>
        /// Resolves the target version once per domain reload, recording where it came from.
        /// </summary>
        private static void EnsureVersionResolved()
        {
            if (_resolvedVersion != null)
            {
                return;
            }

            var fromManifest = ReadInstalledPackageVersion();
            _resolvedVersion = fromManifest ?? TARGET_VERSION;
            _versionSource = fromManifest != null
                ? "package.json"
                : "build-stamped TARGET_VERSION fallback";
        }

        /// <summary>
        /// Reads the "version" field from this package's package.json.
        /// Returns null when it can't be read or isn't a real version number.
        /// </summary>
        private static string ReadInstalledPackageVersion()
        {
            try
            {
                var packageDir = PoiyomiProExtractor.FindPackageDirectory();
                if (string.IsNullOrEmpty(packageDir))
                {
                    return null;
                }

                var manifestPath = Path.Combine(packageDir, "package.json");
                if (!File.Exists(manifestPath))
                {
                    return null;
                }

                var version = (string)JObject.Parse(File.ReadAllText(manifestPath))["version"];

                // "0.0.0" is the placeholder the repo carries before a release is built
                if (string.IsNullOrEmpty(version) || version == "0.0.0" || !char.IsDigit(version[0]))
                {
                    return null;
                }

                return version;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PoiyomiPro] Could not read version from package.json: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// Poiyomi Pro Installer - Downloads and installs Pro shaders after Patreon authentication.
    ///
    /// Authentication is handled entirely by the website (pro.poiyomi.com).
    /// No credentials or tokens are stored locally.
    /// </summary>
    [InitializeOnLoad]
    public class PoiyomiProInstaller : EditorWindow
    {
        private static bool isAuthenticating = false;
        private static bool isDownloading = false;
        private static bool cancelRequested = false;
        private static string statusMessage = "";
        private static int authElapsedSeconds = 0;
        private static HttpClient httpClient;

        // Download progress tracking
        private static float downloadProgress = 0f;
        private static long downloadedBytes = 0;
        private static long totalBytes = 0;

        // IPv4 mode control
        private static bool forceIPv4 = false;
        private static string cachedIPv4CheckUrl = null;

        // Tracks if we've already checked this domain reload cycle
        private static bool hasCheckedThisCycle = false;

        // Tracks if installation has been initiated this session (survives within same domain)
        private static bool installationInitiated = false;

        // Cached GUI styles to avoid GC allocations
        private static GUIStyle _headerStyle;
        private static GUIStyle _centeredGreyMiniLabel;
        private static bool _stylesInitialized = false;

        // Marker file path to prevent download restart across domain reloads
        // Stored in the same Editor folder that gets deleted with the installer
        private static string _cachedScriptDirectory = null;

        private static string MarkerFilePath
        {
            get
            {
                var scriptPath = GetScriptDirectory();
                return scriptPath != null ? Path.Combine(scriptPath, ".download_started") : null;
            }
        }

        /// <summary>
        /// Gets the directory where this script is located.
        /// </summary>
        private static string GetScriptDirectory()
        {
            // Return cached path if available
            if (_cachedScriptDirectory != null)
                return _cachedScriptDirectory;

            // Try AssetDatabase first (works when fully loaded)
            var guids = UnityEditor.AssetDatabase.FindAssets("PoiyomiProInstaller t:MonoScript");
            if (guids.Length > 0)
            {
                var assetPath = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    var fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
                    _cachedScriptDirectory = Path.GetDirectoryName(fullPath);
                    return _cachedScriptDirectory;
                }
            }

            // Fallback: search common package locations directly
            var possiblePaths = new[]
            {
                Path.Combine(Application.dataPath, "..", "Packages", "com.poiyomi.pro", "Editor"),
                Path.Combine(Application.dataPath, "..", "Packages", "com.poiyomi.pro.installer", "Editor"),
                Path.Combine(Application.dataPath, "_PoiyomiPro", "Editor"),
            };

            foreach (var path in possiblePaths)
            {
                var fullPath = Path.GetFullPath(path);
                if (Directory.Exists(fullPath))
                {
                    _cachedScriptDirectory = fullPath;
                    return _cachedScriptDirectory;
                }
            }

            return null;
        }

        static PoiyomiProInstaller()
        {
            // Reset flag on domain reload and subscribe to update for reliable frame counting
            LogVerbose("Static constructor called - domain reload detected");
            hasCheckedThisCycle = false;
            delayFrames = 0;
            EditorApplication.update += OnEditorUpdate;
        }

        private static int delayFrames = 0;

        private static void OnEditorUpdate()
        {
            // Wait a few frames for AssetDatabase to be fully ready after domain reload
            if (delayFrames < 3)
            {
                delayFrames++;
                if (delayFrames == 1)
                    LogVerbose("OnEditorUpdate started, waiting 3 frames...");
                return;
            }

            // Unsubscribe immediately to prevent multiple calls
            EditorApplication.update -= OnEditorUpdate;

            // Check if we're being deleted - don't run any logic if script file is gone
            var scriptDir = GetScriptDirectory();
            if (scriptDir == null || !Directory.Exists(scriptDir))
            {
                LogVerbose("Script directory not found, installer is being deleted");
                return;
            }
            var scriptPath = Path.Combine(scriptDir, "PoiyomiProInstaller.cs");
            if (!File.Exists(scriptPath))
            {
                LogVerbose("Script file not found, installer is being deleted");
                return;
            }

            LogVerbose("Frame wait complete, checking for auto-start...");

            if (httpClient == null)
            {
                ConfigureNetworking();
            }

            TryAutoStart();
        }

        /// <summary>
        /// Called by AssetPostprocessor when assets are imported.
        /// This catches package imports that don't trigger script recompilation.
        /// </summary>
        public static void OnAssetsImported()
        {
            // Reset the flag so we check again after new assets are imported
            hasCheckedThisCycle = false;
            delayFrames = 0;
            // Unsubscribe first to prevent duplicate subscriptions
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;
        }

        private static void TryAutoStart()
        {
            LogVerbose($"TryAutoStart called, hasCheckedThisCycle={hasCheckedThisCycle}");

            // Prevent multiple checks in rapid succession
            if (hasCheckedThisCycle)
            {
                LogVerbose("Already checked this cycle, skipping");
                return;
            }
            hasCheckedThisCycle = true;

            // Check if installation already initiated this session (in-memory flag)
            if (installationInitiated)
            {
                LogVerbose("Installation already initiated this session, skipping auto-start");
                return;
            }

            // Check if download has already been started (persists across domain reloads via marker file)
            if (HasDownloadStarted())
            {
                LogVerbose("Download already started (marker file exists), skipping auto-start");
                return;
            }

            // If this installer script exists, it should run - no detection needed
            // The full version will replace these files, so if we're here, we need to install
            LogVerbose("Installer exists, opening window...");

            var window = GetWindow<PoiyomiProInstaller>("Poiyomi Pro");
            window.minSize = new Vector2(400, 300);
            window.Show();
            window.Focus();

            // Auto-start authentication after a brief delay
            EditorApplication.delayCall += () => {
                LogVerbose($"DelayCall for auth: isAuthenticating={isAuthenticating}, isDownloading={isDownloading}");
                if (!isAuthenticating && !isDownloading)
                {
                    // Set in-memory flag and create marker file to prevent restart.
                    // A dry run installs nothing, so it must stay repeatable across reloads.
                    installationInitiated = true;
                    if (!PoiyomiProConfig.IsDryRun)
                    {
                        CreateDownloadStartedMarker();
                    }
                    LogVerbose("Starting authentication...");
                    _ = window.StartAuthenticationAsync();
                }
            };
        }

        /// <summary>
        /// Checks if the download started marker file exists.
        /// </summary>
        private static bool HasDownloadStarted()
        {
            try
            {
                var path = MarkerFilePath;
                return path != null && File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Creates the marker file to indicate download has started.
        /// </summary>
        private static void CreateDownloadStartedMarker()
        {
            try
            {
                var path = MarkerFilePath;
                if (path == null)
                {
                    LogVerbose("Could not determine marker file path");
                    return;
                }
                File.WriteAllText(path, DateTime.Now.ToString("o"));
                LogVerbose($"Created download marker: {path}");
            }
            catch (Exception ex)
            {
                LogVerbose($"Failed to create marker file: {ex.Message}");
            }
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
                    Timeout = TimeSpan.FromSeconds(PoiyomiProConfig.HTTP_TIMEOUT_SECONDS)
                };
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PoiyomiPro] Failed to configure networking: {ex.Message}");
                httpClient = new HttpClient();
            }
        }

        [MenuItem("Poi/Pro/Download & Update")]
        public static void ShowWindow()
        {
            var window = GetWindow<PoiyomiProInstaller>("Poiyomi Pro");
            window.minSize = new Vector2(400, 300);
            window.Show();
        }

        [MenuItem("Poi/Pro/Test Network Connection")]
        public static void TestNetworkConnection()
        {
            // Run the async test without awaiting (fire-and-forget for menu item)
            _ = TestNetworkConnectionAsync();
        }

        /// <summary>
        /// Tests network connectivity to the API server.
        /// </summary>
        private static async Task TestNetworkConnectionAsync()
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(PoiyomiProConfig.API_HOST);
                var ipv4 = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork);

                var request = new HttpRequestMessage(HttpMethod.Get, $"https://{PoiyomiProConfig.API_HOST}/");
                request.Headers.Add("User-Agent", "Unity/" + Application.unityVersion);

                var response = await httpClient.SendAsync(request);

                EditorUtility.DisplayDialog(
                    "Network Test Passed",
                    $"DNS Resolution: OK\n" +
                    $"IPv4 Available: {(ipv4 != null ? "Yes" : "No")}\n" +
                    $"HTTPS Connection: OK\n\n" +
                    "Your network should work with Poiyomi Pro.",
                    "OK"
                );
            }
            catch (Exception e)
            {
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
            forceIPv4 = !forceIPv4;
            cachedIPv4CheckUrl = null; // Clear cache to force re-resolution

            EditorUtility.DisplayDialog(
                "IPv4 Mode",
                forceIPv4
                    ? "IPv4 mode ENABLED.\n\nAll API requests will now resolve to IPv4 addresses first."
                    : "IPv4 mode DISABLED.\n\nNormal DNS resolution will be used.",
                "OK"
            );
        }

        /// <summary>
        /// Initializes cached GUI styles. Called once when styles are first needed.
        /// Must be called from OnGUI when EditorStyles is guaranteed to be available.
        /// </summary>
        private static void InitializeStyles()
        {
            if (_stylesInitialized) return;

            // EditorStyles can be null if accessed too early in Unity's initialization
            if (EditorStyles.label == null) return;

            _headerStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 24,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };

            _centeredGreyMiniLabel = new GUIStyle(EditorStyles.centeredGreyMiniLabel);

            _stylesInitialized = true;
        }

        void OnEnable()
        {
            // Subscribe to download progress events
            PoiyomiProDownloader.OnDownloadProgress += HandleDownloadProgress;
        }

        void OnDisable()
        {
            // Unsubscribe from download progress events
            PoiyomiProDownloader.OnDownloadProgress -= HandleDownloadProgress;
        }

        private void HandleDownloadProgress(float progress, long downloaded, long total)
        {
            downloadProgress = progress;
            downloadedBytes = downloaded;
            totalBytes = total;
            Repaint();
        }

        void OnGUI()
        {
            // Initialize styles once
            InitializeStyles();

            EditorGUILayout.Space(10);

            // Header
            EditorGUILayout.LabelField("Poiyomi Pro", _headerStyle, GUILayout.Height(40));

            EditorGUILayout.Space(10);

            // A dry-run build must be impossible to mistake for a real one
            if (PoiyomiProConfig.IsDryRun)
            {
                EditorGUILayout.HelpBox(
                    $"DEBUG DRY RUN - {PoiyomiProConfig.DEBUG_MODE}\n" +
                    "Nothing will be downloaded or installed.\n" +
                    "Set DEBUG_MODE to DebugMode.Off in PoiyomiProInstaller.cs to restore normal behaviour.",
                    MessageType.Warning
                );
                EditorGUILayout.Space(10);
            }

            // Version info
            var versionText = PoiyomiProConfig.IsVersionPinned ? $"Version {PoiyomiProConfig.TargetVersion}" : "Latest Version";
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
                var buttonLabel = PoiyomiProConfig.IsDryRun ? "Run Debug Dry Run" : "Download Poiyomi Pro";
                if (GUILayout.Button(buttonLabel, GUILayout.Height(40)))
                {
                    _ = StartAuthenticationAsync();
                }

                EditorGUILayout.Space(10);

                if (GUILayout.Button("Get Patreon Subscription", GUILayout.Height(25)))
                {
                    Application.OpenURL(PoiyomiProConfig.PATREON_URL);
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

                // Animated progress indicator for auth (indeterminate)
                var progress = Mathf.PingPong(Time.realtimeSinceStartup * 0.5f, 1f);
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

                // Show real download progress
                string progressText;
                if (totalBytes > 0)
                {
                    var downloadedMB = downloadedBytes / (1024f * 1024f);
                    var totalMB = totalBytes / (1024f * 1024f);
                    progressText = $"{downloadedMB:F1} MB / {totalMB:F1} MB ({downloadProgress * 100:F0}%)";
                }
                else
                {
                    progressText = "Downloading...";
                }

                EditorGUI.ProgressBar(
                    EditorGUILayout.GetControlRect(GUILayout.Height(20)),
                    downloadProgress,
                    progressText
                );
            }

            GUI.enabled = true;

            // Footer
            EditorGUILayout.Space(20);
            EditorGUILayout.LabelField("Need help? Join our Discord", _centeredGreyMiniLabel);
            if (GUILayout.Button("Discord Support", GUILayout.Height(20)))
            {
                Application.OpenURL(PoiyomiProConfig.DISCORD_URL);
            }

            // Force repaint during animations
            if (isAuthenticating || isDownloading)
            {
                Repaint();
            }
        }

        public async Task StartAuthenticationAsync()
        {
            // Offline dry run never touches the network
            if (PoiyomiProConfig.DEBUG_MODE == DebugMode.DryRunOffline)
            {
                await RunOfflineDryRun();
                return;
            }

            try
            {
                isAuthenticating = true;
                cancelRequested = false;
                authElapsedSeconds = 0;
                statusMessage = "Starting authentication...";
                Repaint();

                // Create auth session on server
                var sessionId = await CreateAuthSession();

                // Open browser - website handles all authentication
                Application.OpenURL($"{PoiyomiProConfig.WEB_BASE}/unity-auth?sessionId={sessionId}&version={PoiyomiProConfig.TargetVersion}");
                statusMessage = "Please complete authentication in your browser";
                Repaint();

                // Poll for completion
                await PollForCompletion(sessionId);
            }
            catch (Exception e)
            {
                statusMessage = $"Error: {e.Message}";
            }
            finally
            {
                isAuthenticating = false;
                Repaint();
            }
        }

        private async Task<string> CreateAuthSession()
        {
            var requestBody = new { data = new { version = PoiyomiProConfig.TargetVersion } };
            var jsonBody = JsonConvert.SerializeObject(requestBody);

            // Try with normal resolution first, then fallback to IPv4-only if it fails
            Exception lastException = null;
            int maxAttempts = forceIPv4 ? 1 : 2;
            int startAttempt = forceIPv4 ? 1 : 0; // Skip normal resolution if forcing IPv4

            for (int attempt = startAttempt; attempt < maxAttempts + startAttempt; attempt++)
            {
                try
                {
                    string url = $"{PoiyomiProConfig.API_BASE}/startUnityAuth";

                    // On retry or when forcing IPv4, resolve explicitly
                    if (attempt > 0 || forceIPv4)
                    {
                        url = await ResolveToIPv4Url(url);
                    }

                    var request = new HttpRequestMessage(HttpMethod.Post, url);
                    request.Headers.Add("User-Agent", "Unity/" + Application.unityVersion);
                    request.Headers.Host = PoiyomiProConfig.API_HOST;
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
                "3. Temporarily disable IPv6 in Network Adapter settings\n" +
                "4. Use Poi > Pro > Force IPv4 Mode\n\n" +
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
                    var builder = new UriBuilder(uri)
                    {
                        Host = ipv4Address.ToString()
                    };

                    LogVerbose($"Resolved {host} to IPv4: {ipv4Address}");
                    return builder.Uri.ToString();
                }
                else
                {
                    LogVerbose($"No IPv4 address found for {host}");
                    return originalUrl;
                }
            }
            catch (Exception ex)
            {
                LogVerbose($"Failed to resolve IPv4: {ex.Message}");
                return originalUrl;
            }
        }

        private async Task PollForCompletion(string sessionId)
        {
            cancelRequested = false;
            authElapsedSeconds = 0;
            int pollInterval = PoiyomiProConfig.AUTH_POLL_INTERVAL_MS;
            bool hasSlowedDown = false;

            for (int i = 0; i < PoiyomiProConfig.AUTH_MAX_ATTEMPTS; i++)
            {
                if (cancelRequested)
                {
                    return;
                }

                await Task.Delay(pollInterval);
                authElapsedSeconds += pollInterval / 1000;
                Repaint();

                try
                {
                    var status = await CheckAuthStatus(sessionId);

                    // Slow down polling if server recommends it
                    if (status.shouldSlowDown && !hasSlowedDown)
                    {
                        pollInterval = 5000; // Increase to 5 seconds
                        hasSlowedDown = true;
                        statusMessage = "Still waiting for authentication... Check your browser";
                        LogVerbose($"Server requested slow down at poll #{status.pollCount}, increasing interval to 5s");
                    }

                    if (status.status == "completed")
                    {
                        isAuthenticating = false;
                        Repaint();

                        if (string.IsNullOrEmpty(status.downloadUrl))
                        {
                            throw new Exception("Server returned empty download URL");
                        }

                        // Live-auth dry run stops here, with a real URL in hand. Checked
                        // before announcing a download so the window never claims one is
                        // starting when nothing will actually be fetched.
                        if (PoiyomiProConfig.DEBUG_MODE == DebugMode.DryRunLiveAuth)
                        {
                            ReportLiveAuthDryRun(status.downloadUrl);
                            return;
                        }

                        statusMessage = "Authentication successful! Starting download...";
                        Repaint();

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

                    if (i == PoiyomiProConfig.AUTH_MAX_ATTEMPTS - 1)
                    {
                        throw new Exception($"Authentication timed out: {e.Message}");
                    }
                }
            }

            throw new Exception("Authentication timed out. Please try again.");
        }

        private async Task<AuthStatusResponse> CheckAuthStatus(string sessionId)
        {
            var requestBody = new { data = new { sessionId = sessionId } };
            var jsonBody = JsonConvert.SerializeObject(requestBody);
            string url = $"{PoiyomiProConfig.API_BASE}/checkUnityAuth";

            // Use cached IPv4 URL if available or if forcing IPv4
            if (!string.IsNullOrEmpty(cachedIPv4CheckUrl))
            {
                url = cachedIPv4CheckUrl;
            }
            else if (forceIPv4)
            {
                url = await ResolveToIPv4Url(url);
                cachedIPv4CheckUrl = url;
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        url = await ResolveToIPv4Url($"{PoiyomiProConfig.API_BASE}/checkUnityAuth");
                        cachedIPv4CheckUrl = url;
                    }

                    var request = new HttpRequestMessage(HttpMethod.Post, url);
                    request.Headers.Add("User-Agent", "Unity/" + Application.unityVersion);
                    request.Headers.Host = PoiyomiProConfig.API_HOST;
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
                catch (HttpRequestException) when (attempt == 0)
                {
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
                    if (result) Application.OpenURL(PoiyomiProConfig.PATREON_JOIN_URL);
                    statusMessage = "Error: Insufficient Patreon tier (requires $10+)";
                    break;

                case "not_a_patron":
                    var joinResult = EditorUtility.DisplayDialog(
                        "Patreon Subscription Required",
                        "Poiyomi Pro requires an active Patreon subscription.\n\nWould you like to become a patron?",
                        "Join on Patreon",
                        "Cancel"
                    );
                    if (joinResult) Application.OpenURL(PoiyomiProConfig.PATREON_URL);
                    statusMessage = "Error: Active Patreon subscription required";
                    break;

                default:
                    statusMessage = $"Error: {error}";
                    break;
            }
        }

        private async Task DownloadAndInstall(string downloadUrl)
        {
            // Belt and braces. The dry-run branches upstream should already have returned,
            // but this is the single funnel every download passes through, so enforcing it
            // here means no future call path can quietly start fetching in a debug build.
            if (PoiyomiProConfig.IsDryRun)
            {
                ReportDryRun(
                    $"Download blocked by {PoiyomiProConfig.DEBUG_MODE}.\n\n" +
                    $"Server returned: {downloadUrl}\n" +
                    "If Debug was off, the download would start immediately.");
                return;
            }
            
            try
            {
                isDownloading = true;
                downloadProgress = 0f;
                downloadedBytes = 0;
                totalBytes = 0;
                statusMessage = "Downloading package...";
                Repaint();

                // Download the package (with progress reporting)
                var packagePath = await PoiyomiProDownloader.DownloadPackage(downloadUrl);

                VerifyDownloadedVersion(packagePath);

                statusMessage = "Installing to package directory...";
                downloadProgress = 1f;
                Repaint();

                // Extract directly to this package's directory (don't delete installer yet)
                bool success = await PoiyomiProExtractor.ExtractToPackageDirectory(packagePath, deleteInstaller: false);

                if (!success)
                {
                    throw new Exception("Failed to extract package");
                }

                // Clean up downloaded file
                try
                {
                    File.Delete(packagePath);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[PoiyomiPro] Failed to delete temp package: {ex.Message}");
                }

                statusMessage = "Installation complete!";

                // Close the window FIRST, then delete installer files
                LogVerbose("Installation complete, closing window...");
                Close();

                // Delete installer files after window is closed
                EditorApplication.delayCall += () => {
                    LogVerbose("Deleting installer files...");
                    PoiyomiProExtractor.DeleteInstallerFiles();
                };
            }
            catch (Exception e)
            {
                statusMessage = $"Download failed: {e.Message}";
                Debug.LogError($"[PoiyomiPro] Download failed: {e.Message}\n{e.StackTrace}");
            }
            finally
            {
                isDownloading = false;
                Repaint();
            }
        }

        /// <summary>
        /// Debug dry run with no network: pretends authentication succeeded and reports
        /// the version that would have been requested.
        /// </summary>
        private async Task RunOfflineDryRun()
        {
            isAuthenticating = true;
            statusMessage = "Debug: simulating authentication...";
            Repaint();

            // Brief pause so the simulated step is actually visible in the window
            await Task.Delay(500);

            isAuthenticating = false;

            var version = PoiyomiProConfig.TargetVersion;
            var source = PoiyomiProConfig.VersionSource;

            if (PoiyomiProConfig.IsVersionPinned)
            {
                ReportDryRun(
                    "Success, found matching version package.\n\n" +
                    $"Would request version {version} (resolved from {source}).\n" +
                    "If Debug was off, the download would start immediately.");
            }
            else
            {
                ReportDryRun(
                    "No pinned version found, so the latest release would be requested.\n\n" +
                    $"Checked: {source}.\n" +
                    "If Debug was off, the download would start immediately.");
            }
        }

        /// <summary>
        /// Debug dry run after a real authentication round-trip: reports the download URL
        /// the server returned without fetching it. Comparing that URL against the pinned
        /// version is how you tell whether the server honours the version we ask for.
        /// </summary>
        private void ReportLiveAuthDryRun(string downloadUrl)
        {
            var version = PoiyomiProConfig.TargetVersion;

            if (!PoiyomiProConfig.IsVersionPinned)
            {
                ReportDryRun(
                    "No pinned version found, so the latest release would be downloaded.\n\n" +
                    $"Server returned: {downloadUrl}\n" +
                    "If Debug was off, the download would start immediately.");
                return;
            }

            // A signed URL may legitimately omit the version, so this is a hint, not proof
            if (downloadUrl.Contains(version))
            {
                ReportDryRun(
                    "Success, found matching version package.\n\n" +
                    $"Requested {version} (resolved from {PoiyomiProConfig.VersionSource}) " +
                    "and the returned URL mentions it.\n" +
                    $"Server returned: {downloadUrl}\n" +
                    "If Debug was off, the download would start immediately.");
            }
            else
            {
                ReportDryRun(
                    $"Requested {version}, but the returned URL does not mention that version.\n\n" +
                    "That is inconclusive on its own - a signed URL can omit the version - but if " +
                    "this URL points at a different release, the server is ignoring the version " +
                    "we asked for.\n" +
                    $"Server returned: {downloadUrl}\n" +
                    "If Debug was off, the download would start immediately and the version check " +
                    "would run against the downloaded file.");
            }
        }

        /// <summary>
        /// Surfaces a dry-run result in the window and the console.
        /// </summary>
        private void ReportDryRun(string message)
        {
            statusMessage = $"[Dry run] {message}";
            Debug.Log($"[PoiyomiPro] Dry run ({PoiyomiProConfig.DEBUG_MODE}):\n{message}");
            Repaint();
        }

       /// <summary>
        /// Aborts the install when the server hands back a different build than the one
        /// this package pins - extracting it would silently undo a deliberate downgrade.
        /// Packages we can't read a version from are allowed through.
        /// </summary>
        private static void VerifyDownloadedVersion(string packagePath)
        {
            if (!PoiyomiProConfig.IsVersionPinned)
            {
                return;
            }

            var expected = PoiyomiProConfig.TargetVersion;
            var actual = PoiyomiProExtractor.ReadPackageVersionFromArchive(packagePath);

            if (string.IsNullOrEmpty(actual))
            {
                LogVerbose($"Downloaded package has no readable version, skipping check (expected {expected})");
                return;
            }

            if (actual != expected)
            {
                try { File.Delete(packagePath); } catch { }

                throw new Exception(
                    $"Server returned Poiyomi Pro {actual}, but this package is pinned to {expected}. " +
                    "Nothing was installed. Change the version in VCC to install a different release.");
            }

            LogVerbose($"Verified downloaded package matches pinned version {actual}");
        }

        /// <summary>
        /// Logs a message only when verbose logging is enabled.
        /// </summary>
        private static void LogVerbose(string message)
        {
            if (PoiyomiProConfig.VERBOSE_LOGGING)
            {
                Debug.Log($"[PoiyomiPro] {message}");
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
            public int pollCount;
            public bool shouldSlowDown;
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

    /// <summary>
    /// Detects when assets are imported to trigger installer check.
    /// This catches package imports that don't cause script recompilation.
    /// </summary>
    public class PoiyomiProAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            // Check if any imported assets are in our package directory
            foreach (var asset in importedAssets)
            {
                if (asset.Contains("_PoiyomiPro") || asset.Contains("Poiyomi"))
                {
                    PoiyomiProInstaller.OnAssetsImported();
                    return;
                }
            }
        }
    }
}
