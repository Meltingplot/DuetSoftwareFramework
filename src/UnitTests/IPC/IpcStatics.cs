using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DuetAPI.Connection;
using DuetControlServer.IPC;
using DuetControlServer.IPC.Processors;
using DuetControlServer.Model;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using UnitTests.FakeFirmware;

namespace UnitTests.IPC;

/// <summary>
/// Access to the static registries of the IPC processors. They are shared by all hosts in the test process, so a test that
/// leaves an entry behind changes the behaviour of later tests
/// </summary>
public static class IpcStatics
{
    /// <summary>
    /// Registered code streams (private static field CodeStream._streams)
    /// </summary>
    private static IList CodeStreams => (IList)typeof(CodeStream).GetField("_streams", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    /// <summary>
    /// Number of registered code streams
    /// </summary>
    public static int CodeStreamCount
    {
        get
        {
            IList streams = CodeStreams;
            lock (streams)
            {
                return streams.Count;
            }
        }
    }

    /// <summary>
    /// Registered interceptors per mode (private static field CodeInterception._connections)
    /// </summary>
    private static Dictionary<InterceptionMode, List<CodeInterception>> Interceptors =>
        (Dictionary<InterceptionMode, List<CodeInterception>>)typeof(CodeInterception).GetField("_connections", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    /// <summary>
    /// Get the registered interceptors of a mode
    /// </summary>
    public static List<CodeInterception> GetInterceptors(InterceptionMode mode)
    {
        List<CodeInterception> interceptors = Interceptors[mode];
        lock (interceptors)
        {
            return [.. interceptors];
        }
    }

    /// <summary>
    /// Whether an interceptor of the given connection ID is registered
    /// </summary>
    public static bool IsInterceptorRegistered(int connectionId) =>
        Enum.GetValues<InterceptionMode>().Any(mode => GetInterceptors(mode).Any(interceptor => interceptor.Connection.Id == connectionId));

    /// <summary>
    /// Registered subscriptions (private static field ModelSubscription._subscriptions)
    /// </summary>
    public static List<ModelSubscription> GetSubscriptions()
    {
        List<ModelSubscription> subscriptions = (List<ModelSubscription>)typeof(ModelSubscription).GetField("_subscriptions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        lock (subscriptions)
        {
            return [.. subscriptions];
        }
    }

    /// <summary>
    /// Whether a subscription of the given connection ID is registered
    /// </summary>
    public static bool IsSubscriptionRegistered(int connectionId) => GetSubscriptions().Any(subscription => subscription.Connection.Id == connectionId);

    /// <summary>
    /// Number of handlers of Observer.OnPropertyPathChanged of a host. Every subscription in Patch mode adds one (ModelSubscription.ProcessAsync)
    /// </summary>
    public static int GetObserverHandlerCount(FakeFirmwareHost host)
    {
        Observer observer = host.Services.GetRequiredService<Observer>();
        Delegate? handlers = (Delegate?)typeof(Observer).GetField(nameof(Observer.OnPropertyPathChanged), BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(observer);
        return handlers?.GetInvocationList().Length ?? 0;
    }

    /// <summary>
    /// Number of commands that wait for the plugin service (private static fields PluginService._pendingCommands and _pendingRootCommands)
    /// </summary>
    public static int GetPendingPluginServiceCommands(bool asRoot)
    {
        ICollection pending = (ICollection)typeof(PluginService).GetField(asRoot ? "_pendingRootCommands" : "_pendingCommands", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        return pending.Count;
    }

    /// <summary>
    /// Find the DCS side of a connection that is registered as interceptor, subscription or code stream
    /// </summary>
    /// <param name="connectionId">Connection ID from the server init message</param>
    /// <returns>Connection or null if no processor of that connection is registered</returns>
    public static Connection? FindConnection(int connectionId)
    {
        foreach (InterceptionMode mode in Enum.GetValues<InterceptionMode>())
        {
            foreach (CodeInterception interceptor in GetInterceptors(mode))
            {
                if (interceptor.Connection.Id == connectionId)
                {
                    return interceptor.Connection;
                }
            }
        }
        foreach (ModelSubscription subscription in GetSubscriptions())
        {
            if (subscription.Connection.Id == connectionId)
            {
                return subscription.Connection;
            }
        }
        IList streams = CodeStreams;
        lock (streams)
        {
            foreach (CodeStream stream in streams)
            {
                if (stream.Connection.Id == connectionId)
                {
                    return stream.Connection;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Poll a condition until it is met
    /// </summary>
    /// <param name="condition">Condition to wait for</param>
    /// <param name="because">Description of the condition for the failure message</param>
    /// <param name="poke">Optional action to run between the polls, e.g. to cause an object model update</param>
    /// <param name="timeout">Timeout or null for the default timeout</param>
    public static async Task WaitUntilAsync(Func<bool> condition, string because, Action? poke = null, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? FakeFirmwareHost.DefaultTimeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new AssertionException($"Timed out waiting until {because}");
            }
            poke?.Invoke();
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Remove the code streams whose connection is gone. The CodeStream constructor registers the stream before it checks the buffer
    /// size and only ProcessAsync removes it again, so a refused CodeStream connection stays registered (see
    /// IpcCodeStream.RefusedStreamIsNotRegistered). Tests that are refused on purpose clean up with this, so that the leaked entry
    /// does not change the behaviour of later tests (CodeStream.HasClientsWaitingForMessages makes LockWrapper clear the messages)
    /// </summary>
    /// <returns>Number of removed entries</returns>
    public static int RemoveLeakedCodeStreams()
    {
        IList streams = CodeStreams;
        int removed = 0;
        lock (streams)
        {
            for (int i = streams.Count - 1; i >= 0; i--)
            {
                if (!((CodeStream)streams[i]!).Connection.IsConnected)
                {
                    streams.RemoveAt(i);
                    removed++;
                }
            }
        }
        return removed;
    }
}
