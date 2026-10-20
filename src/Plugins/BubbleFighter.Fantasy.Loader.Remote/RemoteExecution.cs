using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Loader.Remote;

/// <summary>Acquires one execution per activation of a remote definition; all rows of a process share its process.</summary>
internal sealed class RemoteExecutionFactory(RemoteSource source, RemoteDefinition definition,
    IReadOnlyDictionary<string, string> contracts, int maxSessions, TimeSpan requestTimeout) : IPluginExecutionFactory
{
    /// <summary>Gets no shared key: the process serves concurrent activations, and a restart is not this loader's job.</summary>
    public string? CoordinationKey => null;

    /// <inheritdoc />
    public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source.IsFaulted)
            throw new RemoteProcessException($"SDK plugin process for '{definition.Id}' is not running: {source.Faulted.Result.Message}", source.Faulted.Result);
        // reload asks to refresh executable code; the child process is the code, and loader.process never restarts it.
        return ValueTask.FromResult<IPluginExecution>(new RemoteExecution(source, definition, contracts, maxSessions, requestTimeout, reload));
    }
}

/// <summary>Runs one row against a remote process: validates config, sends activate and mirrors deactivation into the row's effect order.</summary>
internal sealed class RemoteExecution(RemoteSource source, RemoteDefinition definition,
    IReadOnlyDictionary<string, string> contracts, int maxSessions, TimeSpan requestTimeout, bool reload) : IPluginExecution
{
    private RemoteActivation? _activation;
    private bool _activated;

    /// <inheritdoc />
    public PluginAttribute Declaration => definition.Declaration;

    /// <inheritdoc />
    public string Version => definition.Version;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> ContractVersions => contracts;

    /// <inheritdoc />
    public Type? ImplementationType => null;

    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var config = definition.BindConfig(context.Row);
        var activation = new RemoteActivation(source, definition, context, maxSessions, context.Row.Lifecycle.EffectCapacity, requestTimeout);
        _activation = activation;
        source.Add(activation);
        // First registered, last released: the activation stays addressable until every other effect is gone.
        context.Effect(() => source.Remove(activation));
        context.Effect(context.Events.On<ConnectionClosedEvent>(activation.OnConnectionClosed));
        activation.GoLive();
        await source.ActivateAsync(activation, new
        {
            activation = activation.Id,
            row_id = context.Row.Id,
            plugin = definition.Id,
            config,
            reload,
            max_provider_callbacks = context.Row.Lifecycle.EffectCapacity,
        }, cancellationToken).ConfigureAwait(false);
        // Last registered, first released: the plugin runs its own disposers while its handles still exist,
        // then the row releases the remaining handles itself in reverse order.
        context.Effect(() => activation.DeactivateAsync(bestEffort: false).GetAwaiter().GetResult());
        _activated = true;
        await context.RunTaskAsync($"remote-fault-watch:{context.Row.Id}", async token =>
        {
            var cause = await activation.WaitForFaultAsync(token).ConfigureAwait(false);
            context.Logger.LogError("SDK plugin '{Plugin}' faulted: {Message}", definition.Id, cause.Message);
            throw new RemoteProcessException($"SDK plugin '{definition.Id}' faulted: {cause.Message}", cause);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_activation is not { } activation)
            return;
        // A failed activation never reached the deactivate effect; give the plugin the chance to run what it registered.
        await activation.DeactivateAsync(bestEffort: !_activated).ConfigureAwait(false);
        source.Remove(activation);
    }
}
