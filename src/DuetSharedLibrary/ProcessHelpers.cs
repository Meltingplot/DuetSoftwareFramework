using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace DuetSharedLibrary;

/// <summary>
/// Helper class to retrieve process information and perform operations on processes
/// </summary>
public static class ProcessHelpers
{
    /// <summary>
    /// Get the effective group ID
    /// </summary>
    /// <returns>Effective group ID</returns>
    public static int GetEffectiveGroupID() => Interop.getegid();

    /// <summary>
    /// Get the effective user ID
    /// </summary>
    /// <returns>Effective user ID</returns>
    public static int GetEffectiveUserID() => Interop.geteuid();

    /// <summary>
    /// Ask the process to terminate by sending SIGTERM to it. Nothing happens if it has gone already
    /// </summary>
    /// <param name="process">Process</param>
    /// <exception cref="IOException">Failed to kill process</exception>
    public static void Terminate(this Process process)
    {
        int error = Interop.kill(process.Id, Interop.SIGTERM);
        if (error < 0)
        {
            int errno = Marshal.GetLastWin32Error();
            if (errno != Interop.ESRCH)
            {
                throw new IOException($"Failed to kill process (error {errno})");
            }
        }
    }

    /// <summary>
    /// Read the parent PID of a given process from /proc/{pid}/stat
    /// </summary>
    /// <param name="pid">Process ID to look up</param>
    /// <returns>Parent PID, or 0 if it could not be determined</returns>
    public static int GetParentPid(int pid) => ReadStat(pid)?.ParentPid ?? 0;

    /// <summary>
    /// Read the parent PID and the start time of a given process from /proc/{pid}/stat
    /// </summary>
    /// <param name="pid">Process ID to look up</param>
    /// <returns>Parent PID and start time in clock ticks after boot, or null if the process does not exist</returns>
    private static (int ParentPid, ulong StartTime)? ReadStat(int pid)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            // Field 2 (comm) is parenthesised and may contain spaces, so scan past the final ')' to reach the PPID (field 4) and the start time (field 22)
            int lastParen = stat.LastIndexOf(')');
            if (lastParen < 0 || lastParen + 2 >= stat.Length)
            {
                return null;
            }
            string[] fields = stat[(lastParen + 2)..].Split(' ');
            if (fields.Length < 20 || !int.TryParse(fields[1], out int ppid) || !ulong.TryParse(fields[19], out ulong startTime))
            {
                return null;
            }
            return (ppid, startTime);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Get the processes below a given process, i.e. its children, their children and so on
    /// </summary>
    /// <param name="pid">Process ID</param>
    /// <returns>PIDs and start times of the descendants</returns>
    /// <remarks>
    /// The children of a process are re-parented when it exits, so they must be looked up while it is still running
    /// </remarks>
    public static List<(int Pid, ulong StartTime)> GetDescendants(int pid)
    {
        Dictionary<int, List<(int Pid, ulong StartTime)>> children = [];
        foreach (string directory in Directory.EnumerateDirectories("/proc"))
        {
            if (int.TryParse(Path.GetFileName(directory), out int childPid) && ReadStat(childPid) is (int parentPid, ulong startTime))
            {
                if (!children.TryGetValue(parentPid, out List<(int, ulong)>? list))
                {
                    list = [];
                    children.Add(parentPid, list);
                }
                list.Add((childPid, startTime));
            }
        }

        List<(int Pid, ulong StartTime)> descendants = [];
        Queue<int> pending = new([pid]);
        while (pending.TryDequeue(out int parent))
        {
            if (children.TryGetValue(parent, out List<(int Pid, ulong StartTime)>? list))
            {
                foreach ((int childPid, ulong startTime) in list)
                {
                    descendants.Add((childPid, startTime));
                    pending.Enqueue(childPid);
                }
            }
        }
        return descendants;
    }

    /// <summary>
    /// Kill a process with SIGKILL unless it has exited or its PID belongs to another process by now
    /// </summary>
    /// <param name="pid">Process ID</param>
    /// <param name="startTime">Start time of the process as returned by <see cref="GetDescendants(int)"/></param>
    /// <returns>Whether the process was still running and has been killed</returns>
    public static bool Kill(int pid, ulong startTime)
    {
        if (ReadStat(pid)?.StartTime == startTime)
        {
            return Interop.kill(pid, Interop.SIGKILL) == 0;
        }
        return false;
    }

    /// <summary>
    /// Get the executable path of a process by resolving /proc/{pid}/exe. Unlike Process.MainModule this does not
    /// parse the full module list from /proc/{pid}/maps, which allocates hundreds of kilobytes per call
    /// </summary>
    /// <param name="pid">Process ID to look up</param>
    /// <returns>Absolute executable path, or null if it could not be resolved</returns>
    public static string? GetExecutablePath(int pid)
    {
        try
        {
            return File.ResolveLinkTarget($"/proc/{pid}/exe", returnFinalTarget: false)?.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Check whether the process was exec'd with <c>AT_SECURE=1</c>. Parses /proc/{pid}/auxv looking for the AT_SECURE
    /// entry (type 23) set to non-zero. The kernel sets this when the exec crosses a privilege boundary (setuid,
    /// setgid, or file capabilities), causing glibc to ignore LD_PRELOAD and related environment variables. The bit is
    /// immutable for the life of the process so it cannot be stripped after the fact
    /// </summary>
    /// <param name="pid">Process ID to look up</param>
    /// <returns>True if the process's exec was secure-mode</returns>
    public static bool IsExecSecure(int pid)
    {
        try
        {
            ReadOnlySpan<byte> auxv = File.ReadAllBytes($"/proc/{pid}/auxv");
            int wordSize = IntPtr.Size;
            for (int offset = 0; offset + 2 * wordSize <= auxv.Length; offset += 2 * wordSize)
            {
                ulong type = ReadWord(auxv.Slice(offset, wordSize));
                if (type == 0)
                {
                    // AT_NULL terminator
                    return false;
                }
                if (type == 23)
                {
                    // AT_SECURE
                    return ReadWord(auxv.Slice(offset + wordSize, wordSize)) != 0;
                }
            }
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        static ulong ReadWord(ReadOnlySpan<byte> bytes) => bytes.Length == 8
            ? BinaryPrimitives.ReadUInt64LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }
}
