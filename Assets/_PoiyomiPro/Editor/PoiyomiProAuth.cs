using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json;
using System.Security.Cryptography;
using System.IO;

namespace Poiyomi.Pro
{
    public static class PoiyomiProAuth
    {
        private const string AUTH_TOKEN_KEY = "PoiyomiPro.AuthToken";
        private const string AUTH_EXPIRY_KEY = "PoiyomiPro.AuthExpiry";
        
        // Check if user has valid cached authentication
        public static bool HasValidAuth()
        {
            var token = EditorPrefs.GetString(AUTH_TOKEN_KEY, "");
            var expiryStr = EditorPrefs.GetString(AUTH_EXPIRY_KEY, "");
            
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(expiryStr))
            {
                return false;
            }
            
            if (DateTime.TryParse(expiryStr, out var expiry))
            {
                return DateTime.Now < expiry;
            }
            
            return false;
        }
        
        
        // Store authentication token
        public static void StoreAuth(string token, DateTime expiry)
        {
            // Use proper AES encryption with machine-specific key
            var encrypted = EncryptToken(token);
            
            EditorPrefs.SetString(AUTH_TOKEN_KEY, encrypted);
            EditorPrefs.SetString(AUTH_EXPIRY_KEY, expiry.ToString("O"));
        }
        
        // Get stored authentication token
        public static string GetStoredToken()
        {
            var encrypted = EditorPrefs.GetString(AUTH_TOKEN_KEY, "");
            if (string.IsNullOrEmpty(encrypted))
            {
                return null;
            }
            
            try
            {
                return DecryptToken(encrypted);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Poiyomi Pro] Failed to decrypt stored token: {e.Message}");
                ClearAuth();
                return null;
            }
        }
        
        // Clear stored authentication
        public static void ClearAuth()
        {
            EditorPrefs.DeleteKey(AUTH_TOKEN_KEY);
            EditorPrefs.DeleteKey(AUTH_EXPIRY_KEY);
        }
        
        // Proper AES encryption using machine-specific key
        private static string EncryptToken(string plainText)
        {
            using (var aes = Aes.Create())
            {
                // Derive key from multiple machine-specific values
                var keySource = $"{SystemInfo.deviceUniqueIdentifier}|{SystemInfo.deviceName}|{SystemInfo.operatingSystem}";
                var key = DeriveKey(keySource, 32); // 256-bit key
                var iv = DeriveKey(keySource + "|IV", 16); // 128-bit IV
                
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                
                using (var encryptor = aes.CreateEncryptor())
                using (var ms = new MemoryStream())
                using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                {
                    var plainBytes = Encoding.UTF8.GetBytes(plainText);
                    cs.Write(plainBytes, 0, plainBytes.Length);
                    cs.FlushFinalBlock();
                    
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
        }
        
        private static string DecryptToken(string cipherText)
        {
            using (var aes = Aes.Create())
            {
                // Derive key from multiple machine-specific values
                var keySource = $"{SystemInfo.deviceUniqueIdentifier}|{SystemInfo.deviceName}|{SystemInfo.operatingSystem}";
                var key = DeriveKey(keySource, 32); // 256-bit key
                var iv = DeriveKey(keySource + "|IV", 16); // 128-bit IV
                
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                
                var cipherBytes = Convert.FromBase64String(cipherText);
                
                using (var decryptor = aes.CreateDecryptor())
                using (var ms = new MemoryStream(cipherBytes))
                using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                using (var result = new MemoryStream())
                {
                    cs.CopyTo(result);
                    return Encoding.UTF8.GetString(result.ToArray());
                }
            }
        }
        
        // Derive a key from a string using PBKDF2
        private static byte[] DeriveKey(string source, int keySize)
        {
            using (var pbkdf2 = new Rfc2898DeriveBytes(
                source,
                Encoding.UTF8.GetBytes("PoiyomiPro2024"), // Salt
                10000, // Iterations
                HashAlgorithmName.SHA256))
            {
                return pbkdf2.GetBytes(keySize);
            }
        }
        
    }
    
    // Quick update checker
    public static class PoiyomiProUpdateChecker
    {
        private const string LAST_UPDATE_CHECK_KEY = "PoiyomiPro.LastUpdateCheck";
        private const string CURRENT_VERSION_KEY = "PoiyomiPro.CurrentVersion";
        private const string UPDATE_AVAILABLE_KEY = "PoiyomiPro.UpdateAvailable";
        
        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            EditorApplication.delayCall += CheckForUpdatesInBackground;
        }
        
        private static async void CheckForUpdatesInBackground()
        {
            // Only check once per day
            var lastCheckStr = EditorPrefs.GetString(LAST_UPDATE_CHECK_KEY, "");
            if (DateTime.TryParse(lastCheckStr, out var lastCheck))
            {
                if ((DateTime.Now - lastCheck).TotalDays < 1)
                {
                    return;
                }
            }
            
            // Skip update check for now - endpoint not implemented
            // TODO: Implement version check endpoint
            return;
            
            /*
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "Unity/" + Application.unityVersion);
                    
                    var response = await client.GetAsync("https://pro.poiyomi.com/api/unity/version/latest");
                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        var data = JsonConvert.DeserializeObject<VersionInfo>(content);
                        
                        var currentVersion = GetCurrentVersion();
                        if (!string.IsNullOrEmpty(currentVersion) && data.version != currentVersion)
                        {
                            EditorPrefs.SetBool(UPDATE_AVAILABLE_KEY, true);
                        }
                    }
                }
                
                EditorPrefs.SetString(LAST_UPDATE_CHECK_KEY, DateTime.Now.ToString("O"));
            }
            catch (Exception e)
            {
                Debug.Log($"[Poiyomi Pro] Update check failed: {e.Message}");
            }
            */
        }
        
        private static string GetCurrentVersion()
        {
            // Look for version file or parse from shader files
            var versionFiles = AssetDatabase.FindAssets("PoiyomiVersion t:TextAsset", new[] { "Assets/Poiyomi" });
            if (versionFiles.Length > 0)
            {
                var path = AssetDatabase.GUIDToAssetPath(versionFiles[0]);
                var text = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                return text?.text?.Trim();
            }
            
            return EditorPrefs.GetString(CURRENT_VERSION_KEY, "");
        }
        
        
        [Serializable]
        private class VersionInfo
        {
            public string version;
            public string releaseNotes;
            public string downloadUrl;
        }
    }
}