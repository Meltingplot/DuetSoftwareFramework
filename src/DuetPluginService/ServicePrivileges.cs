using System;

namespace DuetPluginService;

/// <summary>
/// Privileges of this plugin service instance
/// </summary>
/// <remarks>
/// DSF runs one instance as dsf user (duetpluginservice.service) and one as root (duetpluginservice-root.service).
/// The commands check this value to decide which part of the work is up to this instance
/// </remarks>
public sealed class ServicePrivileges
{
    /// <summary>
    /// Whether this instance runs with root privileges
    /// </summary>
    public bool IsPrivileged { get; init; } = Environment.IsPrivilegedProcess;
}
