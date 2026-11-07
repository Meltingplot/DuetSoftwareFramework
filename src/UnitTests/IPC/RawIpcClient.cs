using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DuetAPI.Utility;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.IPC;

/// <summary>
/// Plain UNIX socket client of the DCS IPC server, for tests that send what DuetAPIClient never sends.
/// Messages are framed the same way as on both sides of the real connection, by <see cref="JsonHelper.ReceiveUtf8JsonAsync(Socket, CancellationToken)"/>
/// </summary>
public sealed class RawIpcClient : IDisposable
{
    /// <summary>
    /// Socket of the connection
    /// </summary>
    public Socket Socket { get; } = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

    /// <summary>
    /// Connection ID the server sent in its init message, or 0 if it was not read
    /// </summary>
    public int Id { get; private set; }

    /// <summary>
    /// Connect to the IPC server
    /// </summary>
    /// <param name="socketPath">Path of the UNIX socket</param>
    /// <param name="readServerInit">Whether to read the init message the server sends first (Server.ProcessConnectionAsync)</param>
    /// <returns>Connected client</returns>
    public static async Task<RawIpcClient> ConnectAsync(string socketPath, bool readServerInit = true)
    {
        RawIpcClient client = new();
        try
        {
            await client.Socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
            if (readServerInit)
            {
                using JsonDocument init = await client.ReceiveAsync() ?? throw new IOException("Server closed the connection instead of sending its init message");
                Assert.That(init.RootElement.GetProperty("version").GetInt32(), Is.EqualTo(DuetAPI.Connection.Defaults.ProtocolVersion));
                client.Id = init.RootElement.GetProperty("id").GetInt32();
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return client;
    }

    /// <summary>
    /// Connect to the IPC server and switch to the given mode
    /// </summary>
    /// <param name="socketPath">Path of the UNIX socket</param>
    /// <param name="initMessage">Client init message as JSON</param>
    /// <returns>Connected client</returns>
    /// <exception cref="IOException">Server refused the mode</exception>
    public static async Task<RawIpcClient> ConnectAsync(string socketPath, string initMessage)
    {
        RawIpcClient client = await ConnectAsync(socketPath);
        try
        {
            await client.SendAsync(initMessage);
            using JsonDocument response = await client.ReceiveAsync() ?? throw new IOException("Server closed the connection instead of answering the init message");
            if (!response.RootElement.GetProperty("success").GetBoolean())
            {
                throw new IOException($"Server refused {initMessage}: {response.RootElement}");
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return client;
    }

    /// <summary>
    /// Send UTF-8 text
    /// </summary>
    public Task SendAsync(string text) => SendAsync(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Send raw bytes
    /// </summary>
    public async Task SendAsync(byte[] data)
    {
        for (int sent = 0; sent < data.Length;)
        {
            sent += await Socket.SendAsync(data.AsMemory(sent), SocketFlags.None);
        }
    }

    /// <summary>
    /// Receive the next JSON object
    /// </summary>
    /// <param name="timeout">Timeout or null for the default timeout</param>
    /// <returns>Received object or null if the server closed the connection</returns>
    /// <exception cref="TimeoutException">Nothing arrived in time</exception>
    public async Task<JsonDocument?> ReceiveAsync(TimeSpan? timeout = null)
    {
        using CancellationTokenSource cts = new(timeout ?? FakeFirmwareHost.DefaultTimeout);
        try
        {
            await using MemoryStream json = await JsonHelper.ReceiveUtf8JsonAsync(Socket, cts.Token);
            return JsonDocument.Parse(json);
        }
        catch (SocketException)
        {
            return null;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException("No message from the IPC server in time");
        }
    }

    /// <summary>
    /// Receive a JSON object that must be a response
    /// </summary>
    /// <param name="timeout">Timeout or null for the default timeout</param>
    /// <returns>Received response</returns>
    public async Task<JsonDocument> ReceiveResponseAsync(TimeSpan? timeout = null)
    {
        JsonDocument response = await ReceiveAsync(timeout) ?? throw new IOException("Server closed the connection instead of sending a response");
        AssertResponse(response.RootElement);
        return response;
    }

    /// <summary>
    /// Send a message and receive the response
    /// </summary>
    /// <param name="json">Message to send</param>
    /// <param name="timeout">Timeout or null for the default timeout</param>
    /// <returns>Received response</returns>
    public async Task<JsonDocument> RequestAsync(string json, TimeSpan? timeout = null)
    {
        await SendAsync(json);
        return await ReceiveResponseAsync(timeout);
    }

    /// <summary>
    /// Receive JSON objects until the server closes the connection
    /// </summary>
    /// <param name="timeout">Time to wait for the server to close the connection</param>
    /// <returns>Received objects and whether the server closed the connection in time</returns>
    public async Task<(List<JsonDocument> messages, bool closed)> ReceiveUntilClosedAsync(TimeSpan timeout)
    {
        List<JsonDocument> messages = [];
        using CancellationTokenSource cts = new(timeout);
        try
        {
            while (true)
            {
                await using MemoryStream json = await JsonHelper.ReceiveUtf8JsonAsync(Socket, cts.Token);
                try
                {
                    messages.Add(JsonDocument.Parse(json));
                }
                catch (JsonException e)
                {
                    throw new AssertionException($"IPC server sent malformed JSON: {Encoding.UTF8.GetString(json.ToArray())} ({e.Message})");
                }
            }
        }
        catch (SocketException)
        {
            return (messages, true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return (messages, false);
        }
    }

    /// <summary>
    /// Check that a JSON object has the layout of a response (DuetAPI/Commands/Responses.cs)
    /// </summary>
    /// <param name="response">Response to check</param>
    /// <returns>True if it is a success response</returns>
    public static bool AssertResponse(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("success", out JsonElement success) || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new AssertionException($"Not a response: {response}");
        }
        if (success.ValueKind == JsonValueKind.False &&
            (!response.TryGetProperty("errorType", out JsonElement errorType) || errorType.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(errorType.GetString()) ||
             !response.TryGetProperty("errorMessage", out JsonElement errorMessage) || errorMessage.ValueKind != JsonValueKind.String))
        {
            throw new AssertionException($"Error response without errorType and errorMessage: {response}");
        }
        return success.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// Get the error type of a response or null if it is a success response
    /// </summary>
    public static string? GetErrorType(JsonElement response) => AssertResponse(response) ? null : response.GetProperty("errorType").GetString();

    /// <summary>
    /// Close the connection
    /// </summary>
    public void Dispose() => Socket.Dispose();
}
