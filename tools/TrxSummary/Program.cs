namespace BubbleFighter.Fantasy.Tools.TrxSummary;

/// <summary>
/// Entry point of the TRX summary tool, which turns a list of Visual Studio TRX result files
/// into one human readable report on standard output.
/// </summary>
public static class Program
{
    /// <summary>
    /// Runs the tool with the process command line.
    /// </summary>
    /// <param name="args">One input list path and optional validated resource-limit switches.</param>
    /// <returns>The process exit code described by <see cref="TrxSummaryExitCode"/>.</returns>
    public static int Main(string[] args)
    {
        return TrxSummaryRunner.Run(args, Console.Out, Console.Error);
    }
}
