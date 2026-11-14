using System.Text.Json;
using System.Text.Json.Nodes;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Distribution;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Companion;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Titles;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using BubbleFighter.Fantasy.Contracts.Rooms;

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
        // Companion federation keeps URI endpoints and revocable trust registrations in the native process.
        // UDP lease registration needs native callback ownership; span/out lookups and IP endpoint records have no RPC adapter.
        // Authenticated match operations retain the native handshake gate and a cancellation-bound callback.
        // Account acquisition returns an owned native connection handle; the SDK has no adapter for that lifetime.
        // Retained player installation returns native session state, which has no value-only RPC representation.
        // Handoff storage retains the same native handle; migration addresses and owned broker callbacks have no adapter.
        // Room migration binding owns asynchronous source callbacks and their drain lifetime; it stays in-process.
        // Plaza links and UDP status registrations also own native callbacks and IDisposable lifetimes, with no remote adapter.
        // Battle records and atomic round medals inherit the same native settlement callback, which is not transportable.
        // Ban-pick and room policies register native policy callbacks with owned lifetimes; the hub hands out native
        // service references, and the scope's companion seam takes a native caller and raw JSON.
        // Pet combat-effect resolution returns its effects through an out parameter, which the RPC adapter cannot transport.
        // Client data opens as a native stream that unpacks while it is read; a remote plugin reads the same file
        // through the byte-array reads and asks its size with Length, which are transportable. The workbook cache takes
        // a parse callback and is reached only through WorkbookFile inside the host; a remote store simply has none.
        // Compiled client data takes a compile callback and writes through native streams; a store without it (any
        // remote store) builds the catalog on every load, which CompiledClientData.Load does by itself.
        string[] reviewed = ["ICompiledCatalog`1.Read", "ICompiledCatalog`1.Write", "ICompiledCatalog`1.get_Name", "ICompiledClientData.Load",
            "IClientDataStore.OpenRead", "IClientDataStore.OpenRead", "IClientWorkbookCache.Sheets", "IPetCombatEffects.TryResolve", "IBanPickHub.Register", "IBanPickHub.get_Companion", "IBanPickHub.get_Store", "IBanPickScope.CompanionAsync",
            "IRoomPolicyHost.Register", "IAccountHandoffStore.HandOffAsync", "IAccountHandoffStore.TryClaimAsync", "IAccountHandoffStore.TryWithdrawAsync",
            "IAccountMutationFence.VerifyAsync", "IAccountMutationScopes.Bind", "IAccountMutationScopes.Enter", "IAccountOwnershipStore.TryAcquireAsync", "IAdminWebContent.TryGetFile", "IBuildReleaseLabels.TryGetRelease", "ICommandRegistry.Register",
            "ICompanionFederation.RedirectAsync", "ICompanionFederation.TrustAuthority",
            "ICompanionHub.Offer", "ICompanionHub.Route", "ICompanionHub.Send", "IGameCommandEndpoints.Register", "IGameText.TryFormat", "IGameText.TryFormat", "IGameText.TryFormat",
            "IDistributionTransport.CopyFileAsync", "IDistributionTransport.CopyRepositoryAssetAsync",
            "IInstallationDatabase.PrepareAsync", "IInstallationRuntime.ApplyAsync", "IInstallationService.ImportClientDataAsync",
            "IListenerEndpoint.get_LocalEndPoint", "IMatchCapabilityOfferSender.SendAsync",
            "IMatchConnectionOperations.RunAuthenticatedAsync",
            "IMatchUdpRelayDirectory.Register", "IMatchUdpRelayDirectory.TryBind", "IMatchUdpRelayDirectory.TryGetEndpoint",
            "IMatchUdpRelayDirectory.TryGetTarget", "IMatchUdpRelayDirectory.TryResolve",
            "IMessengerService.WaitEventsAsync", "INativeSceneSeedStore.TryGet",
            "IMigrationArrivals.Cancel", "IMigrationArrivals.Expect", "IMigrationArrivals.TryConsume",
            "IMigrationArrivalCleanup.Collect", "IMigrationArrivalCleanup.Complete", "IMigrationArrivalCleanup.Retire",
            "IMigrationBroker.RequestAsync", "IMigrationBroker.Serve",
            "IOnlineCommandScopes.Register", "IPaintItemCatalog.TryGetColorIndex", "IPartyDirectory.TryReserveSolo", "IPermissionRegistry.Register", "IPlayerBattleRecordsRepository.SettleRoundAsync", "IPlayerProgressRepository.SettleRoundAsync", "IPlazaFederationEndpoint.Bind", "IRemoteRoomMigrationEndpoint.Bind", "IRetainedPlayerAdmission.InstallAsync", "IRoomPeerRoster.ExecuteAsync", "IRoomStartService.ListAsync",
            "IRoundTitleMedalSettlementRepository.SettleRoundAsync",
            "IUdpPeerDialect.ReadDiscovery", "IUdpPeerDialect.SendCandidateAsync", "IUdpTransportStatusRegistry.Register"];
        Assert.Equal(reviewed.Order(StringComparer.Ordinal), gaps);
    }

    /// <summary>The match operation callback stays native while its protocol identity remains a serializable value.</summary>
    [Fact]
    public void AuthenticatedMatchOperationRequiresAnOwnedNativeCallbackBridge()
    {
        var operation = Operation<IMatchConnectionOperations>(nameof(IMatchConnectionOperations.RunAuthenticatedAsync));
        var protocol = Operation<IMatchConnectionOperations>("get_Protocol");
        foreach (var prefix in new[] { "", "provider_" })
        {
            Assert.Equal("unsupported", operation[prefix + "rpc_shape"]!.GetValue<string>());
            Assert.Contains("Parameter 'operation'", operation[prefix + "unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Contains("owned callback bridge", operation[prefix + "unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal("supported", protocol[prefix + "rpc_shape"]!.GetValue<string>());
        }
        var error = Assert.Throws<ConfigException>(() => RemoteServiceProvider.Validate(typeof(IMatchConnectionOperations)));
        Assert.Contains(nameof(IMatchConnectionOperations.RunAuthenticatedAsync), error.Message, StringComparison.Ordinal);
        Assert.Contains("owned callback bridge", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Federation retains native endpoint and trust ownership until those shapes have explicit SDK adapters.</summary>
    [Theory]
    [InlineData(nameof(ICompanionFederation.RedirectAsync), "System.Uri")]
    [InlineData(nameof(ICompanionFederation.TrustAuthority), "System.IDisposable")]
    public void CompanionFederationRequiresNativeRepresentations(string method, string nativeType)
    {
        var operation = Operation<ICompanionFederation>(method);
        Assert.Equal("unsupported", operation["rpc_shape"]!.GetValue<string>());
        Assert.Equal("unsupported", operation["provider_rpc_shape"]!.GetValue<string>());
        Assert.Contains(nativeType, operation["unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains(nativeType, operation["provider_unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>Plaza links and UDP reporters retain their native registration ownership instead of advertising a value-only RPC adapter.</summary>
    [Fact]
    public void PlazaAndUdpRegistrationsRetainNativeOwnership()
    {
        foreach (var operation in new[]
        {
            Operation<IPlazaFederationEndpoint>(nameof(IPlazaFederationEndpoint.Bind)),
            Operation<IUdpTransportStatusRegistry>(nameof(IUdpTransportStatusRegistry.Register)),
        })
        {
            Assert.Equal("unsupported", operation["rpc_shape"]!.GetValue<string>());
            Assert.Equal("unsupported", operation["provider_rpc_shape"]!.GetValue<string>());
            Assert.Contains(nameof(IDisposable), operation["unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MigrationShapesKeepNativeOwnershipAndAddressesExplicitWithoutDisablingSerializableOperations()
    {
        foreach (var method in new[] { nameof(IAccountHandoffStore.HandOffAsync), nameof(IAccountHandoffStore.TryClaimAsync),
            nameof(IAccountHandoffStore.TryWithdrawAsync) })
        {
            var operation = Operation<IAccountHandoffStore>(method);
            Assert.Equal("unsupported", operation["rpc_shape"]!.GetValue<string>());
            Assert.Equal("unsupported", operation["provider_rpc_shape"]!.GetValue<string>());
            Assert.Contains(nameof(IAccountOwnership), operation["unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
        }
        foreach (var method in new[] { nameof(IMigrationArrivals.Expect), nameof(IMigrationArrivals.TryConsume), nameof(IMigrationArrivals.Cancel) })
        {
            var operation = Operation<IMigrationArrivals>(method);
            Assert.Equal("unsupported", operation["rpc_shape"]!.GetValue<string>());
            Assert.Contains("IPAddress", operation["unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
        }
        Assert.Contains("IPAddress", Operation<IMigrationBroker>(nameof(IMigrationBroker.RequestAsync))
            ["unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("IDisposable", Operation<IMigrationBroker>(nameof(IMigrationBroker.Serve))
            ["unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("supported", Operation<IMigrationBroker>(nameof(IMigrationBroker.CancelAsync))["rpc_shape"]!.GetValue<string>());
        var binding = Operation<IRemoteRoomMigrationEndpoint>(nameof(IRemoteRoomMigrationEndpoint.Bind));
        Assert.Equal("unsupported", binding["rpc_shape"]!.GetValue<string>());
        Assert.Equal("unsupported", binding["provider_rpc_shape"]!.GetValue<string>());
        Assert.Contains(nameof(IAsyncDisposable), binding["unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
        foreach (var method in new[] { nameof(IMigrationArrivalCleanup.Collect), nameof(IMigrationArrivalCleanup.Complete),
            nameof(IMigrationArrivalCleanup.Retire) })
        {
            var operation = Operation<IMigrationArrivalCleanup>(method);
            Assert.Equal("unsupported", operation["rpc_shape"]!.GetValue<string>());
            Assert.Equal("unsupported", operation["provider_rpc_shape"]!.GetValue<string>());
            Assert.Contains("IPAddress", operation["unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
        }
        foreach (var method in new[] { "get_NodeId", "get_DirectoryUrl", "get_Current" })
        {
            var operation = Operation<INodeLeaseSource>(method);
            Assert.Equal("supported", operation["rpc_shape"]!.GetValue<string>());
            Assert.Equal("supported", operation["provider_rpc_shape"]!.GetValue<string>());
        }
        Assert.DoesNotContain("private-lease-token", new NodeLeaseCredentials("private-lease-token", 1).ToString(), StringComparison.Ordinal);
        foreach (var method in new[] { nameof(IAccountSessionHandoff.HandOffAsync), nameof(IAccountSessionHandoff.TryClaimAsync),
            nameof(IAccountSessionHandoff.TryWithdrawAsync) })
            Assert.Equal("supported", Operation<IAccountSessionHandoff>(method)["rpc_shape"]!.GetValue<string>());
    }

    [Fact]
    public void InitialHandshakeContinuationRemainsBoundToNativePacketDispatch()
    {
        var operation = Operation<IInitialHandshakeContinuation>(nameof(IInitialHandshakeContinuation.ContinueWithoutVersionVerification));
        Assert.Equal("unsupported", operation["rpc_shape"]!.GetValue<string>());
        Assert.Equal("unsupported", operation["provider_rpc_shape"]!.GetValue<string>());
        Assert.Contains("native packet dispatch lifetime", operation["unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("native packet dispatch lifetime", operation["provider_unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void AccountAcquisitionDoesNotSerializeAnOwnedNativeConnectionHandle()
    {
        var operation = Operation<IAccountOwnershipStore>(nameof(IAccountOwnershipStore.TryAcquireAsync));
        Assert.Equal("unsupported", operation["rpc_shape"]!.GetValue<string>());
        Assert.Equal("unsupported", operation["provider_rpc_shape"]!.GetValue<string>());
        Assert.Contains(nameof(IAccountOwnership), operation["unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains(nameof(IAccountOwnership), operation["provider_unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void NativeMigrationMessagesPreserveUnsignedStageAndWidenedIdentityInTheSdk()
    {
        var schema = new ContractSchema().Export();
        var types = new[] { typeof(MoveIntoRequested), typeof(MoveIntoReplied), typeof(PendingConnectionPromoted),
            typeof(PendingPromotionAcknowledged), typeof(PendingPromotionCompleted) };
        foreach (var type in types)
        {
            Assert.Contains(schema["messages"]!.AsArray(), message =>
                message!["full_name"]!.GetValue<string>() == ContractSchema.TypeName(type));
            Assert.NotNull(schema["types"]![ContractSchema.TypeName(type)]);
        }
        var message = new MoveIntoRequested(uint.MaxValue, 1);
        var json = JsonSerializer.SerializeToElement(message, WireJson.Options);
        Assert.Equal(4294967295L, json.GetProperty("retained_player_id").GetInt64());
        Assert.Equal(1u, json.GetProperty("stage_word").GetUInt32());
        Assert.Equal(message, JsonSerializer.Deserialize<MoveIntoRequested>(json, WireJson.Options));
    }

    [Fact]
    public void RetainedPlayerLookupIsSerializableWithoutGrantingAnOwnershipHandle()
    {
        foreach (var method in new[] { nameof(IPlayerRepository.FindByUserNumAsync), "get_SupportsRetainedAccountLookup" })
        {
            var operation = Operation<IPlayerRepository>(method);
            Assert.Equal("supported", operation["rpc_shape"]!.GetValue<string>());
            Assert.Equal("supported", operation["provider_rpc_shape"]!.GetValue<string>());
        }
    }

    [Fact]
    public void MutationVerificationRequiresTheCallersNativeConnectionAndTransaction()
    {
        var operation = Operation<IAccountMutationFence>(nameof(IAccountMutationFence.VerifyAsync));
        Assert.Equal("unsupported", operation["rpc_shape"]!.GetValue<string>());
        Assert.Equal("unsupported", operation["provider_rpc_shape"]!.GetValue<string>());
        Assert.Contains("DbConnection", operation["unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>Pins unsupported native UDP ownership and endpoint shapes without excluding serializable controls.</summary>
    [Fact]
    public void UdpCallbacksAndEndpointLookupsRemainExplicitlyNative()
    {
        var queue = Operation<IRoomPeerRoster>(nameof(IRoomPeerRoster.ExecuteAsync));
        Assert.Equal("unsupported", queue["rpc_shape"]!.GetValue<string>());
        Assert.Contains("callback", queue["unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("supported", Operation<IRoomPeerRoster>(nameof(IRoomPeerRoster.Snapshot))["rpc_shape"]!.GetValue<string>());
        Assert.Contains("IDisposable", Operation<IMatchUdpRelayDirectory>(nameof(IMatchUdpRelayDirectory.Register))
            ["unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
        foreach (var name in new[] { nameof(IMatchUdpRelayDirectory.TryBind), nameof(IMatchUdpRelayDirectory.TryResolve),
            nameof(IMatchUdpRelayDirectory.TryGetEndpoint), nameof(IMatchUdpRelayDirectory.TryGetTarget) })
        {
            var operation = Operation<IMatchUdpRelayDirectory>(name);
            Assert.Equal("unsupported", operation["rpc_shape"]!.GetValue<string>());
            Assert.Contains("Managed references", operation["unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
        }
        foreach (var name in new[] { nameof(IUdpPeerDialect.ReadDiscovery), nameof(IUdpPeerDialect.SendCandidateAsync) })
        {
            var operation = Operation<IUdpPeerDialect>(name);
            Assert.Equal("unsupported", operation["rpc_shape"]!.GetValue<string>());
            Assert.Contains("IPAddress", operation["unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
        }
        Assert.Equal("supported", Operation<IMatchUdpRelayControl>(nameof(IMatchUdpRelayControl.OfferAsync))
            ["rpc_shape"]!.GetValue<string>());
        Assert.Equal("supported", Operation<IMatchUdpRelayDirectory>(nameof(IMatchUdpRelayDirectory.IssueAsync))
            ["rpc_shape"]!.GetValue<string>());
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DelegateInsideSettlementCommandIsNotMistakenForSerializableData(bool includesMedals)
    {
        var type = includesMedals ? typeof(IRoundTitleMedalSettlementRepository) : typeof(IPlayerProgressRepository);
        var service = new ContractSchema().Export()["services"]!.AsArray().Single(item =>
            item!["full_name"]!.GetValue<string>() == ContractSchema.TypeName(type))!;
        var method = service["methods"]!.AsArray().Single(item =>
            item!["name"]!.GetValue<string>() == nameof(IPlayerProgressRepository.SettleRoundAsync))!;
        foreach (var prefix in new[] { "", "provider_" })
        {
            Assert.Equal("unsupported", method[prefix + "rpc_shape"]!.GetValue<string>());
            Assert.Contains("advance", method[prefix + "unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Contains("callback", method[prefix + "unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
        }
    }

    /// <summary>Battle history reads remain usable through RPC while inherited settlement callbacks retain native ownership.</summary>
    [Fact]
    public void BattleRecordReadsDoNotAdvertiseInheritedSettlementCallbacks()
    {
        var services = new ContractSchema().Export()["services"]!.AsArray();
        var service = Assert.Single(services, item => item!["full_name"]!.GetValue<string>()
            == ContractSchema.TypeName(typeof(IPlayerBattleRecordsRepository)))!;
        var methods = service["methods"]!.AsArray();
        var settlement = Assert.Single(methods, item => item!["name"]!.GetValue<string>()
            == nameof(IPlayerProgressRepository.SettleRoundAsync))!;
        foreach (var prefix in new[] { "", "provider_" })
        {
            Assert.Equal("unsupported", settlement[prefix + "rpc_shape"]!.GetValue<string>());
            Assert.Contains("advance", settlement[prefix + "unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Contains("callback", settlement[prefix + "unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
        }
        foreach (var name in new[] { nameof(IPlayerBattleRecordsRepository.ListBattleRecordsAsync), "get_IsDurable" })
        {
            var read = Assert.Single(methods, item => item!["name"]!.GetValue<string>() == name)!;
            Assert.Equal("supported", read["rpc_shape"]!.GetValue<string>());
            Assert.Equal("supported", read["provider_rpc_shape"]!.GetValue<string>());
        }
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
