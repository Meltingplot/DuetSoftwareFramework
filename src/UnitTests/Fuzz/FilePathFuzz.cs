using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer;
using DuetControlServer.Files;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using DcsModel = DuetControlServer.Model.ObjectModel;

namespace UnitTests.Fuzz;

/// <summary>
/// Fuzz test for FilePathResolver, which every file access of the codes goes through (MCodeHandler, KeywordHandler, Functions,
/// EventLogger, the macro requests in Link/Channel/Processor.cs and the file requests in LinkService.cs).
/// DSF guarantees that a resolved path stays inside the root of its drive, 0:/ being Settings.BaseDirectory and n:/ the path of
/// volume n, or else throws an ArgumentException (FilePathResolver.CombineContained, FilePathResolver.cs:54-72, GetDrivePath :80-119).
/// That is all it guarantees: the check is lexical (Path.GetFullPath does not resolve symbolic links), and the volumes are every mounted
/// file system that PeriodicUpdateService finds (PeriodicUpdateService.cs:526-583), so n:/ may be a system mount such as /home or /etc/ssh.
/// Containment in BaseDirectory holds for drive 0 and for paths without a drive in a directory on drive 0 only
/// </summary>
[TestFixture]
public class FilePathFuzz
{
    private string _baseDirectory = null!, _volumeDirectory = null!;
    private ServiceProvider _services = null!;
    private FilePathResolver _resolver = null!;
    private DcsModel _model = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        string root = Path.Combine(Path.GetTempPath(), "dsf-path-fuzz-" + Guid.NewGuid().ToString("N"));
        _baseDirectory = Path.Combine(root, "sd");
        _volumeDirectory = Path.Combine(root, "usb");
        Directory.CreateDirectory(_baseDirectory);
        Directory.CreateDirectory(_volumeDirectory);

        _services = DcsServices.Build(new Settings { BaseDirectory = _baseDirectory });
        _resolver = _services.GetRequiredService<FilePathResolver>();
        _model = _services.GetRequiredService<DcsModel>();
        using (_model.AccessReadWrite())
        {
            _model.Volumes.Clear();
            _model.Volumes.Add(new Volume { Path = _baseDirectory });
            _model.Volumes.Add(new Volume { Path = _volumeDirectory });
            _model.Volumes.Add(new Volume());       // a volume without a path, e.g. one that is not mounted
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _services.Dispose();
        Directory.Delete(Path.GetDirectoryName(_baseDirectory)!, true);
    }

    /// <summary>
    /// Path segments, among them traversals, drive prefixes, separators and characters that need care
    /// </summary>
    private static readonly string[] Segments =
    [
        "..", "..", ".", "", "sys", "gcodes", "a.g", "sub", "0:", "1:", "2:", "3:", "00:", "-1:", "99999999999:", "\\", "..\\..", "/", "//",
        "\0", "ä", "😀", "\u2024\u2024", "%2e%2e", "~", " ", "*", "?", "con", "...", ". .", ".. ", " ..", new string('x', 300), "usb", "sd"
    ];

    /// <summary>
    /// Fuzzed input
    /// </summary>
    /// <param name="Path">Path to resolve</param>
    /// <param name="Directory">Index of the directory argument, see <see cref="Directories"/></param>
    public sealed record Input(string Path, int Directory);

    /// <summary>
    /// Directory arguments: none, the well-known directories and explicit directories
    /// </summary>
    private static readonly object?[] Directories =
    [
        null, FileDirectory.Filaments, FileDirectory.Firmware, FileDirectory.GCodes, FileDirectory.Macros, FileDirectory.Menu, FileDirectory.System,
        FileDirectory.Web, (FileDirectory)99, "0:/sys", "1:/sub", "2:/x", "7:/x", "/macros", "../..", "0:/../..", "", "sys/../../.."
    ];

    private static Input Generate(Random random)
    {
        StringBuilder path = new();
        if (random.Next(3) == 0)
        {
            path.Append(random.Next(4) switch { 0 => "0:/", 1 => "1:", 2 => "2:/", _ => $"{random.Next(10)}:" });
        }
        if (random.Next(4) == 0)
        {
            path.Append('/');
        }
        for (int i = random.Next(6); i >= 0; i--)
        {
            path.Append(Segments[random.Next(Segments.Length)]);
            if (i > 0)
            {
                path.Append((random.Next(4) == 0) ? '\\' : '/');
            }
        }
        if (random.Next(5) == 0)
        {
            path.Insert(random.Next(path.Length + 1), (char)random.Next(0, 0x3000));
        }
        return new Input(path.ToString(), random.Next(Directories.Length));
    }

