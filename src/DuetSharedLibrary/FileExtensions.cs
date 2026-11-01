using System.IO;
using System.Runtime.InteropServices;

namespace DuetSharedLibrary;

/// <summary>
/// Generic file extensions
/// </summary>
public static class FileExtensions
{
    /// <summary>
    /// Change the owner of a file or directory
    /// </summary>
    /// <param name="filename">Filename</param>
    /// <param name="uid">User ID</param>
    /// <param name="gid">Group ID</param>
    /// <exception cref="IOException">Operation failed</exception>
    public static void ChangeOwner(string filename, int uid, int gid)
    {
        int error = Interop.chown(filename, uid, gid);
        if (error < 0)
        {
            throw new IOException($"Failed to change owner of {filename} (error {Marshal.GetLastWin32Error()})");
        }
    }

    /// <summary>
    /// Get the fundamental block size of the file system that holds the given path, i.e. its allocation unit
    /// </summary>
    /// <param name="path">File or directory on the file system</param>
    /// <returns>Block size in bytes</returns>
    /// <exception cref="IOException">Operation failed</exception>
    public static long GetFileSystemBlockSize(string path)
    {
        statvfsbuf buf = default;
        if (Interop.statvfs(path, ref buf) < 0)
        {
            throw new IOException($"Failed to query the file system of {path} (error {Marshal.GetLastWin32Error()})");
        }
        return (long)buf.FragmentSize;
    }
}
