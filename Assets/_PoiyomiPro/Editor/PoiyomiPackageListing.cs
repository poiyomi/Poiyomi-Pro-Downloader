using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace Poiyomi.Pro
{
    /// <summary>
    /// One version of a package on Poiyomi's VPM listing.
    /// </summary>
    public class PoiyomiPackageVersion
    {
        public string Id;
        public string Version;
        public string DisplayName;
        public string Description;
        public string Url;
        public string Sha256;
        public Dictionary<string, string> Dependencies = new Dictionary<string, string>();
        public List<string> LegacyPackages = new List<string>();

        public bool IsPrerelease => Version.Contains("-");
    }

    /// <summary>
    /// The same public listing VCC reads, so a package shows up in both once it's added there.
    /// </summary>
    public class PoiyomiPackageListing
    {
        public static string ListingUrl = "https://poiyomi.github.io/vpm/index.json";

        private readonly Dictionary<string, List<PoiyomiPackageVersion>> packages = new Dictionary<string, List<PoiyomiPackageVersion>>();

        public IEnumerable<string> PackageIds => packages.Keys;

        public static async Task<PoiyomiPackageListing> Fetch(CancellationToken cancel = default)
        {
            using (var request = UnityWebRequest.Get(ListingUrl))
            {
                request.timeout = 30;
                var operation = request.SendWebRequest();

                while (!operation.isDone)
                {
                    if (cancel.IsCancellationRequested)
                    {
                        request.Abort();
                        cancel.ThrowIfCancellationRequested();
                    }
                    await Task.Delay(50);
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    throw new Exception(request.error);
                }

                return Parse(request.downloadHandler.text);
            }
        }

        public static PoiyomiPackageListing Parse(string json)
        {
            var listing = new PoiyomiPackageListing();

            foreach (var package in (JObject)JObject.Parse(json)["packages"])
            {
                var versions = new List<PoiyomiPackageVersion>();

                foreach (var version in (JObject)package.Value["versions"])
                {
                    var entry = (JObject)version.Value;
                    versions.Add(new PoiyomiPackageVersion
                    {
                        Id = package.Key,
                        Version = version.Key,
                        DisplayName = (string)entry["displayName"] ?? package.Key,
                        Description = (string)entry["description"],
                        Url = (string)entry["url"],
                        Sha256 = (string)entry["zipSHA256"],
                        Dependencies = entry["vpmDependencies"]?.ToObject<Dictionary<string, string>>() ?? new Dictionary<string, string>(),
                        LegacyPackages = entry["legacyPackages"]?.ToObject<List<string>>() ?? new List<string>(),
                    });
                }

                versions.Sort((a, b) => PoiyomiSemVer.Compare(b.Version, a.Version));
                listing.packages[package.Key] = versions;
            }

            return listing;
        }

        /// <summary>Versions newest first, without prereleases.</summary>
        public List<PoiyomiPackageVersion> Stable(string id)
        {
            return packages.TryGetValue(id, out var versions)
                ? versions.Where(version => !version.IsPrerelease).ToList()
                : new List<PoiyomiPackageVersion>();
        }

        public PoiyomiPackageVersion Newest(string id)
        {
            return Stable(id).FirstOrDefault();
        }

        public PoiyomiPackageVersion Find(string id, string version)
        {
            return packages.TryGetValue(id, out var versions)
                ? versions.FirstOrDefault(entry => entry.Version == version)
                : null;
        }

        /// <summary>The newest stable version a VPM dependency range allows.</summary>
        public PoiyomiPackageVersion Resolve(string id, string range)
        {
            return Stable(id).FirstOrDefault(entry => PoiyomiSemVer.Satisfies(entry.Version, range));
        }
    }

    public static class PoiyomiSemVer
    {
        public static int Compare(string a, string b)
        {
            SplitVersion(a, out var numbersA, out var prereleaseA);
            SplitVersion(b, out var numbersB, out var prereleaseB);

            for (int i = 0; i < Math.Max(numbersA.Length, numbersB.Length); i++)
            {
                var partA = i < numbersA.Length ? numbersA[i] : 0;
                var partB = i < numbersB.Length ? numbersB[i] : 0;
                if (partA != partB)
                    return partA.CompareTo(partB);
            }

            // 1.0.0-beta sorts below 1.0.0
            if (prereleaseA == null || prereleaseB == null)
                return (prereleaseA == null ? 1 : 0) - (prereleaseB == null ? 1 : 0);

            return string.CompareOrdinal(prereleaseA, prereleaseB);
        }

        /// <summary>
        /// Every range Poiyomi's packages use is a lower bound such as ">=2.73.5".
        /// </summary>
        public static bool Satisfies(string version, string range)
        {
            var lowerBound = range.Trim().TrimStart('>', '=', '^', '~', ' ');
            return lowerBound.Length == 0 || !char.IsDigit(lowerBound[0]) || Compare(version, lowerBound) >= 0;
        }

        private static void SplitVersion(string version, out int[] numbers, out string prerelease)
        {
            var dash = version.IndexOf('-');
            prerelease = dash >= 0 ? version.Substring(dash + 1) : null;
            var core = dash >= 0 ? version.Substring(0, dash) : version;
            numbers = core.Split('.').Select(part => int.TryParse(part, out var value) ? value : 0).ToArray();
        }
    }
}
