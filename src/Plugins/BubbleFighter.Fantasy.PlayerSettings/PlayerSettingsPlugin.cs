using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Settings;

namespace BubbleFighter.Fantasy.PlayerSettings;

/// <summary>Persists typed key-map updates under the authenticated identity and restores them at the shared login boundary.</summary>
[Plugin("player.settings", Description = "Authenticated customized-key storage and login restoration.",
    Requires = [typeof(IMessageRouter), typeof(IPlayerSettingsRepository)],
    RequiresMany = [typeof(IInboundMessageBinding<CustomizedKeysUpdated>), typeof(IMessageSender<CustomizedKeysRestored>)])]
public sealed class PlayerSettingsPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = context.Config.Bind<PlayerSettingsOptions>();
        options.Validate();
        var store = context.Services.Get<IPlayerSettingsRepository>();
        store.Limits.Validate();
        var bindings = context.Services.GetAll<IInboundMessageBinding<CustomizedKeysUpdated>>()
            .Count(binding => binding.Protocol == options.Protocol);
        var senders = context.Services.GetAll<IMessageSender<CustomizedKeysRestored>>()
            .Where(sender => sender.Protocol == options.Protocol).ToArray();
        if (bindings != 1 || senders.Length != 1)
            throw new ConfigException($"player.settings requires one key-map binding and sender for '{options.Protocol}'.");
        var sender = senders[0];
        context.Effect(context.Services.Get<IMessageRouter>().For(context).On<CustomizedKeysUpdated>(options.Protocol,
            async (session, update, ct) =>
            {
                var userNum = session.Feature<IPlayerSession>().Identity.UserNum;
                var copy = store.Limits.Copy(update.Mappings);
                if (update.UseDefaults != (copy.Length == 0))
                    throw new InvalidDataException("The customized-key defaults flag does not match its override list.");
                await store.ReplaceKeysAsync(userNum, options.Scope, copy, ct).ConfigureAwait(false);
                // kr-1256 sub_60B150 sends a complete update; no immediate reply is established by that path.
            }));
        context.Effect(context.Events.OnAsync<LoginMenuInstalled>(async (installed, ct) =>
        {
            if (installed.Protocol != options.Protocol) return;
            var identity = installed.Session.Feature<IPlayerSession>().Identity;
            if (identity.UserNum != installed.Login.UserNumber)
                throw new InvalidOperationException("Key-map restoration does not match the authenticated player.");
            // The login sender publishes this boundary before Lobby advances Authenticated to MainMenu.
            var mappings = await store.ReadKeysAsync(identity.UserNum, options.Scope, ct).ConfigureAwait(false);
            var copy = store.Limits.Copy(mappings);
            await sender.SendAsync(installed.Session, new(copy.Length != 0, copy), ct).ConfigureAwait(false);
        }));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Selects one connection scope and its independently persisted native control-name namespace.</summary>
public sealed class PlayerSettingsOptions
{
    /// <summary>Gets or sets the game protocol whose typed key-map requests this row handles.</summary>
    [Required]
    public string Protocol { get; set; } = "";

    /// <summary>Gets or sets the storage scope; use separate values until control-name compatibility is established.</summary>
    [Required]
    public string Scope { get; set; } = "";

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Protocol)) throw new ConfigException("player.settings requires a protocol.");
        CustomizedKeyLimits.ValidateIdentity(1, Scope);
    }
}
