using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Poiyomi.Pro
{
    /// <summary>
    /// Installs packages the way VCC does, into Packages/ with package.json and vpm-manifest.json
    /// kept in step, so VCC shows the right version and doesn't reinstall over it.
    ///
    /// com.poiyomi.pro holds this code in Editor/ and the shaders in _PoiyomiShaders/. Changing the
    /// Pro version only replaces the shaders, so going back to a release older than this
    /// package manager keeps it.
    /// </summary>
    public static class PoiyomiPackageInstaller
    {
        public const string PRO_ID = "com.poiyomi.pro";
        public const string THRY_ID = "com.poiyomi.thryeditor";
        public const string RESULT_KEY = "Poiyomi.PackageManager.Result";
        public const string INSTALLING_STATUS = "Installing...";

        // ThryEditor's inspector reads this to mark the version as old and offer an update
        public const string UPDATE_VERSION_KEY = "Poiyomi.PackageManager.UpdateVersion";

        private const string SHADERS_FOLDER = "_PoiyomiShaders";
        private const string FILE_LIST = ".poi-installed-files";

        private static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        private static string VpmManifestPath => Path.Combine(ProjectRoot, "Packages", "vpm-manifest.json");

        // Temp is on the same drive as Packages, so staged files move into place without copying
        private static string WorkDir => Path.Combine(ProjectRoot, "Temp", "PoiyomiInstall");

        public static string PackageDir(string id) => Path.Combine(ProjectRoot, "Packages", id);

        /// <summary>A fresh stub from VCC has a package.json and no shaders yet.</summary>
        public static bool ProInstalled => Directory.Exists(Path.Combine(PackageDir(PRO_ID), SHADERS_FOLDER));

        public static string InstalledVersion(string id)
        {
            return id == PRO_ID && !ProInstalled ? null : ReadPackageVersion(id);
        }

        public static string ReadPackageVersion(string id)
        {
            var path = Path.Combine(PackageDir(id), "package.json");
            if (!File.Exists(path))
                return null;

            try
            {
                return (string)JObject.Parse(File.ReadAllText(path))["version"];
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static void PublishUpdateVersion(PoiyomiPackageListing listing, string installed)
        {
            var newest = listing?.Newest(PRO_ID)?.Version;
            var outdated = newest != null && installed != null && PoiyomiSemVer.Compare(newest, installed) > 0;
            SessionState.SetString(UPDATE_VERSION_KEY, outdated ? newest : "");
        }

        /// <summary>
        /// Logs in, downloads the version and installs it. Unity recompiles when this finishes.
        /// </summary>
        public static async Task InstallPro(string version, PoiyomiPackageListing listing, Action<string> status, CancellationToken cancel)
        {
            if (PoiyomiProConfig.DEBUG_MODE == DebugMode.DryRunOffline)
            {
                status($"[Dry run] Would request Poiyomi Pro {version}.");
                return;
            }

            status("Starting login...");
            var url = await PoiyomiProInstaller.RequestDownloadUrl(version, status, cancel);

            if (PoiyomiProConfig.DEBUG_MODE == DebugMode.DryRunLiveAuth)
            {
                status($"[Dry run] Server returned {url}");
                return;
            }

            status($"Downloading Poiyomi Pro {version}...");
            var packagePath = await PoiyomiProDownloader.DownloadPackage(url, cancel);

            try
            {
                await ApplyProPackage(packagePath, version, listing, status, cancel);
            }
            finally
            {
                File.Delete(packagePath);
            }
        }

        /// <summary>
        /// Installs a Pro .unitypackage that's already on disk.
        /// </summary>
        public static async Task ApplyProPackage(string packagePath, string version, PoiyomiPackageListing listing, Action<string> status, CancellationToken cancel = default)
        {
            var entry = listing?.Find(PRO_ID, version);
            var stripThry = entry != null ? entry.Dependencies.ContainsKey(THRY_ID) : !PoiyomiProConfig.BUNDLE_THRY_EDITOR;
            var packageDir = PackageDir(PRO_ID);
            var work = WorkDir;
            var stage = Path.Combine(work, "pro");
            var backup = Path.Combine(work, "backup", "pro");

            BeginInstall(work);
            try
            {
                status("Unpacking...");
                await Task.Run(() => PoiyomiProExtractor.UnpackUnityPackage(packagePath, stage, stripThry));

                // Anything that needs the network happens before the installed files are touched
                var dependencies = await StageDependencies(entry, listing, status, cancel);
                var managerStage = await StageManagerUpdate(listing);
                cancel.ThrowIfCancellationRequested();

                status(INSTALLING_STATUS);
                var previous = ReadFileList(packageDir) ?? PoiyomiFolderSync.ListFiles(packageDir, SHADERS_FOLDER);
                var installed = await Task.Run(() => PoiyomiFolderSync.Sync(stage, packageDir, previous, backup));
                File.WriteAllLines(Path.Combine(packageDir, FILE_LIST), installed);

                WriteProManifest(packageDir, version, entry);
                UpdateVpmManifest(PRO_ID, version, entry?.Dependencies, direct: true);
                await ApplyDependencies(dependencies);
                RemoveLegacyPackages(entry);

                if (managerStage != null)
                {
                    var editorFiles = PoiyomiFolderSync.ListFiles(packageDir, "Editor");
                    await Task.Run(() => PoiyomiFolderSync.Sync(managerStage, packageDir, editorFiles, Path.Combine(work, "backup", "manager")));
                }

                PublishUpdateVersion(listing, version);
                SessionState.SetString(RESULT_KEY, $"Installed Poiyomi Pro {version}.");
            }
            finally
            {
                await EndInstall(work);
            }

            RefreshProject();
        }

        /// <summary>
        /// Installs a package from the public listing, along with the Poiyomi packages it depends on.
        /// </summary>
        public static async Task InstallPublicPackage(PoiyomiPackageVersion entry, PoiyomiPackageListing listing, Action<string> status, CancellationToken cancel)
        {
            var work = WorkDir;

            BeginInstall(work);
            try
            {
                status($"Downloading {entry.DisplayName} {entry.Version}...");
                var staged = await StagePublicPackage(entry, cancel);
                var dependencies = await StageDependencies(entry, listing, status, cancel);
                cancel.ThrowIfCancellationRequested();

                status(INSTALLING_STATUS);
                await ApplyPublicPackage(staged, direct: true);
                await ApplyDependencies(dependencies);
                RemoveLegacyPackages(entry);

                SessionState.SetString(RESULT_KEY, $"Installed {entry.DisplayName} {entry.Version}.");
            }
            finally
            {
                await EndInstall(work);
            }

            RefreshProject();
        }

        public static void RemovePackage(PoiyomiPackageVersion entry)
        {
            RemovePackageFiles(entry.Id);
            SessionState.SetString(RESULT_KEY, $"Removed {entry.DisplayName}.");
            RefreshProject();
        }

        private static void BeginInstall(string work)
        {
            PoiyomiFolderSync.DeleteFolder(work);
            Directory.CreateDirectory(work);

            EditorApplication.LockReloadAssemblies();
            AssetDatabase.DisallowAutoRefresh();
        }

        private static async Task EndInstall(string work)
        {
            AssetDatabase.AllowAutoRefresh();
            EditorApplication.UnlockReloadAssemblies();

            try
            {
                await Task.Run(() => PoiyomiFolderSync.DeleteFolder(work));
            }
            catch (IOException)
            {
                // Unity clears Temp when it closes
            }
        }

        private static void RefreshProject()
        {
            AssetDatabase.Refresh();
            Client.Resolve();
        }

        private static async Task<List<StagedPackage>> StageDependencies(PoiyomiPackageVersion entry, PoiyomiPackageListing listing, Action<string> status, CancellationToken cancel)
        {
            var staged = new List<StagedPackage>();
            if (entry == null || listing == null)
                return staged;

            foreach (var dependency in entry.Dependencies)
            {
                // VCC and the VRChat SDK look after everything that isn't ours
                if (!dependency.Key.StartsWith("com.poiyomi."))
                    continue;

                var installed = ReadPackageVersion(dependency.Key);
                if (installed != null && PoiyomiSemVer.Satisfies(installed, dependency.Value))
                    continue;

                var target = listing.Resolve(dependency.Key, dependency.Value);
                if (target == null)
                    throw new Exception($"{dependency.Key} {dependency.Value} isn't on the Poiyomi listing.");

                status($"Downloading {target.DisplayName} {target.Version}...");
                staged.Add(await StagePublicPackage(target, cancel));
            }

            return staged;
        }

        private static async Task<StagedPackage> StagePublicPackage(PoiyomiPackageVersion entry, CancellationToken cancel)
        {
            var zip = await PoiyomiProDownloader.DownloadPackage(entry.Url, cancel, reportProgress: false);
            var stage = Path.Combine(WorkDir, entry.Id);

            try
            {
                await Task.Run(() =>
                {
                    PoiyomiProDownloader.VerifySha256(zip, entry.Sha256);
                    PoiyomiProExtractor.UnpackZip(zip, stage);
                });
            }
            finally
            {
                File.Delete(zip);
            }

            return new StagedPackage { Entry = entry, StageDir = stage };
        }

        private static async Task ApplyDependencies(List<StagedPackage> dependencies)
        {
            foreach (var dependency in dependencies)
            {
                await ApplyPublicPackage(dependency, direct: false);
            }
        }

        private static async Task ApplyPublicPackage(StagedPackage package, bool direct)
        {
            var dir = PackageDir(package.Entry.Id);
            var previous = PoiyomiFolderSync.ListFiles(dir, null);
            var backup = Path.Combine(WorkDir, "backup", package.Entry.Id);

            await Task.Run(() => PoiyomiFolderSync.Sync(package.StageDir, dir, previous, backup));
            UpdateVpmManifest(package.Entry.Id, package.Entry.Version, package.Entry.Dependencies, direct);
        }

        /// <summary>
        /// Takes the Editor folder from the newest stub on the listing when it has newer code.
        /// A failure here never stops the install; the next one tries again.
        /// </summary>
        private static async Task<string> StageManagerUpdate(PoiyomiPackageListing listing)
        {
            var newest = listing?.Newest(PRO_ID);
            if (newest == null)
                return null;

            string zip = null;
            try
            {
                zip = await PoiyomiProDownloader.DownloadPackage(newest.Url, reportProgress: false, maxRetries: 1);
                PoiyomiProDownloader.VerifySha256(zip, newest.Sha256);

                if (PoiyomiProExtractor.ReadManagerBuild(zip) <= PoiyomiProConfig.MANAGER_BUILD)
                    return null;

                var stage = Path.Combine(WorkDir, "manager");
                PoiyomiProExtractor.UnpackZip(zip, stage, "Editor/");
                return stage;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PoiyomiPro] Couldn't update the package manager: {e.Message}");
                return null;
            }
            finally
            {
                if (zip != null)
                    File.Delete(zip);
            }
        }

        private static void RemoveLegacyPackages(PoiyomiPackageVersion entry)
        {
            if (entry == null)
                return;

            foreach (var legacy in entry.LegacyPackages)
            {
                // Removing Pro would take the package manager with it
                if (legacy != PRO_ID && Directory.Exists(PackageDir(legacy)))
                {
                    RemovePackageFiles(legacy);
                }
            }
        }

        private static void RemovePackageFiles(string id)
        {
            PoiyomiFolderSync.DeleteFolder(PackageDir(id));

            if (!File.Exists(VpmManifestPath))
                return;

            var manifest = JObject.Parse(File.ReadAllText(VpmManifestPath));
            (manifest["dependencies"] as JObject)?.Remove(id);
            (manifest["locked"] as JObject)?.Remove(id);
            WriteJson(VpmManifestPath, manifest);
        }

        private static void WriteProManifest(string packageDir, string version, PoiyomiPackageVersion entry)
        {
            var path = Path.Combine(packageDir, "package.json");
            var manifest = JObject.Parse(File.ReadAllText(path));

            manifest["version"] = version;
            if (entry != null)
            {
                manifest["vpmDependencies"] = JObject.FromObject(entry.Dependencies);
                manifest["legacyPackages"] = JArray.FromObject(entry.LegacyPackages);
            }

            WriteJson(path, manifest);
        }

        private static void UpdateVpmManifest(string id, string version, Dictionary<string, string> dependencies, bool direct)
        {
            if (!File.Exists(VpmManifestPath))
                return;

            var manifest = JObject.Parse(File.ReadAllText(VpmManifestPath));

            if (direct)
            {
                var requested = manifest["dependencies"] as JObject ?? new JObject();
                requested[id] = new JObject { ["version"] = version };
                manifest["dependencies"] = requested;
            }

            var locked = manifest["locked"] as JObject ?? new JObject();
            locked[id] = new JObject
            {
                ["version"] = version,
                ["dependencies"] = JObject.FromObject(dependencies ?? new Dictionary<string, string>()),
            };
            manifest["locked"] = locked;

            WriteJson(VpmManifestPath, manifest);
        }

        private static List<string> ReadFileList(string packageDir)
        {
            var path = Path.Combine(packageDir, FILE_LIST);
            return File.Exists(path) ? File.ReadAllLines(path).ToList() : null;
        }

        private static void WriteJson(string path, JObject json)
        {
            File.WriteAllText(path, json.ToString(Formatting.Indented), new UTF8Encoding(false));
        }

        private class StagedPackage
        {
            public PoiyomiPackageVersion Entry;
            public string StageDir;
        }
    }
}
