using System.Collections;
using System.Reflection;
using DuetControlServer.IPC.Processors;

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
