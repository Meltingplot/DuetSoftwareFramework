using System.Runtime.InteropServices;

internal static partial class Interop
{
    internal const int SIGKILL = 9;
    internal const int SIGTERM = 15;

    /// <summary>
    /// Error number of kill() for a process that does not exist
    /// </summary>
    internal const int ESRCH = 3;

    [DllImport(LibcLibrary, SetLastError = true)]
    internal static extern int kill(int pid, int signal);
}
