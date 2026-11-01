using UnityEngine;
using UnityEditor;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
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
        public const string CHANGELOG_URL = "https://www.poiyomi.com/changelog";

        // Sentinel meaning "whatever the newest release is"
        public const string LATEST_VERSION = "latest";

        // Fallback version this installer targets - set at build time by build-package.ps1.
        // package.json takes priority over this, see TargetVersion.
        public const string TARGET_VERSION = "latest";

        // Stamped with the matching manifest migration policy by build-package.ps1.
        // Default false preserves standalone Thry for builds of older Pro releases.
        // Only used when the version isn't on the listing; the listing entry decides otherwise.
        public static readonly bool BUNDLE_THRY_EDITOR = false;

        // Bump when this package's code changes. A newer build on the listing replaces the
        // installed Editor folder at the end of the next install.
        public const int MANAGER_BUILD = 1;

        // Networking settings
        public const int AUTH_POLL_INTERVAL_MS = 2000;
        public const int AUTH_MAX_ATTEMPTS = 150; // 5 minutes at 2s intervals
        public const int HTTP_TIMEOUT_SECONDS = 30;

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

        /// <summary>
        /// The version this package.json asks for. VCC writes it when a version is picked there,
        /// so a fresh stub installs what VCC asked for. Falls back to the build-stamped
        /// TARGET_VERSION when package.json is missing or still carries the dev placeholder.
        /// </summary>
        public static string TargetVersion
        {
            get
            {
                if (_resolvedVersion == null)
                {
                    var fromManifest = PoiyomiPackageInstaller.ReadPackageVersion(PoiyomiPackageInstaller.PRO_ID);

                    // "0.0.0" is the placeholder the repo carries before a release is built
                    var usable = !string.IsNullOrEmpty(fromManifest) && fromManifest != "0.0.0" && char.IsDigit(fromManifest[0]);
                    _resolvedVersion = usable ? fromManifest : TARGET_VERSION;
                }
                return _resolvedVersion;
            }
        }
    }

    /// <summary>
    /// A login that finished without download access. Code is the server's error, such as
    /// "insufficient_tier", or its message for anything else.
    /// </summary>
    public class PoiyomiAuthException : Exception
    {
        public bool NeedsPatreon { get; }
        public string PatreonUrl { get; }

        public PoiyomiAuthException(string code) : base(Describe(code))
        {
            NeedsPatreon = code == "insufficient_tier" || code == "not_a_patron"
                || (code != null && code.IndexOf("patreon", StringComparison.OrdinalIgnoreCase) >= 0);
            PatreonUrl = code == "insufficient_tier" ? PoiyomiProConfig.PATREON_JOIN_URL : PoiyomiProConfig.PATREON_URL;
        }

        private static string Describe(string code)
        {
            switch (code)
            {
                case "insufficient_tier": return "Poiyomi Pro needs a $10+ Patreon tier.";
                case "not_a_patron": return "Poiyomi Pro needs an active Patreon subscription.";
                default: return code;
            }
        }
    }

    /// <summary>
    /// Gets a signed download URL for a Pro version after Patreon login.
    ///
    /// Authentication is handled entirely by the website (pro.poiyomi.com).
    /// No credentials or tokens are stored locally.
    /// </summary>
    public static class PoiyomiProInstaller
    {
        private static HttpClient httpClient;

        // IPv4 mode control
        private static bool forceIPv4 = false;
        private static string cachedIPv4CheckUrl = null;

        private static HttpClient Http
        {
            get
            {
                if (httpClient == null)
                {
                    ConfigureNetworking();
                }
                return httpClient;
            }
        }

        /// <summary>
        /// Opens the login page and waits for it to finish. Returns the download URL for the version.
        /// </summary>
        public static async Task<string> RequestDownloadUrl(string version, Action<string> status, CancellationToken cancel)
        {
            var codeVerifier = CreateCodeVerifier();

            using (var loopback = new PoiyomiLoopbackListener())
            {
                var sessionId = await CreateAuthSession(version, CreateCodeChallenge(codeVerifier), loopback.Port, status);
                cancel.ThrowIfCancellationRequested();
                loopback.Listen(sessionId);

                // Open browser - website handles all authentication
                Application.OpenURL($"{PoiyomiProConfig.WEB_BASE}/unity-auth?sessionId={sessionId}&version={version}");
                status("Waiting for you to log in with your browser...");

                return await PollForCompletion(sessionId, codeVerifier, loopback, status, cancel);
            }
        }

        private static string CreateCodeVerifier()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }
            return Base64Url(bytes);
        }

        private static string CreateCodeChallenge(string codeVerifier)
        {
            using (var sha = SHA256.Create())
            {
                return Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(codeVerifier)));
            }
        }

        private static string Base64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
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

                var response = await Http.SendAsync(request);

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

        private static async Task<string> CreateAuthSession(string version, string codeChallenge, int redirectPort, Action<string> status)
        {
            var requestBody = new { data = new { version, codeChallenge, redirectPort } };
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

                    var response = await Http.SendAsync(request);
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
                        status("DNS issue detected, trying IPv4 fallback...");
                    }
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
        private static async Task<string> ResolveToIPv4Url(string originalUrl)
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

                    return builder.Uri.ToString();
                }

                return originalUrl;
            }
            catch (Exception)
            {
                return originalUrl;
            }
        }

        // The login code arrives through the local listener. Polling only reports failures, like a
        // Patreon tier that's too low, and download URLs from servers without loopback login.
        private static async Task<string> PollForCompletion(string sessionId, string codeVerifier, PoiyomiLoopbackListener loopback, Action<string> status, CancellationToken cancel)
        {
            int pollInterval = PoiyomiProConfig.AUTH_POLL_INTERVAL_MS;
            bool hasSlowedDown = false;

            for (int i = 0; i < PoiyomiProConfig.AUTH_MAX_ATTEMPTS; i++)
            {
                await Task.WhenAny(loopback.Code, Task.Delay(pollInterval, cancel));
                cancel.ThrowIfCancellationRequested();

                if (loopback.Code.IsCompleted)
                {
                    status("Login finished. Getting the download...");
                    return await ExchangeCode(sessionId, loopback.Code.Result, codeVerifier);
                }

                AuthStatusResponse result;
                try
                {
                    result = await CheckAuthStatus(sessionId);
                }
                catch (Exception e)
                {
                    if (i == PoiyomiProConfig.AUTH_MAX_ATTEMPTS - 1)
                    {
                        throw new Exception($"Authentication timed out: {e.Message}");
                    }
                    continue;
                }

                cancel.ThrowIfCancellationRequested();

                // Slow down polling if server recommends it
                if (result.shouldSlowDown && !hasSlowedDown)
                {
                    pollInterval = 5000; // Increase to 5 seconds
                    hasSlowedDown = true;
                    status("Still waiting for you to log in with your browser...");
                }

                if (result.status == "completed")
                {
                    if (string.IsNullOrEmpty(result.downloadUrl))
                    {
                        throw new Exception("Server returned empty download URL");
                    }
                    return result.downloadUrl;
                }

                if (result.status == "failed")
                {
                    throw new PoiyomiAuthException(result.error);
                }
            }

            throw new Exception("Authentication timed out. Please try again.");
        }

        private static async Task<string> ExchangeCode(string sessionId, string code, string codeVerifier)
        {
            var jsonBody = JsonConvert.SerializeObject(new { data = new { sessionId, code, codeVerifier } });
            string url = $"{PoiyomiProConfig.API_BASE}/exchangeUnityAuth";

            if (forceIPv4)
            {
                url = await ResolveToIPv4Url(url);
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        url = await ResolveToIPv4Url(url);
                    }

                    var request = new HttpRequestMessage(HttpMethod.Post, url);
                    request.Headers.Add("User-Agent", "Unity/" + Application.unityVersion);
                    request.Headers.Host = PoiyomiProConfig.API_HOST;
                    request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                    var response = await Http.SendAsync(request);
                    var content = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        var message = JsonConvert.DeserializeObject<CallableErrorResponse>(content)?.error?.message;
                        throw new PoiyomiAuthException(message ?? $"Login failed: {response.StatusCode}");
                    }

                    var downloadUrl = JsonConvert.DeserializeObject<CallableResponse<ExchangeResponse>>(content).result.downloadUrl;
                    if (string.IsNullOrEmpty(downloadUrl))
                    {
                        throw new Exception("Server returned empty download URL");
                    }
                    return downloadUrl;
                }
                catch (HttpRequestException) when (attempt == 0)
                {
                    continue;
                }
            }

            throw new Exception("DNS resolution failed while finishing the login");
        }

        private static async Task<AuthStatusResponse> CheckAuthStatus(string sessionId)
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

                    var response = await Http.SendAsync(request);
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
                        catch (JsonException)
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

        [Serializable]
        private class StartAuthResponse { public string sessionId; }

        [Serializable]
        private class ExchangeResponse { public string downloadUrl; }

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
}
