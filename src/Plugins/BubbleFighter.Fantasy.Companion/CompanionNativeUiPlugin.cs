using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Administration;
using BubbleFighter.Fantasy.Contracts.Companion;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Players;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Companion;

/// <summary>
/// The game's own interface on request, over Nirinsou Link. It offers <c>ui.native.invoke</c>, which the launcher shows
/// with the client's native control (overlay fallback when allowed), routes the launcher's <c>ui.native.result</c> back
/// to the waiting caller, provides <see cref="IClientNativeUi"/> to server plugins and adds the <c>clientui</c>
/// developer command.
/// </summary>
[Plugin("companion.native_ui", Category = "launcher", Names = ["en=Nirinsou Link game dialogs", "zh-Hans=Nirinsou Link 游戏对话框", "zh-Hant=Nirinsou Link 遊戲對話框", "ko=Nirinsou Link 게임 대화 상자"], Description = "The game's own message boxes, notifications, banners and local chat on request, over Nirinsou Link.",
    Requires = [typeof(ICompanionHub), typeof(ICommandRegistry), typeof(IPermissionRegistry), typeof(IGameText)],
    Optional = [typeof(IOnlinePlayers)], Provides = [typeof(IClientNativeUi)])]
public sealed class CompanionNativeUiPlugin : IPlugin
{
    internal const string InvokeType = "ui.native.invoke";
    internal const string ResultType = "ui.native.result";

    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<CompanionNativeUiOptions>();
        var hub = context.Services.Get<ICompanionHub>();
        var texts = context.Services.Get<IGameText>();
        context.Services.TryGet<IOnlinePlayers>(out var online);
        var ui = new CompanionNativeUi(hub, options, TimeProvider.System, context.Logger);
        context.Effect(ui);
        context.Effect(hub.Offer(InvokeType, 1));
        context.Effect(hub.Route(ResultType, 1, ui.OnResultAsync));
        context.Effect(context.Services.Register<IClientNativeUi>(ui));

        const string permission = "game.management.clientui";
        const string description = "Show the game's own message box, notification, banner or local chat line to one player (developer test).";
        context.Effect(context.Services.Get<IPermissionRegistry>().Register(new(permission, description)));
        context.Effect(context.Services.Get<ICommandRegistry>().Register(context,
            new("clientui", [], permission, description,
                [new("nickname", CommandArgumentKind.Text), new("kind", CommandArgumentKind.Text), new("text", CommandArgumentKind.RemainingText)]),
            async (invocation, ct) =>
            {
                var (nickname, kind, text) = (invocation.Arguments[0], invocation.Arguments[1], invocation.Arguments[2]);
                if (online?.FindByNickname(nickname) is not { } target)
                    return new CommandResult(CommandStatus.InvalidArguments, texts.Format("", "companion.clientui_no_player", Args(("nickname", nickname))));
                ClientUiRequest request;
                try { request = Request(kind, text, texts.Format(target, "companion.popup_title")); }
                catch (ArgumentException) { return new CommandResult(CommandStatus.InvalidArguments, texts.Format("", "companion.clientui_usage")); }
                var outcome = await ui.ShowAsync(target, request, ct).ConfigureAwait(false);
                return new CommandResult(CommandStatus.Success, texts.Format("", "companion.clientui_result", Args(
                    ("status", outcome.Status), ("presenter", outcome.Presenter.Length == 0 ? "-" : outcome.Presenter), ("button", outcome.ButtonId ?? "-"))));
            }));
        return ValueTask.CompletedTask;
    }

    // The developer command's kinds: a message box with OK and Cancel, a notification, a banner or a system chat line.
    private static ClientUiRequest Request(string kind, string text, string title) => kind switch
    {
        "box" => new("messageBox", text) { Title = title, Buttons = [new("ok", "OK"), new("cancel", "Cancel")] },
        "notice" => new("notification", text),
        "banner" => new("announcement.show", text),
        "chat" => new("chat", "") { Runs = [new(text, "#FFE36C")] },
        _ => throw new ArgumentException("Unknown kind.", nameof(kind)),
    };

    private static Dictionary<string, string> Args(params (string Key, string Value)[] values) =>
        values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
}

/// <summary>Bounds of the <c>companion.native_ui</c> row.</summary>
public sealed class CompanionNativeUiOptions
{
    /// <summary>Gets or sets how many requests may wait for their outcome at once across all players.</summary>
    [Range(1, 65536)] public int MaxPending { get; set; } = 1024;

    /// <summary>Gets or sets how long a message box waits for the player before the caller gets <c>timeout</c>, in seconds.</summary>
    [Range(5, 86400)] public int AnswerTimeoutSeconds { get; set; } = 600;

    /// <summary>Gets or sets how long the other kinds wait for their presentation, in seconds.</summary>
    [Range(1, 600)] public int PresentTimeoutSeconds { get; set; } = 30;
}

