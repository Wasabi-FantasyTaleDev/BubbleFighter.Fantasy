using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>A checked native route and the named fields needed by the shared life handler.</summary>
internal sealed record RoundFeedRoute(ushort GameInbound, ushort RelayInbound, ushort Outbound, string Structure, string[] Actors,
    bool EchoSender, bool Nested, RoundLifeEvent? LifeEvent, string? LifeActor, string? Initiator,
    string? Success, string? DeathFlag)
{
    internal static RoundFeedRoute[] Load(PacketTables packets, string build, IOpcodeTable game, IOpcodeTable relay)
    {
        if (game.Connection != OpcodeConnection.Game || relay.Connection != OpcodeConnection.Relay || game.Profile.Id != relay.Profile.Id)
            throw new ConfigException("Round feed requires the same profile's separate game and relay opcode views.");
        if (packets.ValuesOf(build).GetValueOrDefault("round_feed") is not Dictionary<string, object?> routes || routes.Count == 0)
            throw new ConfigException($"{build} round_feed must state the audited native action routes.");
        var result = new List<RoundFeedRoute>();
        foreach (var (key, raw) in routes)
        {
            var where = $"{build} round_feed.{key}";
            if (raw is not Dictionary<string, object?> map)
                throw new ConfigException($"{where} must be a route map.");
            if (map.Keys.FirstOrDefault(name => name is not ("enabled" or "opcode" or "structure" or "actors" or "echo_sender"
                or "nested" or "life_event" or "life_actor" or "life_initiator" or "life_success" or "life_death_flag")) is { } unknown)
                throw new ConfigException($"{where} has unknown key '{unknown}'.");
            if (!Flag("enabled", true))
                continue;
            var name = Text("opcode", required: true)!;
            var inbound = game.Find(PacketDirection.ClientToServer, name)
                ?? throw new ConfigException($"{where} requires game C2S {name}.");
            var relayInbound = relay.Find(PacketDirection.ClientToServer, name)
                ?? throw new ConfigException($"{where} requires relay C2S {name}.");
            var outbound = relay.Find(PacketDirection.ServerToClient, name)
                ?? throw new ConfigException($"{where} requires relay S2C {name}.");
            var structure = Text("structure", required: true)!;
            if (map.GetValueOrDefault("actors") is not string[] actors || actors.Distinct(StringComparer.Ordinal).Count() != actors.Length)
                throw new ConfigException($"{where}.actors must list distinct reporting identity fields, or an empty list for damager keys.");
            foreach (var actor in actors)
                Require(actor, "u32");
            var nested = Flag("nested", false);
            var fields = packets.Resolve(build, structure);
            if (fields.Count == 0 || (!nested && fields.Any(field => field.Type is not ("u8" or "u16" or "u32" or "bool" or "f32"))))
                throw new ConfigException($"{where}.structure must be nonempty and scalar unless nested is explicitly enabled.");
            var lifeName = Text("life_event", required: false);
            RoundLifeEvent? life = lifeName switch
            {
                null => null,
                "bubbled" => RoundLifeEvent.Bubbled,
                "died" => RoundLifeEvent.Died,
                "released" => RoundLifeEvent.Released,
                "bubble_expired" => RoundLifeEvent.BubbleExpired,
                "finished" => RoundLifeEvent.Finished,
                "death_cleaned" => RoundLifeEvent.DeathCleaned,
                "reset_confirmed" => RoundLifeEvent.ResetConfirmed,
                _ => throw new ConfigException($"{where}.life_event has unsupported value '{lifeName}'."),
            };
            var lifeActor = Text("life_actor", required: life is not null);
            var initiator = Text("life_initiator", required: life == RoundLifeEvent.Finished);
            var success = Text("life_success", required: false);
            var deathFlag = Text("life_death_flag", required: false);
            if ((life is null && (lifeActor is not null || initiator is not null || success is not null || deathFlag is not null))
                || (initiator is not null && life != RoundLifeEvent.Finished)
                || (deathFlag is not null && life != RoundLifeEvent.Bubbled)
                || (life is not null && actors.Length == 0))
                throw new ConfigException($"{where} has life fields incompatible with its life_event.");
            if (lifeActor is not null) Require(lifeActor, "u32");
            if (initiator is not null) Require(initiator, "u32");
            foreach (var gate in new[] { success, deathFlag }.OfType<string>())
            {
                var field = fields.SingleOrDefault(field => field.Name == gate);
                if (field?.Type is not ("u32" or "bool"))
                    throw new ConfigException($"{where} requires {structure}.{gate} as a u32 or bool life gate.");
            }
            result.Add(new(inbound, relayInbound, outbound, structure, actors, Flag("echo_sender", false), nested,
                life, lifeActor, initiator, success, deathFlag));

            string? Text(string field, bool required)
            {
                if (!map.TryGetValue(field, out var value) && !required)
                    return null;
                return value is string text && !string.IsNullOrWhiteSpace(text)
                    ? text : throw new ConfigException($"{where}.{field} must be nonempty text.");
            }
            bool Flag(string field, bool fallback) => !map.TryGetValue(field, out var value) ? fallback : value switch
            {
                "true" => true,
                "false" => false,
                _ => throw new ConfigException($"{where}.{field} must be true or false."),
            };
            void Require(string field, string type) =>
                PacketLayoutContract.RequireFields(packets, build, structure, where, (field, type));
        }
        if (result.Count == 0 || result.Select(route => route.GameInbound).Distinct().Count() != result.Count
            || result.Select(route => route.RelayInbound).Distinct().Count() != result.Count
            || result.Select(route => route.Outbound).Distinct().Count() != result.Count)
            throw new ConfigException($"{build} round_feed requires nonempty routes with distinct native C2S and S2C opcodes.");
        return [.. result];
    }

    internal (RoundFeedSent Feed, RoundLifeReported? Life) Read(PacketTables packets, string build, ReadOnlySpan<byte> body)
    {
        var values = Nested ? RoundResetBinding.Read(packets, build, Structure, body)
            : PacketCodec.ReadScalars(packets, build, Structure, body).ToDictionary(pair => pair.Key, pair => (object?)pair.Value);
        var feed = new RoundFeedSent(Outbound, body.ToArray())
        {
            OnRelay = true, IncludeSender = EchoSender,
            ReportedActors = Actors.Length == 0 ? null : Actors.Select(field => (int)values[field]!).ToArray(),
        };
        if (LifeEvent is not { } kind || (Success is not null && !Truthy(Success)))
            return (feed, null);
        if (DeathFlag is not null && Truthy(DeathFlag))
            kind = RoundLifeEvent.Died;
        return (feed, new RoundLifeReported(kind, (int)values[LifeActor!]!)
        {
            Initiator = Initiator is null ? null : (int)values[Initiator]!,
        });

        bool Truthy(string field) => values[field] switch
        {
            bool flag => flag,
            int word => word != 0,
            _ => throw new InvalidOperationException($"{Structure}.{field} did not decode as its checked life gate type."),
        };
    }
}
