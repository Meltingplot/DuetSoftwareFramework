using DuetSharedLibrary;
using System.IO;

namespace DuetPluginService;

/// <summary>
/// Checks of the files that the root service takes the commands it runs from: its settings file (plugins.json) and the AppArmor template (apparmor.conf).
/// The root service runs these commands and loads profiles made from the template as root, so it uses the files only if nobody but itself may change them,
/// like the files of root plugins (<see cref="RootPluginFiles"/>). The packages set them up like this: root:root 0644 in /opt/dsf/conf, which belongs to
/// root and has the sticky bit (root:dsf 1775), so that the dsf group may create its own files there but not replace the ones of root between a check
/// and their use
/// </summary>
public static class RootSettingsFiles
{
    /// <summary>
    /// Check that only the root service may change a file: it must be a regular file (no symlink) that belongs to the root service and that nobody else may
    /// write, in a directory that belongs to the root service and in which nobody else may rename or remove it
    /// </summary>
    /// <param name="file">File to check</param>
    /// <param name="owner">User ID of the root service</param>
    /// <returns>Error message or null if the file is set up correctly</returns>
    public static string? Check(string file, int owner)
    {
        string path = Path.GetFullPath(file), directory = Path.GetDirectoryName(path)!;
        try
        {
            (int userId, UnixFileMode mode, bool isSymlink) = FileExtensions.GetOwner(directory);
            if (!isSymlink && userId == owner && !mode.HasFlag(UnixFileMode.OtherWrite) && (!mode.HasFlag(UnixFileMode.GroupWrite) || mode.HasFlag(UnixFileMode.StickyBit)) &&
                FileExtensions.IsProtected(path, owner) && !Directory.Exists(path))
            {
                return null;
            }
        }
        catch (IOException)
        {
            // Handled below
        }
        return $"{path} must be a regular file of the root service that nobody else may write, in a directory of the root service where nobody else may replace it " +
            "(root:root 0644 in /opt/dsf/conf, root:dsf 1775)";
    }
}
