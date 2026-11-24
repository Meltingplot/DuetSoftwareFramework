using System.Runtime.InteropServices;

#pragma warning disable 649,8981

/*
 * Header and data of capget(2) and capset(2) for _LINUX_CAPABILITY_VERSION_3, which passes two struct __user_cap_data_struct
 * for capabilities 0-31 and 32-63 (linux/capability.h).
 *
 * See https://man7.org/linux/man-pages/man2/capget.2.html
 */
[StructLayout(LayoutKind.Sequential)]
internal struct cap_user_header
{
    internal uint version;
    internal int pid;
}

[StructLayout(LayoutKind.Sequential)]
internal struct cap_user_data
{
    internal uint effective;
    internal uint permitted;
    internal uint inheritable;
    internal uint effectiveHigh;
    internal uint permittedHigh;
    internal uint inheritableHigh;
}

internal partial class Interop
{
    internal const uint _LINUX_CAPABILITY_VERSION_3 = 0x20080522;

    internal const int CAP_DAC_READ_SEARCH = 2;
    internal const int CAP_SYS_PTRACE = 19;

    [DllImport(LibcLibrary, SetLastError = true)]
    internal static extern int capget(ref cap_user_header header, ref cap_user_data data);

    [DllImport(LibcLibrary, SetLastError = true)]
    internal static extern int capset(ref cap_user_header header, ref cap_user_data data);
}
