using System;
using System.Linq;
using System.Threading.Tasks;
using DuetAPI;
using DuetControlServer.Codes.Meta.Parsing;
using NUnit.Framework;
using UnitTests.FakeFirmware;
using SbcRequest = DuetControlServer.Link.Protocol.SbcRequests.Request;

namespace UnitTests.Codes;

/// <summary>
/// datetime() of a string on the SBC (CU9 in design/open-findings.md). RRF converts only the format "yyyy-mm-ddThh:mm:ss"
/// (RRF ExpressionParser.cpp ParseDateTime, wiki Gcode_meta_commands.md), reads it with strptime, which takes fields without
/// leading zeros, ignores the rest after the seconds and leaves the day and the time of day to mktime (RRF strptime.cpp), and refuses
/// other strings with "string is not a valid date and time". DSF converts what .NET can parse itself, which includes more formats
/// than RRF as a feature of the SBC, and forwards every other string to RRF
/// </summary>
[TestFixture]
public class DateTimeStrings
{
    [TestCase("2026-12-01T10:20:30", 2026, 12, 1, 10, 20, 30)]
    [TestCase("2024-02-29T23:59:59", 2024, 2, 29, 23, 59, 59)]
    [TestCase("1970-01-01T00:00:00", 1970, 1, 1, 0, 0, 0)]
    [TestCase(" 2026-12-01T10:20:30", 2026, 12, 1, 10, 20, 30, Description = "RRF's strtol skips the leading space as well")]
    public void FirmwareFormatIsConvertedOnTheSbc(string text, int year, int month, int day, int hour, int minute, int second)
    {
        Assert.That(MetaExpressionParser.TryEvaluate($"datetime(\"{text}\")", null, out object? value), Is.True);
        Assert.That(value, Is.EqualTo(new DateTime(year, month, day, hour, minute, second)));
    }

    [TestCase("2026-12-01", 2026, 12, 1, 0, 0, 0, Description = "date only")]
    [TestCase("12/01/2026", 2026, 12, 1, 0, 0, 0, Description = "US format")]
    [TestCase("2026-12-01 10:20:30", 2026, 12, 1, 10, 20, 30, Description = "space instead of T")]
    public void MoreFormatsAreConvertedOnTheSbc(string text, int year, int month, int day, int hour, int minute, int second)
    {
        // RRF refuses these; they stay a feature of the SBC
        Assert.That(MetaExpressionParser.TryEvaluate($"datetime(\"{text}\")", null, out object? value), Is.True);
        Assert.That(value, Is.EqualTo(new DateTime(year, month, day, hour, minute, second)));
    }

    [Test]
    public void TimeZoneIsConvertedToLocalTimeOnTheSbc()
    {
        // RRF ignores the "Z" and gives 10:20:30; the SBC keeps converting it to its local time
        Assert.That(MetaExpressionParser.TryEvaluate("datetime(\"2026-12-01T10:20:30Z\")", null, out object? value), Is.True);
        Assert.That(value, Is.EqualTo(new DateTime(2026, 12, 1, 10, 20, 30, DateTimeKind.Utc).ToLocalTime()));
    }

    [TestCase("2026-12-01T10:20:30x", Description = "RRF ignores the rest after the seconds")]
    [TestCase("2026-1-5T0:0:0", Description = "fields without leading zeros, taken by RRF")]
    [TestCase("2026-02-30T00:00:00", Description = "day out of range, RRF leaves it to mktime")]
    [TestCase("2026-12-01T24:00:00", Description = "hour out of range, RRF leaves it to mktime")]
    [TestCase("2026-99-01T00:00:00", Description = "month out of range, refused by RRF since RRF #104")]
    [TestCase("", Description = "refused by RRF")]
    public void StringsDotNetCannotParseAreLeftToTheFirmware(string text)
    {
        Assert.That(MetaExpressionParser.TryEvaluate($"datetime(\"{text}\")", null, out _), Is.False);
    }

    [Test]
    public void OtherTypesAreStillRefusedOnTheSbc()
    {
        // RRF refuses a value that is neither a number, a DateTime nor a string with this text (RRF ExpressionParser.cpp, Function::datetime)
        Assert.That(() => MetaExpressionParser.TryEvaluate("datetime(true)", null, out _),
            Throws.InstanceOf<CodeParserException>().With.Message.StartsWith("can't convert value to DateTime"));
    }

    [Test]
    public async Task EchoForwardsAStringDotNetCannotParse()
    {
        // The fast path gives up, so the expression goes to the firmware (Expressions.cs:1009-1042) as it does for a variable
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        await host.ExecuteAsync("echo {datetime(\"2026-99-01T00:00:00\")}");
        Assert.That(host.Firmware.GetReceivedRequests().Where(request => request.Request == SbcRequest.EvaluateExpression).Select(request => request.Text),
            Has.Some.Contains("datetime(\"2026-99-01T00:00:00\")"));
    }

    [Test]
    public async Task EchoConvertsTheFirmwareFormatOnTheSbc()
    {
        await using FakeFirmwareHost host = await FakeFirmwareHost.StartAsync();
        Assert.That(await host.ExecuteAsync("echo {datetime(\"2026-12-01T10:20:30\")}"), Is.EqualTo("2026-12-01T10:20:30\n"));
        Assert.That(host.Firmware.GetReceivedRequests().Any(request => request.Request == SbcRequest.EvaluateExpression), Is.False);
    }
}
