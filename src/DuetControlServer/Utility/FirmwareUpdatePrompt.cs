using System;

namespace DuetControlServer.Utility;

/// <summary>
/// Y/n questions the firmware updater asks on the console before it updates outdated boards
/// </summary>
internal class FirmwareUpdatePrompt
{
    /// <summary>
    /// Whether the questions can be answered. DCS -u started by the reprapfirmware package has no console input, then every outdated board is updated
    /// </summary>
    public virtual bool IsInteractive => !Console.IsInputRedirected;

    /// <summary>
    /// Discard the keys that were pressed before the questions
    /// </summary>
    public virtual void DiscardPendingKeys()
    {
        while (Console.KeyAvailable)
        {
            Console.ReadKey(true);
        }
    }

    /// <summary>
    /// Wait for the next key and echo it
    /// </summary>
    /// <returns>Character of the key</returns>
    public virtual char ReadKey() => Console.ReadKey().KeyChar;

    /// <summary>
    /// Ask a question
    /// </summary>
    /// <param name="question">Question ending with (Y/n)</param>
    /// <returns>Whether it was answered with Y or Enter</returns>
    public bool Ask(string question)
    {
        Console.Write(question);

        char key = char.ToUpper(ReadKey());
        if (key != '\r')
        {
            Console.WriteLine();
        }
        return key == '\r' || key == 'Y';
    }
}
