using System.Text.Json;
using System.Text.Json.Nodes;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Distribution;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Loader.Remote.Tests;

public sealed class ContractSchemaTests
{
    [Fact]
    public async Task OrdinaryPluginRowExportsWithoutAnyGameServiceOrSdkProcess()
    {
        var path = Path.Combine(Python.RepoRoot, "artifacts", "plugin-parity", "schema-test-" + Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var row = new PluginRow { Id = "schema", Plugin = "sdk.contracts.export", Source = "test",
            Config = new Dictionary<string, object?> { ["output_path"] = path } };
        var host = new PluginHost([row], PluginCatalog.ScanAssemblies([typeof(ContractSchemaExportPlugin).Assembly]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(default);
            Assert.Equal(PluginState.Active, Assert.Single(host.Instances).State);
            var actual = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            Assert.True(JsonNode.DeepEquals(new ContractSchema().Export(), actual));
        }
        finally { await host.ShutdownAsync(default); }
    }

    [Fact]
    public void ExportIsDeterministicAndDoesNotAdvertiseAnUnimplementedProtocol()
    {
        var first = new ContractSchema().Export();
        Assert.Equal(first.ToJsonString(), new ContractSchema().Export().ToJsonString());
        Assert.Equal("[1,2]", first["runtime_protocol_versions"]!.ToJsonString());
        Assert.Equal(new[] { "services.call", "services.provide", "services.decorate", "services.get_all",
            "services.inner", "services.invoke", "services.cancel",
            "messages.override", "messages.use", "messages.next", "messages.invoke", "messages.cancel",
            "events.on", "events.publish", "events.next", "events.invoke", "events.cancel",
            "tasks.start", "tasks.wait", "tasks.dispose", "tasks.invoke", "tasks.cancel",
            "resources.scope", "resources.scope.dispose", "resources.acquire", "resources.dispose",
            "resources.invoke", "resources.cancel", "lifecycle.cancel" },
            first["runtime_features"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.NotEmpty(first["messages"]!.AsArray());
        Assert.NotEmpty(first["events"]!.AsArray());
    }

    [Fact]
    public void EventOnlyPolicyResultsAreDiscoverableWithoutServiceSignatureReferences()
    {
        var schema = new ContractSchema().Export();
        var decision = Assert.Single(schema["event_result_types"]!.AsArray(), entry => entry!["name"]!.GetValue<string>() == nameof(EquipmentSelectionDecision))!;
        Assert.Equal(ContractSchema.TypeName(typeof(EquipmentSelectionDecision)), decision["full_name"]!.GetValue<string>());
        Assert.NotNull(schema["types"]![ContractSchema.TypeName(typeof(EquipmentSelectionDecision))]);
        Assert.True(decision["bail_compatible"]!.GetValue<bool>());
        Assert.Contains(ContractSchema.EventResultTypes, type => type == typeof(IReadOnlyList<InventorySlotRecord>));
    }

    [Fact]
    public void EveryContractInterfaceAndInheritedMethodHasAnExplicitDisposition()
    {
        var document = new ContractSchema().Export();
        foreach (var service in typeof(LoginRequested).Assembly.GetExportedTypes().Where(t => t.IsInterface))
        {
            var described = document["services"]!.AsArray().Single(s => s!["full_name"]!.GetValue<string>() == ContractSchema.TypeName(service))!;
            var methods = described["methods"]!.AsArray();
            var expected = service.GetInterfaces().Append(service).SelectMany(t => t.GetMethods()).Distinct().ToArray();
            Assert.Equal(expected.Length, methods.Count);
            foreach (var method in expected)
            {
                var entry = methods.Single(m => m!["id"]!.GetValue<string>() == ContractSchema.MethodId(method))!;
                Assert.True(entry["rpc_shape"]!.GetValue<string>() is "supported" or "unsupported");
                if (entry["rpc_shape"]!.GetValue<string>() == "unsupported")
                    Assert.False(string.IsNullOrWhiteSpace(entry["unsupported_reason"]!.GetValue<string>()));
                Assert.True(entry["provider_rpc_shape"]!.GetValue<string>() is "supported" or "unsupported");
                if (entry["provider_rpc_shape"]!.GetValue<string>() == "unsupported")
                    Assert.False(string.IsNullOrWhiteSpace(entry["provider_unsupported_reason"]!.GetValue<string>()));
            }
        }
    }

    [Fact]
    public void NewUnrepresentedDomainMethodsRequireAnExplicitReview()
    {
        var gaps = new ContractSchema().Export()["services"]!.AsArray()
            .Where(s => s!["namespace"]!.GetValue<string>().StartsWith("BubbleFighter.Fantasy.Contracts", StringComparison.Ordinal))
            .SelectMany(s => s!["methods"]!.AsArray().Where(m => m!["rpc_shape"]!.GetValue<string>() == "unsupported")
                .Select(m => s!["name"]!.GetValue<string>() + "." + m!["name"]!.GetValue<string>()))
            .Order(StringComparer.Ordinal).ToArray();
        // These gaps need adapters or callback ownership; adding an unsupported method must be an explicit decision.
        // Installation lifecycle operations remain host-local until the SDK surface defines an explicit setup owner.
        // All three text lookup overloads retain an out parameter, which the RPC adapter cannot transport.
        // Command registration needs a native owner context and callback lifetime; no remote adapter exists yet.
        // Permission registration returns an owned IDisposable, which the domain RPC adapter cannot transport.
        // Game and online-scope registrations require a native owner and endpoint callbacks.
        // Payload copies need caller-owned native streams; metadata and job operations use serializable records.
        // Messenger events are an abstract record hierarchy, which needs a tagged-union shape; the long poll stays host-local.
        // Companion routes and offers return owned registrations with handler callbacks, and Send takes an untyped payload.
        string[] reviewed = ["IAdminWebContent.TryGetFile", "IBuildReleaseLabels.TryGetRelease", "ICommandRegistry.Register",
            "ICompanionHub.Offer", "ICompanionHub.Route", "ICompanionHub.Send", "IGameCommandEndpoints.Register", "IGameText.TryFormat", "IGameText.TryFormat", "IGameText.TryFormat",
            "IDistributionTransport.CopyFileAsync", "IDistributionTransport.CopyRepositoryAssetAsync",
            "IInstallationDatabase.PrepareAsync", "IInstallationRuntime.ApplyAsync", "IInstallationService.ImportClientDataAsync",
            "IListenerEndpoint.get_LocalEndPoint", "IMatchCapabilityOfferSender.SendAsync", "IMessengerService.WaitEventsAsync", "INativeSceneSeedStore.TryGet",
            "IOnlineCommandScopes.Register", "IPaintItemCatalog.TryGetColorIndex", "IPermissionRegistry.Register", "IPlayerProgressRepository.SettleRoundAsync", "IRoomStartService.ListAsync"];
        Assert.Equal(reviewed.Order(StringComparer.Ordinal), gaps);
    }

    [Fact]
    public void DistributionStreamingRemainsNativeWhileDocumentMetadataIsSerializable()
    {
        Assert.Equal("supported", Operation<IDistributionTransport>(nameof(IDistributionTransport.ReadDocumentAsync))["rpc_shape"]!.GetValue<string>());
        foreach (var name in new[] { nameof(IDistributionTransport.CopyFileAsync), nameof(IDistributionTransport.CopyRepositoryAssetAsync) })
        {
            var method = Operation<IDistributionTransport>(name);
            Assert.Equal("unsupported", method["rpc_shape"]!.GetValue<string>());
            Assert.Contains("Stream", method["unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NullableWalletAndInt64ArgumentsKeepTheirWireMeaning()
    {
        var method = Operation<IPlayerWalletRepository>(nameof(IPlayerWalletRepository.FindByUserNumAsync));
        Assert.Equal("supported", method["rpc_shape"]!.GetValue<string>());
        Assert.Equal("async", method["execution"]!.GetValue<string>());
        var user = method["parameters"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "user_num")!;
        Assert.Equal("int64", user["type"]!["format"]!.GetValue<string>());
        var cancellation = method["parameters"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "ct")!;
        Assert.Equal("cancellation", cancellation["injected"]!.GetValue<string>());
        Assert.Null(cancellation["type"]);
        Assert.Equal("null", method["result"]!["any_of"]![1]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void DelegateInsideSettlementCommandIsNotMistakenForSerializableData()
    {
        var method = Operation<IPlayerProgressRepository>(nameof(IPlayerProgressRepository.SettleRoundAsync));
        Assert.Equal("unsupported", method["rpc_shape"]!.GetValue<string>());
        Assert.Contains("advance", method["unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("callback", method["unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void SyncPropertiesAndSessionResultsAreNotLost()
    {
        var property = Operation<IRoundResultSequencePolicy>("get_EndBeforeScoreboard");
        Assert.Equal("sync", property["execution"]!.GetValue<string>());
        Assert.Equal("boolean", property["result"]!["type"]!.GetValue<string>());
        var session = Operation<IOnlinePlayers>(nameof(IOnlinePlayers.FindByUserNum));
        Assert.Equal("session_handle", session["result"]!["any_of"]![0]!["format"]!.GetValue<string>());
    }

    [Fact]
    public void DictionaryElementsAndOptionalRecordFieldsRetainTheirTypes()
    {
        var document = new ContractSchema().Export();
        var inventory = document["types"]![typeof(InventorySlotRecord).FullName!]!;
        Assert.NotEmpty(inventory["fields"]!.AsArray());
        var serialized = JsonSerializer.SerializeToElement(new PlayerWallet(7, 11, 13), WireJson.Options);
        var fields = document["types"]![typeof(PlayerWallet).FullName!]!["fields"]!.AsArray();
        Assert.Equal(serialized.EnumerateObject().Select(p => p.Name).Order(), fields.Select(f => f!["name"]!.GetValue<string>()).Order());
        Assert.All(fields, f => Assert.True(f!["required"]!.GetValue<bool>()));
    }

    [Fact]
    public void ServiceRegistrationModeIsNotInventedFromItsInterface()
    {
        var services = new ContractSchema().Export()["services"]!.AsArray();
        Assert.All(services, s => Assert.Equal("provider_declared", s!["registration"]!.GetValue<string>()));
    }

    private static JsonObject Operation<T>(string name) => new ContractSchema().Operation(typeof(T).GetMethod(name)!);
}
