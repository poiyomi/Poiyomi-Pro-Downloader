using UnityEngine;
using UnityEditor;

namespace Poiyomi.Pro
{
    public static class PoiyomiProMenu
    {
        // Note: "Download & Update" menu item is defined in PoiyomiProInstaller.cs
        
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
                var cachePath = System.IO.Path.Combine(Application.temporaryCachePath, "PoiyomiPro");
                if (System.IO.Directory.Exists(cachePath))
                {
                    System.IO.Directory.Delete(cachePath, true);
                }
                
                EditorUtility.DisplayDialog(
                    "Cache Cleared",
                    "Download cache has been cleared.",
                    "OK"
                );
            }
        }
        
        
        [MenuItem("Poi/Pro/Support")]
        public static void OpenSupport()
        {
            Application.OpenURL("https://discord.gg/poiyomi");
        }
        
        [MenuItem("Poi/Pro/Documentation")]
        public static void OpenDocumentation()
        {
            Application.OpenURL("https://www.poiyomi.com/intro");
        }
        
        [MenuItem("Poi/Pro/Report Issue")]
        public static void ReportIssue()
        {
            Application.OpenURL("https://github.com/poiyomi/PoiyomiToonShader/issues");
        }
    }
}
