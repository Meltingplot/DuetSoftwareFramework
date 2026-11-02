namespace DuetSharedLibrary;

/// <summary>
/// Input pin that reports its level and its edges
/// </summary>
public interface IInputGpioPin
{
    /// <summary>
    /// Number of edges that could not be reported
    /// </summary>
    int MissedEdges { get; }

    /// <summary>
    /// Current level of the pin
    /// </summary>
    bool Value { get; }

    /// <summary>
    /// Consume every edge that has occurred so far without blocking
    /// </summary>
    void FlushEvents();

    /// <summary>
    /// Block until the next edge occurs
    /// </summary>
    /// <param name="timeout">Timeout in ms</param>
    /// <returns>Pin level after the edge</returns>
    /// <exception cref="System.OperationCanceledException">Timeout occurred</exception>
    bool WaitForEvent(int timeout);
}
