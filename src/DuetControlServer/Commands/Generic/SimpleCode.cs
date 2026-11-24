using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.ObjectModel;
using DuetControlServer.IPC;
using DuetControlServer.Link;
using DuetControlServer.Link.Protocol.Shared;
using DuetSharedLibrary;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DuetControlServer.Commands;

/// <summary>
/// Implementation of the <see cref="DuetAPI.Commands.SimpleCode"/> command
/// </summary>
/// <param name="codeFactory">Code factory</param>
/// <param name="model">Object model</param>
/// <param name="lifetime">Host application lifetime</param>
/// <param name="linkInterface">Link interface</param>
public sealed class SimpleCode(Codes.CodeFactory codeFactory, Model.ObjectModel model, IHostApplicationLifetime lifetime, LinkInterface linkInterface)
    : DuetAPI.Commands.SimpleCode, IConnectionCommand
{
    /// <summary>
    /// Locks to avoid race conditions when executing multiple text-based codes via the same channel
    /// </summary>
    private static readonly AsyncLock[] _channelLocks = new AsyncLock[Inputs.Total];

    /// <summary>
    /// Static constructor of this class
    /// </summary>
    static SimpleCode()
    {
        for (int i = 0; i < Inputs.Total; i++)
        {
            _channelLocks[i] = new AsyncLock();
        }
    }

    /// <inheritdoc />
    public Connection? Connection { get; set; }

    /// <summary>
    /// Indicates if the code comes from the firmware. Only used internally
    /// </summary>
    [JsonIgnore]
    public bool IsFromFirmware { get; set; }

    /// <summary>
    /// Tag of the channel epoch of the DoCode request, see <see cref="Code.FirmwareTag"/>. Only used internally
    /// </summary>
    [JsonIgnore]
    internal ushort FirmwareTag { get; set; }

    /// <summary>
    /// Parse codes from the given input string asynchronously
    /// </summary>
    /// <returns>Parsed G/M/T-codes</returns>
    public async IAsyncEnumerable<Code> ParseAsync()
    {
        await using MemoryStream stream = new(Encoding.UTF8.GetBytes(Code));
        CodeParserBuffer buffer = new((int)stream.Length, false);

        while (buffer.GetPosition(stream) < stream.Length)
        {
            Code code = codeFactory.Create();
            code.Channel = Channel;
            code.Connection = Connection;
            code.SourceConnection = Connection?.Id ?? 0;

            if (await DuetAPI.Commands.Code.ParseAsync(stream, code, buffer))
            {
                yield return code;
            }
        }
    }

    /// <summary>
    /// Converts simple G/M/T-codes to a regular Code instances, executes them and returns the result as text
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Code result as text</returns>
    /// <exception cref="OperationCanceledException">Code has been cancelled</exception>
    public override async Task<string> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        // Check if the corresponding code channel has been disabled
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            if (model.Inputs[Channel] is null)
            {
                throw new InvalidOperationException("Requested code channel has been disabled");
            }
        }

        // Parse the input string
        Message result = new();
        List<Code> codes = [], priorityCodes = [];
        try
        {
            await foreach (Code code in ParseAsync())
            {
                // Check for async execution
                if (ExecuteAsynchronously)
                {
                    code.Flags |= CodeFlags.Asynchronous;
                }

                // Codes from the firmware bypass the interception logic
                if (IsFromFirmware)
                {
                    code.Flags |= CodeFlags.IsPrioritized | CodeFlags.IsFromFirmware;
                    code.FirmwareTag = FirmwareTag;
                    priorityCodes.Add(code);
                }
                // M108, M112, M122, M292, M25.1, and M999 (B0) always go to an idle channel so we (hopefully) get a low-latency response.
                // So does M24 while a feed hold is active, because the channel it arrives on may be the one whose macro the hold has stopped
                else if (code.Type == CodeType.MCode &&
                    (code.MajorNumber is 108 or 112 or 122 or 292 || (code.MajorNumber == 25 && code.MinorNumber == 1) || (code.MajorNumber == 999 && code.GetInt('B', 0) == 0) || (code.MajorNumber == 24 && await IsFeedHoldActiveAsync(cancellationToken))))
                {
                    code.Flags |= CodeFlags.IsPrioritized;
                    priorityCodes.Add(code);
                }
                else if (IPC.Processors.CodeInterception.IsInterceptingConnection(Connection))
                {
                    // Need to bypass the code order lock for codes being inserted...
                    priorityCodes.Add(code);
                }
                else
                {
                    codes.Add(code);
                }
            }
        }
        catch (CodeParserException cpe)
        {
            result.Append(MessageType.Error, cpe.Message);
            using (await model.AccessReadOnlyAsync(cancellationToken))
            {
                // Repetier or other host servers expect an "ok" after error messages
                if (model.Inputs[Channel]?.Compatibility is Compatibility.Marlin or Compatibility.NanoDLP)
                {
                    result.AppendLine("ok");
                }
            }

            // No code reaches the Executed stage, which sends the result of a code from the firmware
            if (IsFromFirmware)
            {
                SendErrorToFirmware(result);
            }
            return result.ToString();
        }

        try
        {
            // Execute priority codes first
            foreach (Code priorityCode in priorityCodes)
            {
                Message? codeResult = await priorityCode.ExecuteAsync();
                try
                {
                    if (codeResult is not null && !string.IsNullOrEmpty(codeResult.Content))
                    {
                        result.AppendLine(codeResult.ToString());
                    }
                }
                catch (OperationCanceledException)
                {
                    // not logged
                }
            }

            // Codes from the firmware are asynchronous, and the Executed stage sends their results. A code that fails gets no result there,
            // so wait for them and send the error below. A cancelled code is not answered
            if (IsFromFirmware)
            {
                foreach (Code priorityCode in priorityCodes)
                {
                    try
                    {
                        await priorityCode.Task;
                    }
                    catch (OperationCanceledException)
                    {
                        // not answered
                    }
                }
            }

            // Execute normal codes next. Use a lock here because multiple codes may be queued for the same channel
            if (codes.Count > 0)
            {
                Task<Message?>[] codeTasks = new Task<Message?>[codes.Count];
                using (await _channelLocks[(int)Channel].LockAsync(lifetime.ApplicationStopping))
                {
                    for (int i = 0; i < codes.Count; i++)
                    {
                        codeTasks[i] = codes[i].ExecuteAsync();
                    }
                }

                foreach (Task<Message?> codeTask in codeTasks)
                {
                    try
                    {
                        Message? codeResult = await codeTask;
                        if (codeResult is not null)
                        {
                            result.Append(codeResult);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // not logged
                    }
                }
            }
        }
        catch (Exception e) when (e is CodeParserException or ArgumentException || (IsFromFirmware && e is not OperationCanceledException))
        {
            result.Append(MessageType.Error, e.Message);
            using (await model.AccessReadOnlyAsync(lifetime.ApplicationStopping))
            {
                // Repetier or other host servers expect an "ok" after error messages
                if (model.Inputs[Channel]?.Compatibility is Compatibility.Marlin or Compatibility.NanoDLP)
                {
                    result.AppendLine("ok");
                }
            }

            // The failed code has not reached the Executed stage with a result
            if (IsFromFirmware)
            {
                SendErrorToFirmware(result);
            }
        }
        return result.ToString();
    }

    /// <summary>
    /// Send an error as the result of the DoCode request of a code from the firmware, as the Executed stage sends the result of a code that has one.
    /// Else RRF's channel waits for it forever
    /// </summary>
    /// <param name="result">Error message</param>
    private void SendErrorToFirmware(Message result)
    {
        result.AppendLine(string.Empty);
        linkInterface.SendMessage((MessageTypeFlags)(1 << (int)Channel) | MessageTypeFlags.ErrorMessageFlag, result.ToString(), FirmwareTag);
    }

    /// <summary>
    /// Check if a feed hold is active
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Whether the machine status is held</returns>
    private async Task<bool> IsFeedHoldActiveAsync(CancellationToken cancellationToken = default)
    {
        using (await model.AccessReadOnlyAsync(cancellationToken))
        {
            return model.State.Status == MachineStatus.Held;
        }
    }
}
