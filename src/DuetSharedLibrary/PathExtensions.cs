using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DuetSharedLibrary;

/// <summary>
/// Extension members for <see cref="Path"/>
/// </summary>
public static class PathExtensions
{
    extension(Path)
    {
        /// <summary>
        /// Resolve a path to its canonical form, following all symlinks and resolving
        /// ".." components through the actual filesystem (not lexically).
        /// This is equivalent to the POSIX realpath() function.
        /// </summary>
        /// <param name="path">Path to resolve</param>
        /// <returns>Canonical path, or null if the path does not exist</returns>
        public static string? GetRealPath(string path)
        {
            IntPtr result = Interop.realpath(path, IntPtr.Zero);
            if (result == IntPtr.Zero)
            {
                return null;
            }
            string resolved = Marshal.PtrToStringUTF8(result)!;
            Marshal.FreeHGlobal(result);
            return resolved;
        }

        /// <summary>
        /// Check if a path stays inside a directory, e.g. when it is made of a base directory and a file name from a plugin manifest or package,
        /// which may contain '..' segments or be absolute. Symlinks in the existing part of its parent directories are resolved, so a symlinked
        /// directory below the directory cannot lead elsewhere. The entry itself is not followed: whoever writes it must replace it instead
        /// </summary>
        /// <param name="path">Path to check</param>
        /// <param name="directory">Directory the path must not leave</param>
        /// <returns>Whether the path is the directory or below it</returns>
        /// <remarks>
        /// The file system may change after the check, so this does not protect against a concurrent change of the parent directories
        /// </remarks>
        public static bool IsInside(string path, string directory)
        {
            string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            string fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            if (fullPath == fullDirectory)
            {
                return true;
            }
            if (!IsBelow(fullPath, fullDirectory))
            {
                return false;
            }

            string? resolvedDirectory = ResolveExistingPart(fullDirectory), resolvedParent = ResolveExistingPart(Path.GetDirectoryName(fullPath)!);
            return resolvedDirectory is not null && resolvedParent is not null && (resolvedParent == resolvedDirectory || IsBelow(resolvedParent, resolvedDirectory));
        }
    }

    /// <summary>
    /// Check if a full path is below a full directory path
    /// </summary>
    private static bool IsBelow(string fullPath, string fullDirectory) => fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>
    /// Resolve the symlinks of the part of a full path that exists, the rest is appended as it is
    /// </summary>
    /// <param name="fullPath">Full path</param>
    /// <returns>Resolved path or null if a dangling symlink is in the way</returns>
    private static string? ResolveExistingPart(string fullPath)
    {
        string existing = fullPath, rest = string.Empty;
        string? resolved;
        while ((resolved = Path.GetRealPath(existing)) is null)
        {
            if (new FileInfo(existing).LinkTarget is not null)
            {
                return null;
            }
            rest = Path.Join(Path.GetFileName(existing), rest);
            existing = Path.GetDirectoryName(existing)!;
        }
        return Path.Join(resolved, rest).TrimEnd(Path.DirectorySeparatorChar);
    }
}