    private static string Format(Input input) => $"new Input(\"{input.Path.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\0", "\\0")}\", {input.Directory}) /* {Directories[input.Directory] ?? "null"} */";

    /// <summary>
    /// Root of a drive, or null if the drive does not exist (FilePathResolver.GetDrivePath, FilePathResolver.cs:80-95)
    /// </summary>
    private string? DriveRoot(int drive) => drive switch
    {
        0 => Path.GetFullPath(_baseDirectory),
        1 => _volumeDirectory,
        _ => null
    };

    /// <summary>
    /// Drive whose root a path must stay in, following FilePathResolver.ToPhysical (FilePathResolver.cs:197-217)
    /// </summary>
    private static int? GetDrive(string path, string? directory)
    {
        static int? driveOf(string value)
        {
            int colon = value.IndexOf(':');
            if (colon > 0 && value[..colon].All(char.IsAsciiDigit))
            {
                return int.TryParse(value[..colon], out int drive) ? drive : null;
            }
            return null;
        }

        path = path.Replace('\\', '/');
        if (driveOf(path) is int pathDrive)
        {
            return pathDrive;
        }
        if (directory is not null && !path.StartsWith('/') && driveOf(directory) is int directoryDrive)
        {
            return directoryDrive;
        }
        return 0;
    }

    private string? DirectoryOf(object? directory)
    {
        if (directory is FileDirectory fileDirectory)
        {
            using (_model.AccessReadOnly())
            {
                return fileDirectory switch
                {
                    FileDirectory.Filaments => _model.Directories.Filaments,
                    FileDirectory.Firmware => _model.Directories.Firmware,
                    FileDirectory.GCodes => _model.Directories.GCodes,
                    FileDirectory.Macros => _model.Directories.Macros,
                    FileDirectory.Menu => _model.Directories.Menu,
                    FileDirectory.Web => _model.Directories.Web,
                    _ => _model.Directories.System
                };
            }
        }
        return (string?)directory;
    }

    /// <summary>
    /// Resolve a path with the synchronous or the asynchronous variant
    /// </summary>
    private string Resolve(Input input, bool async)
    {
        object? directory = Directories[input.Directory];
        if (directory is FileDirectory fileDirectory)
        {
            return async ? _resolver.ToPhysicalAsync(input.Path, fileDirectory).GetAwaiter().GetResult() : _resolver.ToPhysical(input.Path, fileDirectory);
        }
        return async ? _resolver.ToPhysicalAsync(input.Path, (string?)directory).GetAwaiter().GetResult() : _resolver.ToPhysical(input.Path, (string?)directory);
    }

    private static (string? result, Exception? error) Try(Func<string> resolve)
    {
        try
        {
            return (resolve(), null);
        }
        catch (Exception e)
        {
            return (null, e);
        }
    }

    private static bool IsInside(string path, string root) => path == root || path.StartsWith(root.EndsWith('/') ? root : root + '/', StringComparison.Ordinal);

    private void Check(Input input)
    {
        (string? result, Exception? error) = Try(() => Resolve(input, false));
        (string? asyncResult, Exception? asyncError) = Try(() => Resolve(input, true));

        // Both variants implement the same rules
        if (result != asyncResult || error?.GetType() != asyncError?.GetType())
        {
            throw new FuzzFailure("sync vs async", $"ToPhysical gave '{result}' ({error?.Message}), ToPhysicalAsync '{asyncResult}' ({asyncError?.Message})");
        }

        // ArgumentException is the defined error, for paths escaping the root, unknown drives and invalid characters
        if (error is not null)
        {
            if (error is not ArgumentException)
            {
                throw new FuzzFailure("exception", $"{error.GetType().Name}: {error.Message}");
            }
            return;
        }

        int? drive = GetDrive(input.Path, DirectoryOf(Directories[input.Directory]));
        string? root = (drive is not null) ? DriveRoot(drive.Value) : null;
        if (root is null)
        {
            throw new FuzzFailure("unknown drive", $"Resolved '{result}' on drive {drive?.ToString() ?? "(invalid)"}, which does not exist");
        }
        if (!IsInside(result!, root))
        {
            throw new FuzzFailure("containment", $"'{result}' is outside the root {root} of drive {drive}");
        }

        // The virtual path of the result resolves to the same physical path (FilePathResolver.ToVirtual, FilePathResolver.cs:282-303)
        string virtualPath = _resolver.ToVirtual(result!);
        if (virtualPath != _resolver.ToVirtualAsync(result!).GetAwaiter().GetResult())
        {
            throw new FuzzFailure("virtual sync vs async", $"ToVirtual and ToVirtualAsync differ for '{result}'");
        }
        if (!virtualPath.StartsWith($"{drive}:/", StringComparison.Ordinal))
        {
            throw new FuzzFailure("virtual drive", $"'{result}' maps to '{virtualPath}', not to drive {drive}");
        }
        // A trailing separator names the same location
        if (Path.TrimEndingDirectorySeparator(_resolver.ToPhysical(virtualPath)) != Path.TrimEndingDirectorySeparator(result!))
        {
            throw new FuzzFailure("round trip", $"'{result}' maps to '{virtualPath}', which resolves to '{_resolver.ToPhysical(virtualPath)}'");
        }
    }

    private static IEnumerable<Input> Shrink(Input input)
    {
        for (int i = 0; i < input.Path.Length; i++)
        {
            yield return input with { Path = input.Path.Remove(i, 1) };
        }
        if (input.Directory != 0)
        {
            yield return input with { Directory = 0 };
        }
    }

    [Test]
    public void ResolvePaths() => Fuzzer.Run(Generate, Check, Format, Shrink);
}
