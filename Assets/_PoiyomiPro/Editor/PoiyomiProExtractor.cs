using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Poiyomi.Pro
{
    /// <summary>
    /// Unpacks downloaded packages into a staging folder. Nothing here touches the installed
    /// files; PoiyomiFolderSync moves the staged files into place afterwards.
    /// </summary>
    public static class PoiyomiProExtractor
    {
        /// <summary>
        /// Sub-path (relative to the shaders root) of the ThryEditor copy inside the Pro archive.
        /// It's skipped for releases that get ThryEditor from its own VCC package.
        /// </summary>
        private const string ThrySubPath = "Scripts/ThryEditor";

        private static readonly Regex ManagerBuildPattern = new Regex(@"MANAGER_BUILD\s*=\s*(\d+)\s*;");

        /// <summary>
        /// Unpacks a .unitypackage into stageDir, laid out relative to the Assets folder.
        /// Unity packages are gzipped tar archives with a specific structure.
        /// </summary>
        public static void UnpackUnityPackage(string packagePath, string stageDir, bool stripThry)
        {
            var rawDir = stageDir + "-raw";
            Directory.CreateDirectory(stageDir);
            Directory.CreateDirectory(rawDir);

            ExtractTarGz(packagePath, rawDir);

            var unpacked = 0;
            foreach (var guidFolder in Directory.GetDirectories(rawDir))
            {
                var pathnamePath = Path.Combine(guidFolder, "pathname");
                if (!File.Exists(pathnamePath))
                    continue;

                // Some exporters add a second line after the path
                var originalPath = File.ReadAllText(pathnamePath).Split('\n')[0].Trim();

                if (!originalPath.StartsWith("Assets/"))
                    continue;

                var relativePath = originalPath.Substring("Assets/".Length);
                if (stripThry && IsThryPath(relativePath))
                    continue;

                var destPath = ResolveInside(stageDir, relativePath);
                var assetPath = Path.Combine(guidFolder, "asset");
                var metaPath = Path.Combine(guidFolder, "asset.meta");

                // Folders have a meta and no asset
                if (File.Exists(assetPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                    MoveReplacing(assetPath, destPath);
                    unpacked++;
                }
                else
                {
                    Directory.CreateDirectory(destPath);
                }

                if (File.Exists(metaPath))
                {
                    MoveReplacing(metaPath, destPath + ".meta");
                }
            }

            Directory.Delete(rawDir, true);

            if (unpacked == 0)
            {
                throw new Exception("The downloaded package is empty.");
            }
        }

        /// <summary>
        /// Unpacks a VPM zip into stageDir. With onlyPrefix, only entries under that path.
        /// </summary>
        public static void UnpackZip(string zipPath, string stageDir, string onlyPrefix = null)
        {
            Directory.CreateDirectory(stageDir);

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                        continue;

                    var relativePath = entry.FullName.Replace('\\', '/');
                    if (onlyPrefix != null && !relativePath.StartsWith(onlyPrefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var destPath = ResolveInside(stageDir, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                    entry.ExtractToFile(destPath, overwrite: true);
                }
            }
        }

        /// <summary>
        /// Reads MANAGER_BUILD from the installer source inside a stub zip. 0 for stubs from before it existed.
        /// </summary>
        public static int ReadManagerBuild(string stubZipPath)
        {
            using (var archive = ZipFile.OpenRead(stubZipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (!entry.Name.Equals("PoiyomiProInstaller.cs", StringComparison.OrdinalIgnoreCase))
                        continue;

                    using (var reader = new StreamReader(entry.Open()))
                    {
                        var match = ManagerBuildPattern.Match(reader.ReadToEnd());
                        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
                    }
                }
            }
            return 0;
        }

        /// <summary>
        /// Treats a ".meta" companion the same as its asset, whether or not the path
        /// starts with the shaders folder.
        /// </summary>
        private static bool IsThryPath(string relativePath)
        {
            var normalized = relativePath.Replace('\\', '/');
            if (normalized.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(0, normalized.Length - ".meta".Length);

            return normalized.Equals(ThrySubPath, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(ThrySubPath + "/", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/" + ThrySubPath, StringComparison.OrdinalIgnoreCase)
                || normalized.IndexOf("/" + ThrySubPath + "/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void MoveReplacing(string source, string destination)
        {
            if (File.Exists(destination))
                File.Delete(destination);
            File.Move(source, destination);
        }

        private static string ResolveInside(string rootDir, string relativePath)
        {
            var root = Path.GetFullPath(rootDir) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception($"The package has a file outside its own folder: {relativePath}");
            }
            return full;
        }

        private static void ExtractTarGz(string gzipPath, string outputDir)
        {
            using (var fileStream = File.OpenRead(gzipPath))
            using (var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress))
            {
                ExtractTar(gzipStream, outputDir);
            }
        }

        private static void ExtractTar(Stream stream, string outputDir)
        {
            var header = new byte[512];
            var fileBuffer = new byte[81920];

            while (ReadFully(stream, header, 512) == 512)
            {
                // Check for end of archive (two consecutive zero blocks)
                var allZero = true;
                for (int i = 0; i < 512; i++)
                {
                    if (header[i] != 0) { allZero = false; break; }
                }
                if (allZero)
                    break;

                // Parse filename (first 100 bytes, null-terminated)
                var name = Encoding.ASCII.GetString(header, 0, 100).TrimEnd('\0');
                if (string.IsNullOrEmpty(name))
                    break;

                // Parse file size (octal, bytes 124-135)
                var sizeStr = Encoding.ASCII.GetString(header, 124, 11).Trim('\0', ' ');
                var size = string.IsNullOrEmpty(sizeStr) ? 0L : Convert.ToInt64(sizeStr, 8);

                // Type flag (byte 156)
                var typeFlag = (char)header[156];
                var isFile = (typeFlag == '0' || typeFlag == '\0') && !name.EndsWith("/");
                var padding = (512 - size % 512) % 512;

                if (isFile)
                {
                    var outputPath = ResolveInside(outputDir, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

                    using (var output = File.Create(outputPath))
                    {
                        CopyBytes(stream, output, size, fileBuffer);
                    }
                }
                else
                {
                    CopyBytes(stream, Stream.Null, size, fileBuffer);
                }

                CopyBytes(stream, Stream.Null, padding, fileBuffer);
            }
        }

        private static int ReadFully(Stream stream, byte[] buffer, int count)
        {
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, total, count - total);
                if (read == 0)
                    break;
                total += read;
            }
            return total;
        }

        private static void CopyBytes(Stream source, Stream destination, long count, byte[] buffer)
        {
            while (count > 0)
            {
                var read = source.Read(buffer, 0, (int)Math.Min(count, buffer.Length));
                if (read == 0)
                    throw new EndOfStreamException("The downloaded package is incomplete.");
                destination.Write(buffer, 0, read);
                count -= read;
            }
        }
    }
}
