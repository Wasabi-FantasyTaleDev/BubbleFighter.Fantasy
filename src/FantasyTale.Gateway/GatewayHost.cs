using System.Text.Json;

namespace FantasyTale.Gateway;

/// <summary>Composes static plugin rows and supervises their owned tasks through the parent pipe.</summary>
internal static class GatewayHost
{
    internal static async Task RunAsync(Stream control, IGatewayLog log, CancellationToken cancellationToken,
        int maxControlMessageBytes = GatewayConstants.MaxControlMessageBytes)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(TimeSpan.FromSeconds(10));
        var initial = await ControlMessageStream.ReadAsync(control, startup.Token, maxMessageBytes: maxControlMessageBytes).ConfigureAwait(false)
            ?? throw new EndOfStreamException("Parent closed before launch configuration.");
        var config = JsonSerializer.Deserialize(initial, GatewayJsonContext.Strict.GatewayConfiguration)
            ?? throw new GatewayConfigurationException("Missing launch configuration.");
        config.Validate();
        var plugins = config.Rows.Select(row => GatewayPluginCatalog.Create(row.Plugin)).ToArray();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var contexts = new List<GatewayContext>(config.Rows.Length);
        var activations = new List<GatewayActivation>(config.Rows.Length);
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new List<Task>(config.Rows.Length);
        Task? parent = null;
        try
        {
            for (var i = 0; i < config.Rows.Length; i++)
            {
                var context = new GatewayContext(log, lifetime.Token);
                contexts.Add(context);
                var activation = await plugins[i].ActivateAsync(config.Rows[i], context).ConfigureAwait(false);
                activations.Add(activation);
                work.Add(ObserveAsync(activation.Completion));
            }
            await ControlMessageStream.WriteJsonAsync(control,
                new GatewayReady { Rows = activations.Select(item => item.Endpoint).ToArray() },
                GatewayJsonContext.Strict.GatewayReady, startup.Token, maxControlMessageBytes).ConfigureAwait(false);
            log.Write("Fantasy Tale Gateway ready.");
            var handlers = contexts.Select(context => context.Find<IGatewayControlHandler>()).ToArray();
            parent = ReadCommandsAsync(control, config, handlers, lifetime.Token, maxControlMessageBytes);
            var complete = Task.WhenAll(work);
            var winner = await Task.WhenAny(parent, complete, failure.Task).ConfigureAwait(false);
            await winner.ConfigureAwait(false);
        }
        finally
        {
            lifetime.Cancel();
            for (var i = contexts.Count - 1; i >= 0; i--)
                contexts[i].Dispose();
            if (parent is not null) work.Add(parent);
            try { await Task.WhenAll(work).ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            log.Write("Fantasy Tale Gateway stopped.");
        }

        async Task ObserveAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch
            {
                failure.TrySetResult();
                throw;
            }
        }
    }

    private static async Task ReadCommandsAsync(Stream control, GatewayConfiguration config, IGatewayControlHandler?[] handlers,
        CancellationToken lifetime, int maxControlMessageBytes)
    {
        while (true)
        {
            var body = await ControlMessageStream.ReadAsync(control, lifetime, config.ControlFrameTimeoutMs, maxControlMessageBytes).ConfigureAwait(false);
            if (body is null) return;
            var command = JsonSerializer.Deserialize(body, GatewayJsonContext.Strict.GatewayCommand)
                ?? throw new GatewayConfigurationException("Missing gateway control command.");
            if (command.Type == "stop" && command.RowId is null && command.LaunchId is null && command.GrantId is null
                && command.Revision is null && command.Candidates is null) return;
            if (command.Type != "refresh-automatic")
                throw new GatewayConfigurationException("Unknown gateway control command.");
            if (!GatewayConfiguration.ValidIdentifier(command.RowId))
                throw new GatewayConfigurationException("Candidate refresh requires a valid configured row identifier.");
            var index = Array.FindIndex(config.Rows, row => row.Id == command.RowId);
            var handler = index >= 0 ? handlers[index] : null;
            var result = handler?.Handle(command) ?? new GatewayCommandResult
            {
                RowId = command.RowId, Revision = command.Revision, Status = "rejected", Reason = "unsupported-row",
            };
            using var write = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            write.CancelAfter(config.ControlFrameTimeoutMs);
            await ControlMessageStream.WriteJsonAsync(control, result, GatewayJsonContext.Strict.GatewayCommandResult, write.Token, maxControlMessageBytes).ConfigureAwait(false);
        }
    }
}
