using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Poiyomi.Pro
{
    public class PoiyomiPackageManagerWindow : EditorWindow
    {
        // These come with Pro or replace it, so they don't get a row of their own
        private static readonly string[] NotListed =
        {
            PoiyomiPackageInstaller.PRO_ID,
            PoiyomiPackageInstaller.THRY_ID,
            "com.poiyomi.toon",
        };

        private const string DOWNGRADE_NOTE = "Materials a newer version has already converted stay converted.";

        private PoiyomiPackageListing listing;
        private string listingError;
        private bool listingLoading = true;
        private bool installPending;
        private string patreonUrl = PoiyomiProConfig.PATREON_URL;
        private CancellationTokenSource cancel;
        private bool busy;
        private readonly Dictionary<string, VersionRow> versionRows = new Dictionary<string, VersionRow>();

        private Button refreshButton;
        private Label installedLabel;
        private VisualElement versionList;
        private Button actionButton;
        private Label noteLabel;
        private Label statusLabel;
        private ProgressBar progressBar;
        private Button cancelButton;
        private Button patreonButton;
        private VisualElement extras;

        [MenuItem("Poi/Package Manager", priority = 1)]
        public static PoiyomiPackageManagerWindow ShowWindow()
        {
            var window = GetWindow<PoiyomiPackageManagerWindow>("Poiyomi Packages");
            window.minSize = new Vector2(380, 260);
            window.Show();
            return window;
        }

        [MenuItem("Poi/Pro/Update to Latest")]
        public static void UpdateToLatest()
        {
            ShowWindow().BeginPrimaryInstall();
        }

        private void OnEnable()
        {
            PoiyomiProDownloader.OnDownloadProgress += HandleDownloadProgress;
        }

        private void OnDisable()
        {
            PoiyomiProDownloader.OnDownloadProgress -= HandleDownloadProgress;
        }

        private void OnDestroy()
        {
            cancel?.Cancel();
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            ApplyPoiTheme(root);
            root.style.paddingLeft = root.style.paddingRight = 12;
            root.style.paddingTop = root.style.paddingBottom = 12;

            var titleRow = new VisualElement();
            titleRow.style.flexDirection = FlexDirection.Row;
            titleRow.style.justifyContent = Justify.SpaceBetween;
            titleRow.style.alignItems = Align.Center;
            titleRow.style.marginBottom = 10;
            var title = new Label("Poiyomi Pro");
            title.AddToClassList("thry-title");
            title.style.fontSize = 18;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleRow.Add(title);
            var titleButtons = new VisualElement();
            titleButtons.style.flexDirection = FlexDirection.Row;
            refreshButton = new Button(() => _ = LoadListing()) { text = "Refresh" };
            titleButtons.Add(refreshButton);
            titleButtons.Add(new Button(() => Application.OpenURL(PoiyomiProConfig.CHANGELOG_URL)) { text = "Changelog" });
            titleRow.Add(titleButtons);
            root.Add(titleRow);

            if (PoiyomiProConfig.IsDryRun)
            {
                root.Add(new HelpBox($"Dry run: {PoiyomiProConfig.DEBUG_MODE}. Nothing gets downloaded or installed.", HelpBoxMessageType.Warning));
            }

            installedLabel = new Label();
            installedLabel.AddToClassList("thry-muted");
            installedLabel.style.marginBottom = 8;
            root.Add(installedLabel);

            actionButton = new Button(() => _ = InstallPro(PrimaryVersion()));
            actionButton.AddToClassList("thry-primary-action");
            root.Add(actionButton);

            noteLabel = new Label();
            noteLabel.AddToClassList("thry-muted");
            noteLabel.style.whiteSpace = WhiteSpace.Normal;
            noteLabel.style.marginTop = 6;
            root.Add(noteLabel);

            statusLabel = new Label();
            statusLabel.style.whiteSpace = WhiteSpace.Normal;
            statusLabel.style.marginTop = 8;
            root.Add(statusLabel);

            progressBar = new ProgressBar { lowValue = 0, highValue = 1 };
            progressBar.style.marginTop = 6;
            progressBar.style.display = DisplayStyle.None;
            root.Add(progressBar);

            cancelButton = new Button(() => cancel?.Cancel()) { text = "Cancel" };
            cancelButton.style.marginTop = 6;
            cancelButton.style.display = DisplayStyle.None;
            root.Add(cancelButton);

            patreonButton = new Button(() => Application.OpenURL(patreonUrl)) { text = "Open Patreon" };
            patreonButton.style.marginTop = 6;
            patreonButton.style.display = DisplayStyle.None;
            root.Add(patreonButton);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            scroll.style.marginTop = 10;
            root.Add(scroll);

            versionList = new VisualElement();
            scroll.Add(versionList);

            extras = new VisualElement();
            extras.style.marginTop = 16;
            scroll.Add(extras);

            var result = SessionState.GetString(PoiyomiPackageInstaller.RESULT_KEY, "");
            if (result.Length > 0)
            {
                statusLabel.text = result;
                SessionState.EraseString(PoiyomiPackageInstaller.RESULT_KEY);
            }

            UpdateProControls();
            _ = LoadListing();
        }

        /// <summary>
        /// Starts what the main button offers, once the window and listing are ready.
        /// </summary>
        public void BeginPrimaryInstall()
        {
            installPending = true;
            StartPendingInstall();
        }

        private void StartPendingInstall()
        {
            // CreateGUI can run after this window is shown, and the install wants the listing
            if (!installPending || actionButton == null || listingLoading)
                return;

            installPending = false;
            var version = PrimaryVersion();
            if (version != null)
            {
                _ = InstallPro(version);
            }
        }

        private static void ApplyPoiTheme(VisualElement root)
        {
            // ThryEditor comes with the shaders, so it's missing before the first install and
            // in older releases. The window keeps Unity's look in those cases.
            var style = Type.GetType("Thry.ThryEditor.RetainedWindow, ThryAssemblyDefinition")
                ?.GetMethod("Style", BindingFlags.Static | BindingFlags.NonPublic);
            style?.Invoke(null, new object[] { root });
        }

        private async Task LoadListing()
        {
            listingLoading = true;
            UpdateProControls();
            try
            {
                listing = await PoiyomiPackageListing.Fetch();
                listingError = null;
                PoiyomiPackageInstaller.PublishUpdateVersion(listing, PoiyomiPackageInstaller.InstalledVersion(PoiyomiPackageInstaller.PRO_ID));
            }
            catch (Exception e)
            {
                listingError = $"Couldn't load the version list: {e.Message}";
            }

            listingLoading = false;
            if (listing != null)
            {
                BuildVersionList();
            }
            UpdateAll();
            StartPendingInstall();
        }

        private void UpdateAll()
        {
            if (listing != null)
            {
                BuildExtras();
            }

            UpdateProControls();
        }

        private void BuildVersionList()
        {
            var wasOpen = versionList.Query<Foldout>().ToList().ToDictionary(foldout => foldout.text, foldout => foldout.value);
            versionList.Clear();
            versionRows.Clear();

            var urp = PoiyomiProConfig.IsUrpProject;
            var versions = listing.Stable(PoiyomiPackageInstaller.PRO_ID).Select(version => version.Version)
                .Where(version => !urp || PoiyomiSemVer.Compare(version, PoiyomiProConfig.MIN_URP_VERSION) >= 0)
                .ToList();
            var installed = PoiyomiPackageInstaller.InstalledVersion(PoiyomiPackageInstaller.PRO_ID);

            // The newest line starts open, as does whichever one is installed. A refresh keeps what's open.
            bool StartsOpen(string text, IEnumerable<string> group) => wasOpen.TryGetValue(text, out var open)
                ? open
                : group.Contains(versions.First()) || group.Contains(installed);

            foreach (var major in versions.GroupBy(version => version.Split('.')[0]))
            {
                var majorFoldout = new Foldout { text = $"Version {major.Key}", value = StartsOpen($"Version {major.Key}", major) };

                foreach (var minor in major.GroupBy(version => string.Join(".", version.Split('.').Take(2))))
                {
                    var minorFoldout = new Foldout { text = minor.Key, value = StartsOpen(minor.Key, minor) };

                    var index = 0;
                    foreach (var version in minor)
                    {
                        minorFoldout.Add(BuildVersionRow(version, index++ % 2 == 1));
                    }

                    majorFoldout.Add(minorFoldout);
                }

                versionList.Add(majorFoldout);
            }
        }

        private VisualElement BuildVersionRow(string version, bool alternate)
        {
            // Stripes and hover come from the theme's inspector rows
            var row = new VisualElement();
            row.AddToClassList("thry-property-row");
            row.EnableInClassList("thry-row-alt", alternate);
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingLeft = 4;

            var name = new Label(version);
            name.style.width = 64;
            row.Add(name);

            var tag = new Label();
            tag.AddToClassList("thry-muted");
            tag.style.flexGrow = 1;
            row.Add(tag);

            var button = new Button(() => _ = InstallPro(version));
            button.style.width = 84;
            row.Add(button);

            versionRows[version] = new VersionRow { Name = name, Tag = tag, Button = button };
            return row;
        }

        /// <summary>
        /// What the main button installs: the newest version, or what VCC asked for on a fresh stub.
        /// Null when there's nothing newer than what's installed.
        /// </summary>
        private string PrimaryVersion()
        {
            var installed = PoiyomiPackageInstaller.InstalledVersion(PoiyomiPackageInstaller.PRO_ID);
            var newest = listing?.Newest(PoiyomiPackageInstaller.PRO_ID)?.Version;

            if (installed == null)
            {
                return newest == null || listing.Find(PoiyomiPackageInstaller.PRO_ID, PoiyomiProConfig.TargetVersion) != null
                    ? PoiyomiProConfig.TargetVersion
                    : newest;
            }

            return newest != null && PoiyomiSemVer.Compare(newest, installed) > 0 ? newest : null;
        }

        private void UpdateProControls()
        {
            if (actionButton == null)
                return;

            var installed = PoiyomiPackageInstaller.InstalledVersion(PoiyomiPackageInstaller.PRO_ID);
            var newest = listing?.Newest(PoiyomiPackageInstaller.PRO_ID)?.Version;
            if (installed == null)
            {
                installedLabel.text = "Not installed yet.";
            }
            else if (newest != null && PoiyomiSemVer.Compare(newest, installed) > 0)
            {
                installedLabel.text = $"Version {installed} is installed. {newest} is available.";
            }
            else
            {
                installedLabel.text = $"Version {installed} is installed.";
            }

            var primary = PrimaryVersion();
            actionButton.style.display = primary == null ? DisplayStyle.None : DisplayStyle.Flex;
            actionButton.text = installed == null ? $"Install {primary}" : $"Update to {primary}";
            actionButton.SetEnabled(!busy && !listingLoading);
            refreshButton.SetEnabled(!busy && !listingLoading);

            noteLabel.text = listingError ?? "";
            noteLabel.style.display = listingError == null ? DisplayStyle.None : DisplayStyle.Flex;

            foreach (var pair in versionRows)
            {
                var comparison = installed == null ? 1 : PoiyomiSemVer.Compare(pair.Key, installed);
                var row = pair.Value;

                row.Name.style.unityFontStyleAndWeight = comparison == 0 ? FontStyle.Bold : FontStyle.Normal;
                row.Tag.text = comparison == 0 ? "Installed" : pair.Key == newest ? "Latest" : "";
                row.Button.text = installed == null ? "Install"
                    : comparison > 0 ? "Update"
                    : comparison < 0 ? "Downgrade"
                    : "Reinstall";
                row.Button.tooltip = comparison < 0 ? DOWNGRADE_NOTE : "";
                row.Button.SetEnabled(!busy && !listingLoading);
            }
        }

        private void BuildExtras()
        {
            extras.Clear();

            var ids = listing.PackageIds.Where(id => !NotListed.Contains(id) && listing.Newest(id) != null).ToList();
            if (ids.Count == 0)
                return;

            var heading = new Label("Other packages");
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            heading.style.marginBottom = 6;
            extras.Add(heading);

            foreach (var id in ids)
            {
                extras.Add(BuildExtraRow(id));
            }
        }

        private VisualElement BuildExtraRow(string id)
        {
            var newest = listing.Newest(id);
            var installed = PoiyomiPackageInstaller.InstalledVersion(id);

            var row = new VisualElement();
            row.style.marginBottom = 10;

            var name = new Label(installed == null ? newest.DisplayName : $"{newest.DisplayName} ({installed})");
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            row.Add(name);

            if (!string.IsNullOrEmpty(newest.Description))
            {
                var description = new Label(newest.Description);
                description.AddToClassList("thry-muted");
                description.style.whiteSpace = WhiteSpace.Normal;
                row.Add(description);
            }

            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;
            buttons.style.marginTop = 4;

            if (installed == null || PoiyomiSemVer.Compare(newest.Version, installed) > 0)
            {
                var label = installed == null ? $"Install {newest.Version}" : $"Update to {newest.Version}";
                var install = new Button(() => _ = Run(token => PoiyomiPackageInstaller.InstallPublicPackage(newest, listing, SetStatus, token))) { text = label };
                install.SetEnabled(!busy);
                buttons.Add(install);
            }

            if (installed != null)
            {
                var remove = new Button(() => PoiyomiPackageInstaller.RemovePackage(newest)) { text = "Remove" };
                remove.SetEnabled(!busy);
                buttons.Add(remove);
            }

            row.Add(buttons);
            return row;
        }

        private Task InstallPro(string version)
        {
            return Run(token => PoiyomiPackageInstaller.InstallPro(version, listing, SetStatus, token));
        }

        private async Task Run(Func<CancellationToken, Task> install)
        {
            if (busy || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                if (!busy)
                    SetStatus("Leave Play mode first.");
                return;
            }

            busy = true;
            cancel = new CancellationTokenSource();
            patreonButton.style.display = DisplayStyle.None;
            cancelButton.style.display = DisplayStyle.Flex;
            UpdateAll();

            try
            {
                await install(cancel.Token);

                var result = SessionState.GetString(PoiyomiPackageInstaller.RESULT_KEY, "");
                if (result.Length > 0)
                    SetStatus(result);
            }
            catch (OperationCanceledException)
            {
                SetStatus("Cancelled. Nothing was changed.");
            }
            catch (PoiyomiAuthException e)
            {
                SetStatus(e.Message);
                if (e.NeedsPatreon)
                {
                    patreonUrl = e.PatreonUrl;
                    patreonButton.style.display = DisplayStyle.Flex;
                }
            }
            catch (Exception e)
            {
                SetStatus($"Install failed: {e.Message}");
                Debug.LogError($"[PoiyomiPro] Install failed: {e}");
            }
            finally
            {
                busy = false;
                cancel = null;
                cancelButton.style.display = DisplayStyle.None;
                progressBar.style.display = DisplayStyle.None;
                UpdateAll();
            }
        }

        private void SetStatus(string text)
        {
            statusLabel.text = text;

            // Past this point the install can't be stopped; a failure rolls it back instead
            if (text == PoiyomiPackageInstaller.INSTALLING_STATUS)
            {
                cancelButton.style.display = DisplayStyle.None;
                progressBar.style.display = DisplayStyle.None;
            }
        }

        private class VersionRow
        {
            public Label Name;
            public Label Tag;
            public Button Button;
        }

        private void HandleDownloadProgress(float progress, long downloaded, long total)
        {
            if (progressBar == null)
                return;

            progressBar.style.display = DisplayStyle.Flex;
            progressBar.value = progress;
            progressBar.title = $"{downloaded / (1024f * 1024f):F1} MB / {total / (1024f * 1024f):F1} MB";
        }
    }

    /// <summary>
    /// A fresh stub from VCC starts its install when the project opens. Once Pro is installed,
    /// this only checks the listing for a newer version, once per editor session.
    /// </summary>
    [InitializeOnLoad]
    internal static class PoiyomiPackageStartup
    {
        private const string CHECKED_KEY = "Poiyomi.PackageManager.Checked";

        static PoiyomiPackageStartup()
        {
            if (Application.isBatchMode)
                return;

            EditorApplication.update += WaitForEditor;
        }

        // delayCall can stall while Unity is in the background, which it is when VCC opens a project
        private static void WaitForEditor()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            EditorApplication.update -= WaitForEditor;
            Run();
        }

        private static void Run()
        {
            if (!PoiyomiPackageInstaller.ProInstalled)
            {
                // A cancelled or failed login shouldn't reopen the browser on every recompile
                var marker = Path.Combine(PoiyomiPackageInstaller.PackageDir(PoiyomiPackageInstaller.PRO_ID), "Editor", ".download_started");
                if (File.Exists(marker) || !Directory.Exists(Path.GetDirectoryName(marker)))
                    return;

                if (!PoiyomiProConfig.IsDryRun)
                {
                    File.WriteAllText(marker, DateTime.Now.ToString("o"));
                }

                PoiyomiPackageManagerWindow.ShowWindow().BeginPrimaryInstall();
                return;
            }

            if (SessionState.GetBool(PoiyomiPackageInstaller.UPDATE_AFTER_MOVE_KEY, false))
            {
                SessionState.EraseBool(PoiyomiPackageInstaller.UPDATE_AFTER_MOVE_KEY);
                SessionState.SetBool(CHECKED_KEY, true);
                _ = FinishMove();
                return;
            }

            if (!SessionState.GetBool(CHECKED_KEY, false))
            {
                SessionState.SetBool(CHECKED_KEY, true);
                _ = CheckForUpdate();
            }
        }

        // Poiyomi just moved here from Assets and the click that moved it asked for an update
        private static async Task FinishMove()
        {
            PoiyomiPackageListing listing = null;
            try
            {
                listing = await PoiyomiPackageListing.Fetch();
            }
            catch (Exception)
            {
                // The window shows the error when it can't load the list either
            }

            PoiyomiPackageInstaller.AdoptInstalledVersion(PoiyomiPackageInstaller.ReadPackageVersion(PoiyomiPackageInstaller.PRO_ID), listing);
            PoiyomiPackageManagerWindow.ShowWindow().BeginPrimaryInstall();
        }

        private static async Task CheckForUpdate()
        {
            try
            {
                var listing = await PoiyomiPackageListing.Fetch();
                var newest = listing.Newest(PoiyomiPackageInstaller.PRO_ID)?.Version;
                var installed = PoiyomiPackageInstaller.InstalledVersion(PoiyomiPackageInstaller.PRO_ID);
                PoiyomiPackageInstaller.PublishUpdateVersion(listing, installed);
                if (newest == null || installed == null || PoiyomiSemVer.Compare(newest, installed) <= 0)
                    return;

                var notifiedKey = "Poiyomi.PackageManager.Notified." + Hash128.Compute(Application.dataPath);
                if (EditorPrefs.GetString(notifiedKey, "") == newest)
                    return;

                EditorPrefs.SetString(notifiedKey, newest);
                Debug.Log($"Poiyomi Pro {newest} is available. Update it from Poi > Package Manager.");
            }
            catch (Exception)
            {
                // Being offline is fine; the Package Manager window shows the error if it's opened
            }
        }
    }
}
