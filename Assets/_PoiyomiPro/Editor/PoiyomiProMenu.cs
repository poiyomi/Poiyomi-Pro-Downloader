using UnityEngine;
using UnityEditor;
using System.Threading.Tasks;

namespace Poiyomi.Pro
{
    public static class PoiyomiProMenu
    {
        [MenuItem("Poi/Pro/Download & Update")]
        public static void OpenDownloader()
        {
            PoiyomiProInstaller.ShowWindow();
        }
        
        
        [MenuItem("Poi/Pro/Clear Download Cache")]
        public static void ClearCache()
        {
            var result = EditorUtility.DisplayDialog(
                "Clear Download Cache",
                "This will clear temporary download files and authentication cache.",
                "Yes",
                "Cancel"
            );
            
            if (result)
            {
                PoiyomiProAuth.ClearAuth();
                
                // Clear any cached packages
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
            Application.OpenURL("https://poiyomi.com/docs");
        }
        
        [MenuItem("Poi/Pro/Report Issue")]
        public static void ReportIssue()
        {
            Application.OpenURL("https://github.com/poiyomi/PoiyomiToonShader/issues");
        }
        
        // Version management
        [MenuItem("Poi/Pro/Version History")]
        public static void ShowVersionHistory()
        {
            var window = EditorWindow.GetWindow<VersionHistoryWindow>("Version History");
            window.Show();
        }
    }
    
    public class VersionHistoryWindow : EditorWindow
    {
        private Vector2 scrollPosition;
        private string[] versions = new string[] { };
        private bool isLoading = true;
        
        void OnEnable()
        {
            LoadVersionHistory();
        }
        
        async void LoadVersionHistory()
        {
            // In a real implementation, fetch from API
            await Task.Delay(500); // Simulate loading
            
            versions = new string[]
            {
                "9.0.0 - Latest features and improvements",
                "8.2.1 - Bug fixes and performance improvements",
                "8.1.0 - New shader features",
                "8.0.0 - Major update with new UI"
            };
            
            isLoading = false;
            Repaint();
        }
        
        void OnGUI()
        {
            if (isLoading)
            {
                EditorGUILayout.HelpBox("Loading version history...", MessageType.Info);
                return;
            }
            
            EditorGUILayout.LabelField("Available Versions", EditorStyles.boldLabel);
            EditorGUILayout.Space(10);
            
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            
            foreach (var version in versions)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(version);
                
                if (GUILayout.Button("Install", GUILayout.Width(80)))
                {
                    // Trigger installation of specific version
                    Debug.Log($"Installing version: {version}");
                }
                
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.Space(5);
            }
            
            EditorGUILayout.EndScrollView();
        }
    }
}