using UnityEngine;
using UnityEditor;
using System.IO;

namespace Poiyomi.Pro
{
    /// <summary>
    /// Editor menu items for Poiyomi Pro.
    /// Note: "Download & Update" menu item is defined in PoiyomiProInstaller.cs
    /// </summary>
    public static class PoiyomiProMenu
    {
        // URLs not in main config (documentation/issue specific)
        private const string DOCUMENTATION_URL = "https://www.poiyomi.com/intro";
        private const string ISSUES_URL = "https://github.com/poiyomi/PoiyomiToonShader/issues";

        [MenuItem("Poi/Pro/Clear Download Cache")]
        public static void ClearCache()
        {
            var result = EditorUtility.DisplayDialog(
                "Clear Download Cache",
                "This will clear temporary download files.\n\n" +
                "Note: Authentication is handled by the website - no local credentials are stored.",
                "Yes",
                "Cancel"
            );

            if (result)
            {
                // Clear any cached packages (no local auth to clear - handled by website)
                var cachePath = Path.Combine(Application.temporaryCachePath, "PoiyomiPro");
                if (Directory.Exists(cachePath))
                {
                    try
                    {
                        Directory.Delete(cachePath, true);
                        EditorUtility.DisplayDialog(
                            "Cache Cleared",
                            "Download cache has been cleared.",
                            "OK"
                        );
                    }
                    catch (System.Exception ex)
                    {
                        EditorUtility.DisplayDialog(
                            "Error",
                            $"Failed to clear cache: {ex.Message}",
                            "OK"
                        );
                    }
                }
                else
                {
                    EditorUtility.DisplayDialog(
                        "Cache Cleared",
                        "Download cache was already empty.",
                        "OK"
                    );
                }
            }
        }

        [MenuItem("Poi/Pro/Support")]
        public static void OpenSupport()
        {
            Application.OpenURL(PoiyomiProConfig.DISCORD_URL);
        }

        [MenuItem("Poi/Pro/Documentation")]
        public static void OpenDocumentation()
        {
            Application.OpenURL(DOCUMENTATION_URL);
        }

        [MenuItem("Poi/Pro/Report Issue")]
        public static void ReportIssue()
        {
            Application.OpenURL(ISSUES_URL);
        }
    }
}
