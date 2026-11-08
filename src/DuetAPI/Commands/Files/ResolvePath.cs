using DuetAPI.Utility;

namespace DuetAPI.Commands;

/// <summary>
/// Resolve a RepRapFirmware-style path to an actual file path
/// </summary>
[RequiredPermissions(SbcPermissions.CommandExecution | SbcPermissions.FileSystemAccess)]
public partial class ResolvePath : Command<string>
{
    /// <summary>
    /// Path that is RepRapFirmware-compatible
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Optional base directory to resolve the path relative to
    /// </summary>
    public FileDirectory? BaseDirectory { get; set; }

    /// <summary>
    /// Path is going to be deleted, so the command fails if it resolves to the root directory of its volume.
    /// RepRapFirmware does not allow the root folder to be deleted either
    /// </summary>
    public bool ForDeletion { get; set; }
}
