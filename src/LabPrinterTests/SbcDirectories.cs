namespace LabPrinterTests;

/// <summary>
/// Working directories of the tests on the SBC. Root writes into them, so each must be a real directory of root that no other user can change.
/// Below /var/tmp, which has the sticky bit, no other user can remove or rename a directory of root, so it is enough to check it once after
/// creating it: a symbolic link or a directory that another user created first, in which root would write through a path that user controls,
/// stops the script instead
/// </summary>
public static class SbcDirectories
{
    /// <summary>
    /// Directory of the side-load and the fault injection files (run-lab-tests.py SIDE_LOAD_DIR, SPI.cs FaultControlFile)
    /// </summary>
    public const string LabTests = "/var/tmp/lab-tests";

    /// <summary>
    /// Shell lines that create a directory as root with mode 0755 unless it exists, and exit with an error unless it is then a directory of root
    /// with that mode. stat without -L reports a symbolic link itself, so a link fails the check
    /// </summary>
    /// <param name="path">Absolute path of the directory</param>
    public static string CreateRootDirectoryScript(string path) => $$"""
        sudo -n mkdir -m 0755 '{{path}}' 2> /dev/null || true
        [ "$(stat -c '%u %F %a' '{{path}}' 2> /dev/null)" = "0 directory 755" ] || { echo "{{path}} is not a directory of root with mode 755, the test does not use it" >&2; exit 1; }
        """;
}
