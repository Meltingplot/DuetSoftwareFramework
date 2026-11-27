using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.ObjectModel;
using DuetHttpClient;
using NUnit.Framework;

namespace LabPrinterTests;

/// <summary>
/// Version of a fork build of RRF or Duet3Expansion as boards[].firmwareVersion reports it
/// </summary>
/// <remarks>
/// - Fork tags: 3.7.0-rc.2+1-mp.2, 3.7.0-mp.1, 3.7.0+1.mp.5 (D3E), 3.7.0+1.mp.11 (RRF), and from 3.7.1-rc.1 on 3.7.1-rc.1+mp.1, the form of DSF and DWC.
///   The release workflows of both forks start on "*+mp.*" or "*.mp.*" tags (.github/workflows/mp-release.yml), and DWC's version check takes "-mp.N"
///   for a part of the pre-release (DuetWebControl src/utils/version.ts). A test build adds -dev.N to the number of the coming release,
///   e.g. 3.7.0+1.mp.12-dev.1 (README.md, OutputBufferPressure row).
/// - The main board reports VERSION (RRF Version.h, Platform.cpp:223), an expansion board the version of its announcement (ExpansionManager.cpp:61).
/// - The order is that of the releases: a pre-release before the release, then the upstream build +N (none counts as 0), then mp.N, and a dev build
///   before the release of its number.
/// </remarks>
public readonly partial record struct ForkVersion(int Major, int Minor, int Patch, int PreRank, int PreNumber, int Build, int Mp, int Dev) : IComparable<ForkVersion>
{
    /// <summary>
    /// Parse a fork version. A version without "mp." is no fork build
    /// </summary>
    public static bool TryParse(string? text, out ForkVersion version)
    {
        Match match = VersionRegex().Match(text?.Trim() ?? string.Empty);
        if (!match.Success)
        {
            version = default;
            return false;
        }

        static int Number(Group group, int fallback) => group.Success ? int.Parse(group.Value, CultureInfo.InvariantCulture) : fallback;
        version = new(Number(match.Groups["major"], 0), Number(match.Groups["minor"], 0), Number(match.Groups["patch"], 0),
                      match.Groups["pre"].Value switch { "alpha" => 0, "beta" => 1, "rc" => 2, _ => 3 }, Number(match.Groups["preNumber"], 0),
                      Number(match.Groups["build"], 0), Number(match.Groups["mp"], 0), Number(match.Groups["dev"], int.MaxValue));
        return true;
    }

    public int CompareTo(ForkVersion other)
    {
        int[] mine = [Major, Minor, Patch, PreRank, PreNumber, Build, Mp, Dev], theirs = [other.Major, other.Minor, other.Patch, other.PreRank, other.PreNumber, other.Build, other.Mp, other.Dev];
        for (int i = 0; i < mine.Length; i++)
        {
            int result = mine[i].CompareTo(theirs[i]);
            if (result != 0)
            {
                return result;
            }
        }
        return 0;
    }

    [GeneratedRegex(@"^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-(?<pre>alpha|beta|rc)\.(?<preNumber>\d+))?(?:\+(?<build>\d+)[-.]|[-+.])mp\.(?<mp>\d+)(?:-dev\.(?<dev>\d+))?$")]
    private static partial Regex VersionRegex();
}

/// <summary>
/// A fork fix and the first fork release that contains it
/// </summary>
public sealed record FirmwareFix(string Name, string FirstRelease);

/// <summary>
/// Helpers of the tests for the expansion and tool boards
/// </summary>
public static partial class G7Helpers
{
    /// <summary>
    /// SAMC21 system stack of 800 bytes, D3E PR #11 (merge 2eb095de, Version.h "3.7.0-mp.1" in the same PR)
    /// </summary>
    public static readonly FirmwareFix D3E11 = new("D3E #11", "3.7.0-mp.1");

    /// <summary>
    /// DMAC_Handler CHID race that froze the SZP ADC, CoreN2G PR #2 (merged 2026-10-04), in D3E since tag 3.7.0-rc.2+1-mp.2 (D3E #10)
    /// </summary>
    public static readonly FirmwareFix CoreN2G2 = new("CoreN2G #2", "3.7.0-rc.2+1-mp.2");

    /// <summary>
    /// SAMC21 reset data written to the end, D3E PR #30 (merged 2026-11-24 after tag 3.7.0+1.mp.5; 3.7.0+1.mp.6 was never released)
    /// </summary>
    public static readonly FirmwareFix D3E30 = new("D3E #30", "3.7.1-rc.1+mp.1");

    /// <summary>
    /// M915 refuses a driver the board does not have, D3E PR #32 (merged 2026-11-24 after tag 3.7.0+1.mp.5; 3.7.0+1.mp.6 was never released)
    /// </summary>
    public static readonly FirmwareFix D3E32 = new("D3E #32", "3.7.1-rc.1+mp.1");

