using System.Runtime.InteropServices;

#pragma warning disable 649,8981

/*
 * Leading fields of struct statvfs, which are unsigned long on every architecture.
 * The size of the remaining fields differs between architectures, so the struct only reserves enough space for them.
 *
 * See https://man7.org/linux/man-pages/man3/statvfs.3.html
 */
[StructLayout(LayoutKind.Sequential, Size = 256)]
internal struct statvfsbuf
{
    internal nuint BlockSize;       // f_bsize: file system block size
    internal nuint FragmentSize;    // f_frsize: fundamental file system block size
}

internal partial class Interop
{
    [DllImport(LibcLibrary, SetLastError = true, CharSet = CharSet.Ansi)]
    internal static extern int statvfs(string path, ref statvfsbuf buf);
}
