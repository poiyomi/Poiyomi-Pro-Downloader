using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Poiyomi.Pro
{
    /// <summary>
    /// Moves a staged package into an installed folder. Files that didn't change stay untouched so
    /// Unity only reimports what's different, and a failure puts everything back as it was.
    /// </summary>
    public static class PoiyomiFolderSync
    {
        /// <summary>
        /// Replaces the files in liveDir with the ones in stageDir and deletes the previous files
        /// the new version no longer has. Files that were never part of a release are left alone.
        /// stageDir, liveDir and backupDir must be on one drive. Returns the new file list.
        /// </summary>
        public static List<string> Sync(string stageDir, string liveDir, IEnumerable<string> previousFiles, string backupDir)
        {
            var incoming = ListFiles(stageDir, null);
            var incomingSet = new HashSet<string>(incoming, StringComparer.OrdinalIgnoreCase);
            var stale = previousFiles.Where(file => !incomingSet.Contains(file)).ToList();

            var backedUp = new List<string>();
            var added = new List<string>();

            try
            {
                foreach (var file in incoming)
                {
                    var source = Path.Combine(stageDir, file);
                    var target = Path.Combine(liveDir, file);

                    if (File.Exists(target))
                    {
                        if (SameContents(source, target))
                            continue;

                        MoveToBackup(target, Path.Combine(backupDir, file));
                        backedUp.Add(file);
                    }
                    else
                    {
                        added.Add(target);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Move(source, target);
                }

                foreach (var file in stale)
                {
                    var target = Path.Combine(liveDir, file);
                    if (File.Exists(target))
                    {
                        MoveToBackup(target, Path.Combine(backupDir, file));
                        backedUp.Add(file);
                    }
                }
            }
            catch
            {
                foreach (var target in added)
                {
                    if (File.Exists(target))
                        File.Delete(target);
                }
                foreach (var file in backedUp)
                {
                    var target = Path.Combine(liveDir, file);
                    if (File.Exists(target))
                        File.Delete(target);
                    File.Move(Path.Combine(backupDir, file), target);
                }
                throw;
            }

            foreach (var file in stale)
            {
                RemoveEmptyParents(Path.GetDirectoryName(Path.Combine(liveDir, file)), liveDir);
            }

            return incoming;
        }

        /// <summary>
        /// Files under rootDir as forward-slash paths relative to it. With subFolder, only that folder.
        /// </summary>
        public static List<string> ListFiles(string rootDir, string subFolder)
        {
            var searchRoot = subFolder == null ? rootDir : Path.Combine(rootDir, subFolder);
            if (!Directory.Exists(searchRoot))
                return new List<string>();

            var prefixLength = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar).Length + 1;
            return Directory.GetFiles(Path.GetFullPath(searchRoot), "*", SearchOption.AllDirectories)
                .Select(file => file.Substring(prefixLength).Replace('\\', '/'))
                .ToList();
        }

        /// <summary>
        /// Deletes a folder, including the read-only files the shader generator leaves behind.
        /// </summary>
        public static void DeleteFolder(string dir)
        {
            if (!Directory.Exists(dir))
                return;

            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(dir, true);
        }

        private static void MoveToBackup(string file, string backup)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backup));
            File.SetAttributes(file, FileAttributes.Normal);
            File.Move(file, backup);
        }

        private static bool SameContents(string a, string b)
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length)
                return false;

            const int bufferSize = 81920;
            var bufferA = new byte[bufferSize];
            var bufferB = new byte[bufferSize];

            using (var streamA = File.OpenRead(a))
            using (var streamB = File.OpenRead(b))
            {
                while (true)
                {
                    var readA = streamA.Read(bufferA, 0, bufferSize);
                    if (readA == 0)
                        return true;

                    var readB = 0;
                    while (readB < readA)
                    {
                        var read = streamB.Read(bufferB, readB, readA - readB);
                        if (read == 0)
                            return false;
                        readB += read;
                    }

                    for (int i = 0; i < readA; i++)
                    {
                        if (bufferA[i] != bufferB[i])
                            return false;
                    }
                }
            }
        }

        private static void RemoveEmptyParents(string dir, string liveDir)
        {
            var root = Path.GetFullPath(liveDir).TrimEnd(Path.DirectorySeparatorChar);

            while (dir != null && Path.GetFullPath(dir).Length > root.Length && Directory.Exists(dir)
                && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);

                var meta = dir + ".meta";
                if (File.Exists(meta))
                {
                    File.SetAttributes(meta, FileAttributes.Normal);
                    File.Delete(meta);
                }

                dir = Path.GetDirectoryName(dir);
            }
        }
    }
}
