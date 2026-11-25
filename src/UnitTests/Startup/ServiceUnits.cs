using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace UnitTests.Startup;

/// <summary>
/// Settings of the shipped systemd units that the services rely on
/// </summary>
[TestFixture]
public class ServiceUnits
{
    private static string ReadUnit(string package, string unit) => System.IO.File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..",
        "pkg", "common", package, "usr", "lib", "systemd", "system", unit));

    private static string? GetValue(string unitFile, string key)
    {
        Match match = Regex.Match(unitFile, $@"^{key}=(.*?)\s*$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// systemd starts DCS and DPS again whenever they end (Restart=always), without a limit on the restarts (StartLimitIntervalSec=0).
    /// DWS has to come back in the same way, else a crash or an OOM kill of DWS leaves the web interface down until the next boot
    /// </summary>
    [Test]
    public void WebServerIsRestartedLikeControlServer()
    {
        string dcs = ReadUnit("duetcontrolserver", "duetcontrolserver.service"), dws = ReadUnit("duetwebserver", "duetwebserver.service");
        Assert.That(GetValue(dcs, "Restart"), Is.EqualTo("always"));
        Assert.That(GetValue(dws, "Restart"), Is.EqualTo(GetValue(dcs, "Restart")), "Restart of duetwebserver.service");
        Assert.That(GetValue(dws, "RestartSec"), Is.EqualTo(GetValue(dcs, "RestartSec")), "RestartSec of duetwebserver.service");
        Assert.That(GetValue(dws, "StartLimitIntervalSec"), Is.EqualTo(GetValue(dcs, "StartLimitIntervalSec")), "StartLimitIntervalSec of duetwebserver.service");
    }
}
