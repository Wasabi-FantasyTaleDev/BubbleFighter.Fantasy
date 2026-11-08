using System.IO.Pipes;

namespace FantasyTale.Gateway;

/// <summary>Runs a hidden, parent-owned Native AOT process using one local asynchronous control pipe.</summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var log = new GatewayLog(Console.Error);
        try
        {
            var maxControlMessageBytes = ParseControlLimit(args);
            if (args.Length < 2 || args[0] != "--pipe")
                throw new GatewayConfigurationException("Expected --pipe followed by a random local pipe identifier.");
            var name = args[1];
            if (name.Length is < 16 or > 128
                || name.Any(c => c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-')))
                throw new GatewayConfigurationException("Expected --pipe followed by a random local pipe identifier.");
            using var control = new NamedPipeClientStream(".", name, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await control.ConnectAsync(10000).ConfigureAwait(false);
            await GatewayHost.RunAsync(control, log, CancellationToken.None, maxControlMessageBytes).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error)
        {
            log.Write($"Fantasy Tale Gateway failed: {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    internal static int ParseControlLimit(string[] args)
    {
        if (args.Length == 2) return GatewayConstants.MaxControlMessageBytes;
        if (args is ["--pipe", _, "--control-max-bytes", var text]
            && int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var maximum)
            && maximum > 0 && maximum <= Array.MaxLength) return maximum;
        throw new GatewayConfigurationException("Expected --pipe <name> with optional --control-max-bytes <positive byte budget>.");
    }
}
