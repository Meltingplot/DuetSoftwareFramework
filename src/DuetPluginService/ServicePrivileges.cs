using DuetSharedLibrary;
using System;

namespace DuetPluginService;

/// <summary>
/// Privileges of this plugin service instance
/// </summary>
/// <remarks>
/// DSF runs one instance as dsf user (duetpluginservice.service) and one as root (duetpluginservice-root.service).
/// The commands check this value to decide which part of the work is up to this instance
/// </remarks>
public sealed record ServicePrivileges
{
    /// <summary>
    /// Whether this instance runs with root privileges
    /// </summary>
    public bool IsPrivileged { get; init; } = Environment.IsPrivilegedProcess;

    /// <summary>
    /// User ID this instance runs as. The root service starts a plugin with super-user permissions only from files owned by this user
    /// </summary>
    public int UserId { get; init; } = ProcessHelpers.GetEffectiveUserID();

    /// <summary>
    /// Why this instance must not run commands from its settings, or null if it may. The root service runs them as root, so it runs them only if nobody
    /// else may change its settings file (<see cref="RootSettingsFiles"/>). It reads the file once at start, so this is decided at start as well
    /// </summary>
    public string? SettingsError { get; init; }

    /// <summary>
    /// Check the settings file that this instance reads at start. Call this before the file is read
    /// </summary>
    /// <param name="settingsFile">Path of the settings file</param>
    /// <returns>Privileges with <see cref="SettingsError"/></returns>
    public ServicePrivileges ForSettingsFile(string settingsFile) =>
        (IsPrivileged && RootSettingsFiles.Check(settingsFile, UserId) is string error) ? this with { SettingsError = $"Refusing to run commands from the settings: {error}" } : this;

    /// <summary>
    /// Make sure this instance may run commands from its settings
    /// </summary>
    /// <exception cref="ArgumentException">Others than the root service may change the settings file</exception>
    public void CheckSettings()
    {
        if (SettingsError is not null)
        {
            throw new ArgumentException(SettingsError);
        }
    }
}
