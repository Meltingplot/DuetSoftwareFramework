using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer;
using DuetControlServer.Codes;
using DuetControlServer.Commands;
using DuetControlServer.Files;
using DuetControlServer.Link;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Model;
using DuetControlServer.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Code = DuetControlServer.Commands.Code;

namespace UnitTests.Codes;

/// <summary>
/// Tests for the Start stage holding back codes behind unbuffered codes such as M122 and M409
/// </summary>
[TestFixture]
public class UnbufferedCodes
{
    private static readonly TimeSpan CodeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(200);

    private ServiceProvider _provider = null!;
    private CodeProcessor _codeProcessor = null!;
    private string _macroFile = null!;

    [SetUp]
    public void SetUp()
    {
        _macroFile = Path.GetTempFileName();

        ServiceCollection services = new();
        services
            .AddLogging()
            .AddSingleton(Options.Create(new Settings()))
            .AddSingleton<IHostApplicationLifetime, ApplicationLifetime>()
            .AddSingleton(DispatchProxy.Create<ILinkAdapter, SPI.ChannelProcessor.NullLinkAdapter>())
            .AddSingleton<LinkInterface>()
            .AddCodes()
            .AddCommands()
            .AddFiles()
            .AddLink()
            .AddModel()
            .AddUtility();
        _provider = services.BuildServiceProvider();
        _codeProcessor = _provider.GetRequiredService<CodeProcessor>();
    }

    [TearDown]
    public void TearDown()
    {
        // Stop the pipeline tasks, some of them are still waiting for codes that never finish
        ((ApplicationLifetime)_provider.GetRequiredService<IHostApplicationLifetime>()).StopApplication();
        _provider.Dispose();
        System.IO.File.Delete(_macroFile);
    }

    /// <summary>
    /// Start an M122 that is sent to the firmware and never gets a reply, so it keeps holding back other codes
    /// </summary>
    private Code StartBlockingM122(CodeFile? file = null)
    {
        Code code = _provider.GetRequiredService<CodeFactory>().Create();
        code.Channel = CodeChannel.HTTP;
        code.Type = CodeType.MCode;
        code.MajorNumber = 122;
        AssignFile(code, file);
        _ = code.ExecuteAsync();

        // The Start stage counts the code before it passes it on, so wait for it to get past that stage
        Assert.That(SpinWait.SpinUntil(() => code.Stage == PipelineStage.Post, CodeTimeout), Is.True, "M122 did not reach the firmware stage");
        Assert.That(code.Flags.HasFlag(CodeFlags.Unbuffered), Is.True);
        return code;
    }

    /// <summary>
    /// Start a comment that DSF resolves itself, so it finishes as soon as the Start stage lets it through
    /// </summary>
    private Task<Message?> StartComment(CodeFile? file = null, CodeFlags flags = CodeFlags.None)
    {
        Code code = _provider.GetRequiredService<CodeFactory>().Create();
        code.Channel = CodeChannel.HTTP;
        code.Type = CodeType.Comment;
        code.Comment = "test";
        code.Flags = flags;
        AssignFile(code, file);
        return code.ExecuteAsync();
    }

    private static void AssignFile(Code code, CodeFile? file)
    {
        if (file is not null)
        {
            code.File = file;
            code.Flags |= CodeFlags.IsFromMacro;
        }
    }

    /// <summary>
    /// Push a macro level on the code pipeline like the SPI processor does when the firmware starts a macro
    /// </summary>
    private MacroFile PushMacro()
    {
        MacroFile macro = _provider.GetRequiredService<FileFactory>().CreateMacro("0:/macros/test", _macroFile, CodeChannel.HTTP, null)!;
        _codeProcessor.Push(CodeChannel.HTTP, macro);
        return macro;
    }

    [Test]
    public void MacroCodeAfterUnbufferedClientCode()
    {
        // A client sends M122 while a macro runs, then that macro starts a nested macro (lab reproducer with M409, 2026-10-29).
        // The client code is executed only after the macros, so the nested macro must not wait for it
        StartBlockingM122();
        MacroFile macro = PushMacro();

        Assert.That(StartComment(macro).Wait(CodeTimeout), Is.True, "Macro code is held back by the unbuffered client code");
    }

    [Test]
    public void SameLevelCodeAfterUnbufferedCode()
    {
        Code m122 = StartBlockingM122();
        Task<Message?> comment = StartComment();

        Assert.That(comment.Wait(HoldTime), Is.False, "Code on the same level must wait for the unbuffered code");

        _codeProcessor.CancelCode(m122);
        Assert.That(comment.Wait(CodeTimeout), Is.True);
    }

    [Test]
    public void CallingLevelCodeAfterUnbufferedMacroCode()
    {
        // Unbuffered codes hold back codes on their own stack level only (upstream 30ea08be). A code on the calling level
        // passes the Start stage, but if it goes to the firmware it is still sent only after the macro
        MacroFile macro = PushMacro();
        StartBlockingM122(macro);

        Assert.That(StartComment().Wait(CodeTimeout), Is.True, "Code on the calling level is held back by the unbuffered macro code");
    }

    [Test]
    public void PriorityCodeAfterUnbufferedCode()
    {
        // Codes from the firmware are not moved to another channel, so this checks the bypass on the same stack level
        StartBlockingM122();

        Assert.That(StartComment(flags: CodeFlags.IsPrioritized | CodeFlags.IsFromFirmware).Wait(CodeTimeout), Is.True, "Priority code is held back by the unbuffered code");
    }

    [Test]
    public void MessageBoxLevelCodeAfterUnbufferedCode()
    {
        // While a blocking message box is open, codes from clients go to a level without a file on top of the stack
        StartBlockingM122();
        _codeProcessor.Push(CodeChannel.HTTP, null);

        Assert.That(StartComment().Wait(CodeTimeout), Is.True, "Code on the message box level is held back by the unbuffered code");
    }
}