    /// <summary>
    /// No SBC file operation under boardsLock when a board restarts during a collection, RRF PR #112 C1 (merged 2026-11-24 as 766cab582 after tag
    /// 3.7.0+1.mp.11; 3.7.0+1.mp.12 was never released)
    /// </summary>
    public static readonly FirmwareFix Rrf112 = new("RRF #112", "3.7.1-rc.1+mp.1");

    /// <summary>
    /// RRF fork fixes of the expression and object model tests and of the trigger action check, merged after tag 3.7.0+1.mp.11 and first released in 3.7.1-rc.1+mp.1 (merge commits
    /// 6ba549213, 01f00c726, 5d7ed6e93, b840d4f24, f71ed72bd, 4c9c352f3, 2575c498f, 551ecab92, a1bdcb538, 0d4295065; none is an ancestor of the tag)
    /// </summary>
    public static readonly FirmwareFix Rrf102 = new("RRF #102", "3.7.1-rc.1+mp.1"), Rrf104 = new("RRF #104", "3.7.1-rc.1+mp.1"),
        Rrf105 = new("RRF #105", "3.7.1-rc.1+mp.1"), Rrf106 = new("RRF #106", "3.7.1-rc.1+mp.1"), Rrf108 = new("RRF #108", "3.7.1-rc.1+mp.1"),
        Rrf109 = new("RRF #109", "3.7.1-rc.1+mp.1"), Rrf110 = new("RRF #110", "3.7.1-rc.1+mp.1"), Rrf113 = new("RRF #113", "3.7.1-rc.1+mp.1"),
        Rrf114 = new("RRF #114", "3.7.1-rc.1+mp.1"), Rrf115 = new("RRF #115", "3.7.1-rc.1+mp.1");

    /// <summary>
    /// Skip a test unless the main board runs a firmware with all of the given fixes, so that a test whose bug resets RRF or hangs a channel costs
    /// no emergency stop on a firmware that predates the fix
    /// </summary>
    /// <param name="session">Session of the test</param>
    /// <param name="fixes">Fixes the test needs</param>
    public static void RequireMainBoardFixes(DuetHttpSession session, params FirmwareFix[] fixes)
    {
        string? version = Lab.Read(session, model => model.Boards.Count > 0 ? model.Boards[0].FirmwareVersion : null);
        foreach (FirmwareFix fix in fixes)
        {
            if (WhyNotFixed(fix, "The main board", version) is string whyNot)
            {
                Assert.Ignore(whyNot);
            }
        }
    }

    /// <summary>
    /// Fixes that LAB_ASSUME_FIXES declares present, e.g. "RRF#112 D3E#30 D3E#32", for a test or local build whose version cannot show them:
    /// a local build keeps the version of the last release (RRF Version.h at 3.7-dev 0d4295065 says 3.7.0+1.mp.11 and contains #112), and
    /// 3.7.0+1.mp.12-dev.1 is the test build of RRF #113, whose branch (8331cc1c6) does not contain #112
    /// </summary>
    private static readonly string[] AssumedFixes = (Environment.GetEnvironmentVariable("LAB_ASSUME_FIXES") ?? string.Empty)
        .Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(Normalize).ToArray();

    private static string Normalize(string fixName) => fixName.Replace(" ", string.Empty).ToUpperInvariant();

    /// <summary>
    /// Why a board cannot be checked for a fix, or null if its firmware contains the fix
    /// </summary>
    /// <param name="fix">Fix to check for</param>
    /// <param name="board">Name of the board in the reason, e.g. "Board 60 (SZP)"</param>
    /// <param name="firmwareVersion">boards[].firmwareVersion of the board</param>
    public static string? WhyNotFixed(FirmwareFix fix, string board, string? firmwareVersion)
    {
        if (AssumedFixes.Contains(Normalize(fix.Name)))
        {
            TestContext.Progress.WriteLine($"{board} runs {firmwareVersion}, {fix.Name} is assumed present (LAB_ASSUME_FIXES)");
            return null;
        }
        if (!ForkVersion.TryParse(fix.FirstRelease, out ForkVersion first))
        {
            throw new InvalidOperationException($"Bad first release {fix.FirstRelease} of {fix.Name}");
        }
        string hint = $"set LAB_ASSUME_FIXES={fix.Name.Replace(" ", string.Empty)} for a test build that contains it";
        if (!ForkVersion.TryParse(firmwareVersion, out ForkVersion version))
        {
            return $"{board} runs \"{firmwareVersion}\", no fork build, so it is unknown whether it contains {fix.Name} (first in fork release {fix.FirstRelease}; {hint})";
        }
        if (version.CompareTo(first) < 0)
        {
            return $"{board} runs {firmwareVersion}, which predates {fix.Name} (first in fork release {fix.FirstRelease}; {hint})";
        }
        return null;
    }

    /// <summary>
    /// Expansion boards by the board type they announce (boards[].shortName, BOARD_TYPE_NAME in Duet3Expansion src/Config/*.h)
    /// </summary>
    public static (int Address, string ShortName, int MaxMotors, string? FirmwareVersion)[] FindBoards(ObjectModel model, params string[] shortNames) =>
        [.. model.Boards.OfType<ExpansionBoard>()
            .Where(board => board.CanAddress is not null && shortNames.Contains(board.ShortName))
            .Select(board => (board.CanAddress!.Value, board.ShortName, board.MaxMotors, (string?)board.FirmwareVersion))];

