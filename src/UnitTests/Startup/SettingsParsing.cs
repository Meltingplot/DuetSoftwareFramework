using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DuetAPI.Connection;
using DuetAPI.ObjectModel;
using DuetControlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace UnitTests.Startup;

/// <summary>
/// How DCS turns config.json and the command line into its settings: Program.cs adds the config file as a JSON configuration source
/// (Program.cs:158-160) and passes the options to <see cref="ServiceCollectionExtensions.AddSettings"/> (Program.cs:183, Settings.cs:481-570)
/// </summary>
[TestFixture]
public class SettingsParsing
{
    private string _directory = null!;

    [SetUp]
    public void CreateDirectory()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dsf-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void DeleteDirectory() => Directory.Delete(_directory, true);

    /// <summary>
    /// Config file in a directory that does not exist, so that <see cref="Settings.PostConfigure"/> writes nothing (Settings.cs:431)
    /// </summary>
    private FileInfo NoConfigFile => new(Path.Combine(_directory, "missing", "config.json"));

    /// <summary>
    /// Bind the settings like DCS does: the config file as JSON source, then <see cref="ServiceCollectionExtensions.AddSettings"/>
    /// </summary>
    /// <param name="json">Content of the config file, written to <paramref name="jsonFile"/> or a new file in the test directory</param>
    /// <param name="configFile">Config file that DCS writes if it is missing (-c), always in the test directory</param>
    /// <param name="jsonFile">Config file to read</param>
    private (Settings Settings, string StartErrorFile) Bind(string? json = null, FileInfo? configFile = null, bool updateOnly = false, LogLevel? logLevel = null,
        DirectoryInfo? socketDirectory = null, string? socketFile = null, DirectoryInfo? baseDirectory = null, string? jsonFile = null)
    {
        ConfigurationBuilder builder = new();
        if (json is not null)
        {
            jsonFile ??= Path.Combine(_directory, "input-" + Guid.NewGuid().ToString("N") + ".json");
            System.IO.File.WriteAllText(jsonFile, json);
        }
        if (jsonFile is not null)
        {
            builder.AddJsonFile(jsonFile, optional: true);
        }

        IConfiguration configuration = builder.Build();
        ServiceCollection services = new();
        services.AddSettings(configuration, updateOnly, logLevel, configFile ?? NoConfigFile, socketDirectory, socketFile, baseDirectory, out string startErrorFile);
        using ServiceProvider provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IOptions<Settings>>().Value, startErrorFile);
    }

    [Test]
    public void DefaultsWithoutConfiguration()
    {
        (Settings settings, string startErrorFile) = Bind(configFile: NoConfigFile);
        Settings defaults = new();

        Assert.That(startErrorFile, Is.EqualTo(Defaults.StartErrorFile));
        Assert.That(settings.LogLevel, Is.EqualTo(LogLevel.Information));
        Assert.That(settings.UpdateOnly, Is.False);
        Assert.That(settings.ConfigFile, Is.EqualTo(NoConfigFile.FullName));
        Assert.That(settings.SocketDirectory, Is.EqualTo(Defaults.SocketDirectory));
        Assert.That(settings.SocketFile, Is.EqualTo(Defaults.SocketFile));
        Assert.That(settings.FullSocketPath, Is.EqualTo(Defaults.FullSocketPath));
        Assert.That(settings.CommunicationMethod, Is.EqualTo(CommunicationMethod.SPI));
        Assert.That(settings.FirmwareComments, Is.EqualTo(defaults.FirmwareComments));
        Assert.That(settings.NumLayersFilters, Is.EqualTo(defaults.NumLayersFilters));
        Assert.That(settings.PrintTimeFilters, Is.EqualTo(defaults.PrintTimeFilters));
    }

    [Test]
    public void ConfigFileValuesAreBound()
    {
        (Settings settings, string startErrorFile) = Bind("""
            {
              "BaseDirectory": "/srv/sd",
              "StartErrorFile": "/srv/dcs.err",
              "CommunicationMethod": "usb",
              "SbcBufferSize": 4096,
              "SpiFrequency": 2000000,
              "AutoUpdateFirmware": false,
              "MaxMessageAge": 12.5,
              "UnknownKey": "ignored"
            }
            """, NoConfigFile);

        Assert.That(settings.BaseDirectory, Is.EqualTo("/srv/sd"));
        Assert.That(settings.StartErrorFile, Is.EqualTo("/srv/dcs.err"));
        Assert.That(startErrorFile, Is.EqualTo("/srv/dcs.err"), "Program.cs writes start errors to this file once the settings are added");
        Assert.That(settings.CommunicationMethod, Is.EqualTo(CommunicationMethod.USB));
        Assert.That(settings.SbcBufferSize, Is.EqualTo(4096));
        Assert.That(settings.SpiFrequency, Is.EqualTo(2_000_000));
        Assert.That(settings.AutoUpdateFirmware, Is.False);
        Assert.That(settings.MaxMessageAge, Is.EqualTo(12.5));
    }

    /// <summary>
    /// "LogLevel" is taken out of the binding and parsed with <see cref="DuetSharedLibrary.LogLevelHelper"/> (Settings.cs:487-499), which accepts the
    /// short names that the default config.json and the converter use (LogLevelJsonConverter.cs:27-37). An unknown name gives Information
    /// </summary>
    [TestCase("trace", LogLevel.Trace)]
    [TestCase("debug", LogLevel.Debug)]
    [TestCase("info", LogLevel.Information)]
    [TestCase("Information", LogLevel.Information)]
    [TestCase("warn", LogLevel.Warning)]
    [TestCase("warning", LogLevel.Warning)]
    [TestCase("error", LogLevel.Error)]
    [TestCase("fatal", LogLevel.Critical)]
    [TestCase("critical", LogLevel.Critical)]
    [TestCase("off", LogLevel.None)]
    [TestCase("none", LogLevel.None)]
    [TestCase("bogus", LogLevel.Information)]
    public void LogLevelOfTheConfigFile(string name, LogLevel expected)
    {
        (Settings settings, _) = Bind($$"""{ "LogLevel": "{{name}}" }""", NoConfigFile);
        Assert.That(settings.LogLevel, Is.EqualTo(expected));
    }

    /// <summary>
    /// -l is applied after the binding (Settings.cs:539-542), so it wins over the config file
    /// </summary>
    [Test]
    public void CommandLineLogLevelOverridesTheConfigFile()
    {
        (Settings settings, _) = Bind("""{ "LogLevel": "warn" }""", NoConfigFile, logLevel: LogLevel.Debug);
        Assert.That(settings.LogLevel, Is.EqualTo(LogLevel.Debug));
    }

    /// <summary>
    /// Config files of DSF 3.6 and earlier use the SPI names of the link settings (Settings.cs:459-466)
    /// </summary>
    [TestCase("SpiBufferSize", nameof(Settings.SbcBufferSize), 4096)]
    [TestCase("SpiConnectTimeout", nameof(Settings.SbcConnectTimeout), 750)]
    [TestCase("SpiTransferTimeout", nameof(Settings.SbcTransferTimeout), 650)]
    [TestCase("SpiConnectionTimeout", nameof(Settings.SbcConnectionTimeout), 5000)]
    [TestCase("MaxSpiRetries", nameof(Settings.MaxSbcRetries), 3)]
    public void LegacySpiKeysAreMapped(string legacyKey, string currentKey, int value)
    {
        (Settings settings, _) = Bind($$"""{ "{{legacyKey}}": {{value}} }""", NoConfigFile);
        Assert.That(typeof(Settings).GetProperty(currentKey)!.GetValue(settings), Is.EqualTo(value));
    }

    /// <summary>
    /// A legacy key applies only if the current key is not there (Settings.cs:527-531)
    /// </summary>
    [Test]
    public void CurrentKeyWinsOverTheLegacyKey()
    {
        (Settings settings, _) = Bind("""{ "SbcBufferSize": 2048, "SpiBufferSize": 4096 }""", NoConfigFile);
        Assert.That(settings.SbcBufferSize, Is.EqualTo(2048));
    }

    /// <summary>
    /// Config files written before the filters became plain patterns hold serialized Regex objects; the pattern is kept and the options are
    /// dropped (Settings.cs:501-515), CompileFilters applies its own (Settings.cs:424)
    /// </summary>
    [Test]
    public void SerializedRegexFiltersKeepTheirPatterns()
    {
        (Settings settings, _) = Bind("""
            {
              "LayerHeightFilters": [
                { "Pattern": "^layer_height=(?<mm>\\d+\\.?\\d*)", "Options": 17 },
                { "Pattern": "^lh (?<mm>\\d+)", "Options": 1 }
              ]
            }
            """, NoConfigFile);
        Assert.That(settings.LayerHeightFilters, Is.EqualTo(new[] { @"^layer_height=(?<mm>\d+\.?\d*)", @"^lh (?<mm>\d+)" }));
    }

    /// <summary>
    /// The binder would add a configured list to the defaults, so a configured list is emptied first and unconfigured ones keep their defaults
    /// (Settings.cs:572-603)
    /// </summary>
    [Test]
    public void ConfiguredListReplacesTheDefaults()
    {
        (Settings settings, _) = Bind("""{ "FirmwareComments": [ "custom" ], "NumLayersFilters": [ "^layers=(\\d+)" ] }""", NoConfigFile);
        Settings defaults = new();
        Assert.That(settings.FirmwareComments, Is.EqualTo(new[] { "custom" }));
        Assert.That(settings.NumLayersFilters, Is.EqualTo(new[] { @"^layers=(\d+)" }));
        Assert.That(settings.FilamentFilters, Is.EqualTo(defaults.FilamentFilters));
        Assert.That(settings.SimulatedTimeFilters, Is.EqualTo(defaults.SimulatedTimeFilters));
    }

    /// <summary>
    /// -u, -c, -S and -b are applied after the binding (Settings.cs:539-567)
    /// </summary>
    [Test]
    public void CommandLineOptionsOverrideTheConfigFile()
    {
        DirectoryInfo socketDirectory = new(Path.Combine(_directory, "run"));
        DirectoryInfo baseDirectory = new(Path.Combine(_directory, "sd"));
        (Settings settings, _) = Bind("""{ "SocketDirectory": "/srv/run", "SocketFile": "other.sock", "BaseDirectory": "/srv/sd" }""", NoConfigFile,
            updateOnly: true, socketDirectory: socketDirectory, socketFile: "dcs-test.sock", baseDirectory: baseDirectory);

        Assert.That(settings.UpdateOnly, Is.True);
        Assert.That(settings.ConfigFile, Is.EqualTo(NoConfigFile.FullName));
        Assert.That(settings.SocketDirectory, Is.EqualTo(socketDirectory.FullName));
        Assert.That(settings.SocketFile, Is.EqualTo("dcs-test.sock"));
        Assert.That(settings.FullSocketPath, Is.EqualTo(Path.Combine(socketDirectory.FullName, "dcs-test.sock")));
        Assert.That(settings.BaseDirectory, Is.EqualTo(baseDirectory.FullName));
    }

    /// <summary>
    /// The help of -s asks for the full path, and a rooted path sets the directory as well, also over -S (Settings.cs:551-563)
    /// </summary>
    [Test]
    public void RootedSocketFileSetsTheDirectoryToo()
    {
        string socketFile = Path.Combine(_directory, "sockets", "dcs.sock");
        (Settings settings, _) = Bind(configFile: NoConfigFile, socketDirectory: new DirectoryInfo("/srv/run"), socketFile: socketFile);
        Assert.That(settings.SocketDirectory, Is.EqualTo(Path.GetDirectoryName(socketFile)));
        Assert.That(settings.SocketFile, Is.EqualTo("dcs.sock"));
        Assert.That(settings.FullSocketPath, Is.EqualTo(socketFile));
    }

    /// <summary>
    /// A missing config file is written with the settings (Settings.cs:429-446), as the wiki promises for a deleted config.json
    /// (User_manual/Machine_configuration/DSF_Other.md:204). Reading it back gives the same settings
    /// </summary>
    [Test]
    public void MissingConfigFileIsWritten()
    {
        FileInfo configFile = new(Path.Combine(_directory, "config.json"));
        (Settings written, _) = Bind(configFile: configFile);
        Assert.That(configFile.Exists, Is.True);

        JsonObject json = JsonNode.Parse(System.IO.File.ReadAllText(configFile.FullName))!.AsObject();
        Assert.That((string?)json["LogLevel"], Is.EqualTo("info"), "LogLevelJsonConverter writes the short name");
        Assert.That(json.ContainsKey(nameof(Settings.ConfigFile)), Is.False, "ConfigFile is not saved");
        Assert.That(json.ContainsKey(nameof(Settings.UpdateOnly)), Is.False, "UpdateOnly is not saved");

        (Settings readBack, _) = Bind(configFile: configFile, jsonFile: configFile.FullName);
        Assert.That(JsonSerializer.Serialize(readBack, SettingsContext.Default.Settings), Is.EqualTo(JsonSerializer.Serialize(written, SettingsContext.Default.Settings)));
    }

    /// <summary>
    /// No config file is written if its directory does not exist (Settings.cs:431)
    /// </summary>
    [Test]
    public void ConfigFileInMissingDirectoryIsNotWritten()
    {
        Bind(configFile: NoConfigFile);
        Assert.That(Directory.Exists(NoConfigFile.DirectoryName), Is.False);
    }

    /// <summary>
    /// An existing config file is left as it is, even with keys DCS does not know
    /// </summary>
    [Test]
    public void ExistingConfigFileIsNotRewritten()
    {
        string configFile = Path.Combine(_directory, "config.json");
        const string content = """{ "Backlog": 7, "Custom": "kept" }""";
        (Settings settings, _) = Bind(content, new FileInfo(configFile), jsonFile: configFile);
        Assert.That(settings.Backlog, Is.EqualTo(7));
        Assert.That(System.IO.File.ReadAllText(configFile), Is.EqualTo(content));
    }

    /// <summary>
    /// The filters match case-insensitively unless a pattern switches it off inline (Settings.cs:51, 355, 424)
    /// </summary>
    [Test]
    public void FiltersIgnoreCaseUnlessSwitchedOffInline()
    {
        List<Regex> filters = Settings.CompileFilters([@"^layer_height\D+(?<mm>\d+)", @"(?-i)^\s*Height\D+(?<mm>\d+)"]);
        Assert.That(filters[0].IsMatch("LAYER_HEIGHT = 2"), Is.True);
        Assert.That(filters[1].IsMatch("Height: 2"), Is.True);
        Assert.That(filters[1].IsMatch("HEIGHT: 2"), Is.False, "PrusaSlicer's ;HEIGHT: must not match the Fusion 360 filter");
    }

    /// <summary>
    /// The config.json of the package holds the defaults of <see cref="Settings"/>, so that a new installation behaves like a restored config file.
    /// SbcRestartTimeout (Settings.cs:185-188, fork commit f5595e28) was missing there; DCS used the default then, so only the file differed
    /// </summary>
    [Test]
    public void PackagedConfigFileHoldsTheDefaults()
    {
        string packaged = Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "pkg", "common", "duetcontrolserver", "opt", "dsf", "conf", "config.json");
        Assert.That(System.IO.File.Exists(packaged), Is.True, $"Packaged config file not found at {Path.GetFullPath(packaged)}");

        JsonNode expected = JsonNode.Parse(JsonSerializer.Serialize(new Settings(), SettingsContext.Default.Settings))!;
        JsonNode actual = JsonNode.Parse(System.IO.File.ReadAllText(packaged))!;
        Assert.That(JsonNode.DeepEquals(actual, expected), Is.True, $"Differences: {string.Join(", ", Differences(actual.AsObject(), expected.AsObject()))}");
    }

    private static IEnumerable<string> Differences(JsonObject actual, JsonObject expected) =>
        actual.Select(kv => kv.Key).Union(expected.Select(kv => kv.Key)).Where(key => !JsonNode.DeepEquals(actual[key], expected[key]))
            .Select(key => $"{key}: packaged {actual[key]?.ToJsonString() ?? "missing"}, default {expected[key]?.ToJsonString() ?? "missing"}");

    /// <summary>
    /// Configuration keys are case-insensitive, and DCS reads the log level case-insensitively (Settings.cs:488). The key was taken out of the binding with
    /// a case-sensitive comparison though, so "logLevel" went to the binder as well, which cannot convert "info" to <see cref="LogLevel"/>, and DCS did not
    /// start: "Failed to initialize environment" (Program.cs:197, 200-202). Now it is compared case-insensitively (Settings.cs:497), like the other keys
    /// (Settings.cs:459, 493-494)
    /// </summary>
    [Test]
    public void LogLevelKeyIsCaseInsensitive()
    {
        Settings? settings = null;
        Assert.DoesNotThrow(() => settings = Bind("""{ "logLevel": "info" }""", NoConfigFile).Settings);
        Assert.That(settings?.LogLevel, Is.EqualTo(LogLevel.Information));
    }

    /// <summary>
    /// The wiki says DCS restores the factory defaults when config.json is deleted (User_manual/Machine_configuration/DSF_Other.md:204).
    /// The file was written from the settings after the command-line options are applied (Settings.cs:539-568), so the first start after the
    /// deletion decided what was saved. `DuetControlServer -u`, which the postinst of the reprapfirmware package runs
    /// (pkg/deb/reprapfirmware/DEBIAN/postinst:11), sets the log level to error (Program.cs:94-98), and every later start then logged only errors.
    /// Now the defaults of <see cref="Settings"/> are written (Settings.cs:431-435)
    /// </summary>
    [Test]
    public void RestoredConfigFileHoldsTheDefaults()
    {
        FileInfo configFile = new(Path.Combine(_directory, "config.json"));
        (Settings settings, _) = Bind(configFile: configFile, updateOnly: true, logLevel: LogLevel.Error, baseDirectory: new DirectoryInfo(Path.Combine(_directory, "sd")));

        JsonObject json = JsonNode.Parse(System.IO.File.ReadAllText(configFile.FullName))!.AsObject();
        Assert.Multiple(() =>
        {
            Assert.That((string?)json["LogLevel"], Is.EqualTo("info"));
            Assert.That((string?)json["BaseDirectory"], Is.EqualTo(new Settings().BaseDirectory));
            JsonNode expected = JsonNode.Parse(JsonSerializer.Serialize(new Settings(), SettingsContext.Default.Settings))!;
            Assert.That(JsonNode.DeepEquals(json, expected), Is.True, $"Differences: {string.Join(", ", Differences(json, expected.AsObject()))}");
        });

        // The options still apply to this start
        Assert.That(settings.LogLevel, Is.EqualTo(LogLevel.Error));
        Assert.That(settings.BaseDirectory, Is.EqualTo(Path.Combine(_directory, "sd")));
    }
}
