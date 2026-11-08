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

    /// <summary>
    /// Get the owner of a file system entry. A symlink is not followed
    /// </summary>
    /// <param name="path">Path of the entry</param>
    /// <returns>User ID of the owner, mode bits and whether the entry is a symlink</returns>
    /// <exception cref="IOException">Entry could not be queried, e.g. because it does not exist</exception>
    public static (int UserId, UnixFileMode Mode, bool IsSymlink) GetOwner(string path)
    {
        const ushort FileTypeMask = 0xF000, Symlink = 0xA000;
        statxbuf buf = default;
        if (Interop.statx(Interop.AT_FDCWD, path, Interop.AT_SYMLINK_NOFOLLOW, (uint)(Interop.STATX_TYPE | Interop.STATX_MODE | Interop.STATX_UID), ref buf) < 0)
        {
            throw new IOException($"Failed to query {path} (error {Marshal.GetLastWin32Error()})");
        }
        return ((int)buf.UserID, (UnixFileMode)(buf.Mode & ~FileTypeMask), (buf.Mode & FileTypeMask) == Symlink);
    }

    /// <summary>
    /// Check if a file system entry can only be changed by the given user: it is no symlink, belongs to that user and nobody else may write it.
    /// A service may act on such an entry without trusting other users
    /// </summary>
    /// <param name="path">Path of the entry</param>
    /// <param name="owner">User ID that must own the entry</param>
    /// <returns>Whether the entry exists and is protected</returns>
    public static bool IsProtected(string path, int owner)
    {
        try
        {
            (int userId, UnixFileMode mode, bool isSymlink) = GetOwner(path);
            return !isSymlink && userId == owner && (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