    /// <summary>
    /// Number of the accelerometer on a board, the index in sensors.accelerometers that M956 P takes (RRF Accelerometers.cpp:549-552, the
    /// array keeps the numbers, EndstopsManager.cpp:87-92), or -1. The port starts with the CAN address, e.g. "60.i2c.lis" (chx350-config config.g:181)
    /// </summary>
    public static int FindAccelerometer(ObjectModel model, int canAddress)
    {
        for (int i = 0; i < model.Sensors.Accelerometers.Count; i++)
        {
            if (model.Sensors.Accelerometers[i]?.Port?.StartsWith($"{canAddress}.", StringComparison.Ordinal) == true)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Index of the analog sensor with the given name, or -1
    /// </summary>
    public static int FindAnalogSensor(ObjectModel model, string name)
    {
        for (int i = 0; i < model.Sensors.Analog.Count; i++)
        {
            if (model.Sensors.Analog[i]?.Name == name)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Name of the SZP coil thermistor, sensor 4 on port 60.temp0 (chx350-config config.g:172)
    /// </summary>
    public const string SzpCoilSensor = "SZP coil";

    /// <summary>
    /// Journal lines of DCS that show a broken link, as in OutputBufferPressure
    /// </summary>
    public static readonly string[] LinkErrors = ["Connection timeout", "Lost connection", "Connection to controller has been reset", "TfrRdy", "Out-of-order"];

    /// <summary>
    /// Report of a wait for the SBC task under a write lock (RRF SbcInterface.cpp:2697-2704, text RepRap.cpp:2362), logged by DCS like every
    /// error message of RRF (LinkService.cs:941-954, ObjectModel.cs:784-797)
    /// </summary>
    public const string InternalError = "Internal Error in";

    /// <summary>
    /// Start of the messages of RRF when a file operation of the SBC timed out (SbcInterface.cpp:2421-2691), e.g. "Timeout while trying to truncate file"
    /// </summary>
    public const string FileTimeout = "Timeout while trying to";

    /// <summary>
    /// Free system stack in part 1 of M122 B&lt;n&gt; of an expansion board (Duet3Expansion Tasks.cpp:980, CommandProcessor.cpp:573-576)
    /// </summary>
    [GeneratedRegex(@"Never used RAM (-?\d+), free system stack (\d+) words")]
    public static partial Regex FreeSystemStackRegex();

    /// <summary>
    /// Last software reset of an expansion board in part 2 of M122 B&lt;n&gt; (Duet3Expansion SoftwareReset.cpp:117-170, CommandProcessor.cpp:578-594)
    /// </summary>
    [GeneratedRegex(@"^Last software reset (?:at \d{4}-\d{2}-\d{2} \d{2}:\d{2}|time unknown), reason: (?<reason>[^,\r\n]+), [^\r\n]*?, slot (?<slot>\d+)", RegexOptions.Multiline)]
    public static partial Regex SoftwareResetRegex();

    /// <summary>
    /// Reply of M122 B&lt;n&gt; without reset data (CommandProcessor.cpp:587)
    /// </summary>
    public const string NoResetData = "Last software reset data not available";

    /// <summary>
    /// The lines of M122 B&lt;n&gt; about the last software reset of the board: time and reason, reset code, stack (SoftwareReset.cpp:117-195)
    /// </summary>
    public static string SoftwareResetLines(string m122) =>
        string.Join('\n', m122.Split('\n').Select(line => line.Trim()).Where(line =>
            line.StartsWith("Last software reset") || line.StartsWith("Software reset code") || line.StartsWith("Stack:")));

    /// <summary>
    /// Read a text file of the printer, or null if it does not exist
    /// </summary>
    public static async Task<string?> ReadFileAsync(DuetHttpSession session, string filename)
    {
        try
        {
            using HttpResponseMessage response = await session.DownloadAsync(filename);
            return await response.Content.ReadAsStringAsync();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Longest time in seconds in which the model of a session showed no new state.upTime, which RRF counts once a second (as in OutputBufferPressure)
    /// </summary>
    public static async Task<double> SampleUpTimeGapAsync(DuetHttpSession session, CancellationToken cancellationToken)
    {
        int lastUpTime = Lab.Read(session, model => model.State.UpTime);
        Stopwatch sinceChange = Stopwatch.StartNew();
        double maxGap = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            int upTime = Lab.Read(session, model => model.State.UpTime);
            if (upTime != lastUpTime)
            {
                maxGap = Math.Max(maxGap, sinceChange.Elapsed.TotalSeconds);
                lastUpTime = upTime;
                sinceChange.Restart();
            }
            try
            {
                await Task.Delay(100, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        return Math.Max(maxGap, sinceChange.Elapsed.TotalSeconds);
    }
}