/// <summary>Correlates <c>ui.native.invoke</c> requests with the launcher's results.</summary>
internal sealed class CompanionNativeUi(ICompanionHub hub, CompanionNativeUiOptions options, TimeProvider clock, ILogger logger)
    : IClientNativeUi, IDisposable
{
    private static readonly string[] Kinds = ["messageBox", "notification", "announcement.show", "announcement.close", "chat"];
    private static readonly string[] Icons = ["none", "notice", "warning", "question", "error"];
    // A message box ends when the player answers; the other kinds when they were presented or could not be.
    private static readonly string[] Final = ["selected", "closed", "dismissed", "unsupported", "notReady", "busy", "failed", "outcomeUnknown"];
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private int _disposed;

    private sealed record Pending(long SessionId, string Kind, TaskCompletionSource<ClientUiOutcome> Done);

    public bool Supports(ISession session) => hub.Supports(session, CompanionNativeUiPlugin.InvokeType, 1);

    public async ValueTask<ClientUiOutcome> ShowAsync(ISession session, ClientUiRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        Validate(request);
        if (!Supports(session)) return new("unavailable", null, "");
        var operationId = Guid.NewGuid().ToString("N");
        var pending = new Pending(session.Id, request.Kind, new(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_pending.Count >= options.MaxPending) return new("busy", null, "");
            _pending.Add(operationId, pending);
        }
        try
        {
            if (!hub.Send(session, CompanionNativeUiPlugin.InvokeType, 1, new
            {
                OperationId = operationId, request.Kind, request.Text, request.Title, request.Icon,
                Buttons = request.Buttons.Select(button => new { button.Id, button.Label }).ToArray(),
                request.AnnouncementId, request.ChatStyle, request.ChatCaption,
                Runs = request.Runs.Select(run => new { run.Text, run.Color }).ToArray(), request.Fallback,
            }))
                return new("unavailable", null, "");
            var wait = TimeSpan.FromSeconds(request.Kind == "messageBox" ? options.AnswerTimeoutSeconds : options.PresentTimeoutSeconds);
            try { return await pending.Done.Task.WaitAsync(wait, clock, ct).ConfigureAwait(false); }
            catch (TimeoutException) { return new("timeout", null, ""); }
        }
        finally { lock (_gate) _pending.Remove(operationId); }
    }

    internal ValueTask<object?> OnResultAsync(CompanionCaller caller, JsonElement payload, CancellationToken ct)
    {
        var operationId = Text(payload, "operation_id");
        var status = Text(payload, "status");
        if (operationId is null || status is null) return ValueTask.FromResult<object?>(null);
        Pending? pending;
        lock (_gate)
            // Only the launch of the session that was asked can answer its request.
            if (!_pending.TryGetValue(operationId, out pending) || pending.SessionId != caller.Session.Id) return ValueTask.FromResult<object?>(null);
        var presenter = Text(payload, "presenter") ?? "";
        var done = pending.Kind == "messageBox" ? Final.Contains(status) : Final.Contains(status) || status == "shown";
        logger.LogDebug("Launch {LaunchId} reported client UI {OperationId}: {Status} ({Presenter}).", caller.LaunchId, operationId, status, presenter);
        if (done) pending.Done.TrySetResult(new(status, status == "selected" ? Text(payload, "button_id") : null, presenter));
        return ValueTask.FromResult<object?>(null);
    }

    // The launcher validates again; checking here makes a malformed request the caller's error, not a silent drop.
    private static void Validate(ClientUiRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Kinds.Contains(request.Kind)) throw new ArgumentException($"Unknown client UI kind '{request.Kind}'.", nameof(request));
        if (request.Text.Length > 8192 || request.Title.Length > 256) throw new ArgumentException("Client UI text is too long.", nameof(request));
        if (request.Kind == "messageBox")
        {
            if (!Icons.Contains(request.Icon) || request.Buttons.Count is < 1 or > 4
                || request.Buttons.Select(button => button.Id).Distinct(StringComparer.Ordinal).Count() != request.Buttons.Count
                || request.Buttons.Any(button => !Identity(button.Id) || button.Label.Length > 128))
                throw new ArgumentException("A message box needs 1 to 4 buttons with distinct identities and a known icon.", nameof(request));
        }
        else if (request.Buttons.Count != 0 || request.Title.Length != 0)
            throw new ArgumentException("Only a message box takes a title or buttons.", nameof(request));
        if (request.Kind == "chat" ? request.Runs.Count == 0 || request.Text.Length != 0 : request.Runs.Count != 0)
            throw new ArgumentException("A chat entry is coloured runs without a body; other kinds take no runs.", nameof(request));
        if (request.Kind == "announcement.close" && (request.AnnouncementId is null || !Identity(request.AnnouncementId)))
            throw new ArgumentException("Closing a banner needs its announcement identity.", nameof(request));
    }

    private static bool Identity(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    private static string? Text(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    public void Dispose()
    {
        Pending[] pending;
        lock (_gate)
        {
            _disposed = 1;
            pending = [.. _pending.Values];
            _pending.Clear();
        }
        foreach (var entry in pending) entry.Done.TrySetResult(new("unavailable", null, ""));
    }
}
