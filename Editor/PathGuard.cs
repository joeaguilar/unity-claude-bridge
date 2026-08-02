using System;
using System.IO;
using UnityEngine;

namespace Blue.ClaudeBridge
{
    /// <summary>
    /// Resolves caller-supplied paths and refuses anything outside the project.
    ///
    /// Anything able to write into .claude-bridge/in can make the editor execute a
    /// command. That is the accepted trust boundary for a local dev tool, but it
    /// must not widen into "write a file anywhere the editor user can write" --
    /// that is CWE-22, and it is the unpatched CVSS 8.0 defect in CoplayDev's
    /// unity-mcp that motivated building this instead.
    ///
    /// Every command taking a path argument must route through Resolve. Adding a
    /// path-taking command without it silently reopens the hole.
    /// </summary>
    public static class PathGuard
    {
        static readonly string Root = Directory.GetParent(Application.dataPath).FullName;

        public static string ProjectRoot { get { return Path.GetFullPath(Root); } }

        /// <summary>
        /// Resolve a caller-supplied path against the project root.
        /// </summary>
        /// <param name="requested">Caller value; may be null, relative or absolute.</param>
        /// <param name="defaultRelative">Used when requested is null or empty. Project-relative.</param>
        /// <returns>An absolute path guaranteed to sit inside the project.</returns>
        public static string Resolve(string requested, string defaultRelative)
        {
            string candidate = string.IsNullOrEmpty(requested) ? defaultRelative : requested;
            if (string.IsNullOrEmpty(candidate))
                throw new Exception("no path given and no default supplied");

            if (!Path.IsPathRooted(candidate))
                candidate = Path.Combine(ProjectRoot, candidate);

            string full;
            try
            {
                // Collapses ".." and "." segments, so traversal cannot survive the
                // prefix check below.
                full = Path.GetFullPath(candidate);
            }
            catch (Exception e)
            {
                throw new Exception("not a usable path: '" + candidate + "' (" + e.Message + ")");
            }

            string rootPrefix = ProjectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;

            string normalized = full.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            // Compare against root + separator so a sibling directory whose name
            // merely starts with the project name cannot pass.
            if (!normalized.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new Exception(
                    "refusing to touch a path outside the project: '" + full +
                    "' is not under '" + ProjectRoot + "'");

            RejectIfReachedViaLink(normalized, rootPrefix);

            return full;
        }

        /// <summary>
        /// Resolve, then create the containing directory.
        /// </summary>
        public static string ResolveForWrite(string requested, string defaultRelative)
        {
            string full = Resolve(requested, defaultRelative);

            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            return full;
        }

        /// <summary>
        /// Path.GetFullPath collapses ".." textually but does not resolve symlinks or
        /// junctions, so a link inside the project could still point out of it. Walk
        /// the ancestors between the target and the project root and reject any
        /// reparse point.
        ///
        /// Deliberately conservative: it refuses legitimate symlinked folders inside
        /// a project. If that ever becomes a real workflow, resolve link targets and
        /// re-check rather than dropping the test.
        /// </summary>
        static void RejectIfReachedViaLink(string full, string rootPrefix)
        {
            string dir = Path.GetDirectoryName(full);

            while (!string.IsNullOrEmpty(dir) && dir.Length >= rootPrefix.Length)
            {
                if (Directory.Exists(dir))
                {
                    try
                    {
                        if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                            throw new Exception(
                                "refusing to follow a link out of the project: '" + dir + "'");
                    }
                    catch (UnauthorizedAccessException) { /* unreadable; the prefix check still held */ }
                    catch (IOException) { }
                }

                string parent = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(parent) || parent == dir) break;
                dir = parent;
            }
        }
    }
}
