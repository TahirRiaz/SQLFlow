using System.Text.Json;
using SqlFlow.Azure.Invoke;
using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Invoke;
using SqlFlow.Core.Invoke.Legacy;
using SqlFlow.SqlServer.Invoke;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Non-overlapping edge cases for the Invoke feature area: the legacy mapper and InvokeType codec, the
/// parameter-JSON conversions, the dispatcher's failure/cancellation contract, the in-memory invoke and
/// service-principal stores, and the standalone invoke YAML surface plus its hooks. Every test is pure and
/// in-memory (no database, no network) and deterministic (fixed inputs only). These complement, and do not
/// duplicate, the cases in InvokeFlowMappingTests, InvokeDispatcherTests, InMemoryInvokeStoresTests,
/// InvokeParameterJsonTests, YamlInvokeFlowLoaderTests, and YamlInvokeHooksTests.
/// </summary>
public sealed class InvokeEdgeCaseTests
{
    private static readonly YamlInvokeFlowLoader InvokeLoader = new();

    private const string MinimalAdf = """
        flowType: inv
        name: trigger-refresh
        servicePrincipals:
          deploy:
            subscriptionId: 00000000-0000-0000-0000-000000000001
            resourceGroup: rg-data
            dataFactoryName: adf-prod
        invoke:
          type: adf
          pipeline: pl_refresh
          servicePrincipal: deploy
        """;

    private static LegacyInvokeRow EdgeRow() => new()
    {
        FlowID = 42,
        InvokeAlias = "RefreshAdf",
        InvokeType = "adf",
    };

    private static InvokeDefinition EdgeInvoke(
        string alias, int flowId, string? batch = null, bool deactivated = false) => new()
    {
        FlowId = flowId,
        InvokeAlias = alias,
        InvokeType = InvokeType.AzureDataFactory,
        PipelineName = "pl",
        Batch = batch,
        DeactivateFromBatch = deactivated,
        TargetServicePrincipalReference = "@sp",
    };

    private static ServicePrincipalProfile EdgeProfile(string alias, string? secretRef = null) => new()
    {
        Alias = alias,
        SubscriptionId = "s",
        ResourceGroup = "rg",
        ClientSecretRef = secretRef,
    };

    // ----- InvokeTypeCodes: parsing and round-trip -----------------------------------------------------------

    [Theory]
    [InlineData("ADF", InvokeType.AzureDataFactory)]
    [InlineData("Adf", InvokeType.AzureDataFactory)]
    [InlineData("  adf  ", InvokeType.AzureDataFactory)]
    [InlineData("AUT", InvokeType.AzureAutomation)]
    [InlineData("Aut", InvokeType.AzureAutomation)]
    [InlineData("\taut\t", InvokeType.AzureAutomation)]
    public void Codec_ParseIsCaseAndWhitespaceInsensitive(string code, InvokeType expected)
        => Assert.Equal(expected, InvokeTypeCodes.Parse(code));

    [Theory]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData(" \r\n ")]
    public void Codec_WhitespaceOnlyCode_DefaultsToAutomation(string code)
        => Assert.Equal(InvokeType.AzureAutomation, InvokeTypeCodes.Parse(code));

    [Theory]
    [InlineData("PS")]
    [InlineData("Cs")]
    [InlineData(" ps ")]
    public void Codec_RemovedHostCodes_ThrowInjectionRegardlessOfCasing(string code)
    {
        var ex = Assert.Throws<SqlFlowException>(() => InvokeTypeCodes.Parse(code));
        Assert.Contains("code-injection", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Codec_UnknownCode_NamesTheAllowedSet()
    {
        var ex = Assert.Throws<SqlFlowException>(() => InvokeTypeCodes.Parse("ssis"));
        Assert.Contains("adf, aut", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(InvokeType.AzureDataFactory, "adf")]
    [InlineData(InvokeType.AzureAutomation, "aut")]
    public void Codec_ToCode_RoundTripsWithParse(InvokeType type, string expectedCode)
    {
        Assert.Equal(expectedCode, InvokeTypeCodes.ToCode(type));
        Assert.Equal(type, InvokeTypeCodes.Parse(InvokeTypeCodes.ToCode(type)));
    }

    [Fact]
    public void Codec_ToCode_UndefinedEnumValue_Throws()
        => Assert.Throws<SqlFlowException>(() => InvokeTypeCodes.ToCode((InvokeType)99));

    [Fact]
    public void Definition_FlowType_DerivesFromInvokeType()
    {
        Assert.Equal("aut", new InvokeDefinition { InvokeAlias = "a" }.FlowType);
        Assert.Equal("adf", new InvokeDefinition { InvokeAlias = "a", InvokeType = InvokeType.AzureDataFactory }.FlowType);
    }

    // ----- InvokeFlowMapper: lossless field handling ---------------------------------------------------------

    [Fact]
    public void Mapper_NullRow_Throws()
        => Assert.Throws<ArgumentNullException>(() => InvokeFlowMapper.FromLegacy(null!));

    [Fact]
    public void Mapper_BlankOptionalText_BecomesNull_AndPassThroughFieldsSurvive()
    {
        var created = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var row = EdgeRow();
        row.Batch = "   ";
        row.SysAlias = "\t";
        row.CreatedBy = "";
        row.ParameterJSON = "   ";
        row.ToObjectMK = 1234;
        row.CreatedDate = created;

        var def = InvokeFlowMapper.FromLegacy(row);

        Assert.Null(def.Batch);
        Assert.Null(def.SysAlias);
        Assert.Null(def.CreatedBy);
        Assert.Null(def.ParameterJson);
        Assert.Equal(1234, def.ToObjectMK);
        Assert.Equal(created, def.CreatedDate);
    }

    [Fact]
    public void Mapper_MissingAlias_ErrorNamesFlowIdAndColumn()
    {
        var row = EdgeRow();
        row.InvokeAlias = null;

        var ex = Assert.Throws<SqlFlowException>(() => InvokeFlowMapper.FromLegacy(row));
        Assert.Contains("42", ex.Message, StringComparison.Ordinal);
        Assert.Contains("InvokeAlias", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Mapper_OnErrorResume_IsCarriedVerbatim_WhenSet(bool resume)
    {
        var row = EdgeRow();
        row.OnErrorResume = resume;
        Assert.Equal(resume, InvokeFlowMapper.FromLegacy(row).OnErrorResume);
    }

    [Fact]
    public void Mapper_BlankServicePrincipalAliases_StayNull_NotEmptyReference()
    {
        var row = EdgeRow();
        row.trgServicePrincipalAlias = "   ";
        row.srcServicePrincipalAlias = null;

        var def = InvokeFlowMapper.FromLegacy(row);

        Assert.Null(def.TargetServicePrincipalReference);
        Assert.Null(def.SourceServicePrincipalReference);
    }

    // ----- InvokeParameterJson: Data Factory shape -----------------------------------------------------------

    [Fact]
    public void DataFactory_EmptyObject_YieldsEmptyDictionary()
        => Assert.Empty(InvokeParameterJson.ToDataFactoryParameters("{}"));

    [Fact]
    public void DataFactory_NullAndArrayValues_KeepRawJsonText()
    {
        var result = InvokeParameterJson.ToDataFactoryParameters("""{"missing":null,"list":[1,2,3]}""");

        Assert.Equal("null", result["missing"].ToString());
        Assert.Equal("[1,2,3]", result["list"].ToString());
    }

    [Fact]
    public void DataFactory_PropertyNamesAreCaseSensitive()
    {
        var result = InvokeParameterJson.ToDataFactoryParameters("""{"Key":1,"key":2}""");

        Assert.Equal(2, result.Count);
        Assert.Equal("1", result["Key"].ToString());
        Assert.Equal("2", result["key"].ToString());
    }

    // ----- InvokeParameterJson: Automation shape -------------------------------------------------------------

    [Theory]
    [InlineData("""{"v":null}""", "null")]
    [InlineData("""{"v":42}""", "42")]
    [InlineData("""{"v":true}""", "true")]
    [InlineData("""{"v":1.5}""", "1.5")]
    [InlineData("""{"v":[1,2]}""", "[1,2]")]
    public void Automation_NonStringValues_PassAsJsonText(string json, string expected)
        => Assert.Equal(expected, InvokeParameterJson.ToAutomationParameters(json)["v"]);

    [Fact]
    public void Automation_EmptyStringValue_StaysEmptyString()
        => Assert.Equal(string.Empty, InvokeParameterJson.ToAutomationParameters("""{"v":""}""")["v"]);

    [Fact]
    public void Automation_PropertyNamesAreCaseSensitive()
    {
        var result = InvokeParameterJson.ToAutomationParameters("""{"A":"x","a":"y"}""");

        Assert.Equal(2, result.Count);
        Assert.Equal("x", result["A"]);
        Assert.Equal("y", result["a"]);
    }

    [Fact]
    public void ParameterJson_MalformedAndNonObject_HaveDistinctMessages()
    {
        var malformed = Assert.Throws<SqlFlowException>(() => InvokeParameterJson.ToDataFactoryParameters("{oops"));
        Assert.Contains("not valid JSON", malformed.Message, StringComparison.Ordinal);

        var nonObject = Assert.Throws<SqlFlowException>(() => InvokeParameterJson.ToAutomationParameters("123"));
        Assert.Contains("must be a JSON object", nonObject.Message, StringComparison.Ordinal);
    }

    // ----- InvokeDispatcher: result shape, cancellation, selection -------------------------------------------

    [Fact]
    public async Task Dispatcher_SuccessResult_CarriesIdentityAndStreams()
    {
        var dispatcher = new InvokeDispatcher(
        [
            new EdgeExecutor(InvokeType.AzureAutomation,
                _ => new InvokeExecution { StandardOutput = "done", StandardError = "warned" }),
        ]);
        var definition = new InvokeDefinition { FlowId = 77, InvokeAlias = "nightly", InvokeType = InvokeType.AzureAutomation };

        var result = await dispatcher.DispatchAsync(definition);

        Assert.True(result.Success, result.Error);
        Assert.Equal(77, result.FlowId);
        Assert.Equal("nightly", result.InvokeAlias);
        Assert.Equal(InvokeType.AzureAutomation, result.InvokeType);
        Assert.Equal("done", result.StandardOutput);
        Assert.Equal("warned", result.StandardError);
        Assert.NotEqual(Guid.Empty, result.RunId);
        Assert.True(result.DurationSeconds >= 0);
    }

    [Fact]
    public async Task Dispatcher_FailureResult_CarriesIdentityAndNullStreams()
    {
        var dispatcher = new InvokeDispatcher([]);
        var definition = new InvokeDefinition { FlowId = 5, InvokeAlias = "x", InvokeType = InvokeType.AzureDataFactory };

        var result = await dispatcher.DispatchAsync(definition);

        Assert.False(result.Success);
        Assert.Equal(5, result.FlowId);
        Assert.Equal("x", result.InvokeAlias);
        Assert.Null(result.StandardOutput);
        Assert.Null(result.StandardError);
        Assert.NotEqual(Guid.Empty, result.RunId);
    }

    [Fact]
    public async Task Dispatcher_OperationCanceled_Propagates_NotSwallowed()
    {
        var dispatcher = new InvokeDispatcher(
        [
            new EdgeExecutor(InvokeType.AzureAutomation, _ => throw new OperationCanceledException()),
        ]);
        var definition = new InvokeDefinition { FlowId = 1, InvokeAlias = "a", InvokeType = InvokeType.AzureAutomation };

        await Assert.ThrowsAsync<OperationCanceledException>(() => dispatcher.DispatchAsync(definition));
    }

    [Fact]
    public async Task Dispatcher_FirstMatchingExecutorWins()
    {
        var dispatcher = new InvokeDispatcher(
        [
            new EdgeExecutor(InvokeType.AzureAutomation, _ => new InvokeExecution { StandardOutput = "first" }),
            new EdgeExecutor(InvokeType.AzureAutomation, _ => new InvokeExecution { StandardOutput = "second" }),
        ]);
        var definition = new InvokeDefinition { FlowId = 1, InvokeAlias = "a", InvokeType = InvokeType.AzureAutomation };

        var result = await dispatcher.DispatchAsync(definition);

        Assert.Equal("first", result.StandardOutput);
    }

    [Fact]
    public async Task Dispatcher_BothStreamsNull_IsStillSuccess()
    {
        var dispatcher = new InvokeDispatcher(
        [
            new EdgeExecutor(InvokeType.AzureAutomation, _ => new InvokeExecution()),
        ]);
        var definition = new InvokeDefinition { FlowId = 1, InvokeAlias = "a", InvokeType = InvokeType.AzureAutomation };

        var result = await dispatcher.DispatchAsync(definition);

        Assert.True(result.Success);
        Assert.Null(result.StandardOutput);
        Assert.Null(result.StandardError);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Dispatcher_NullExecutors_Throws()
        => Assert.Throws<ArgumentNullException>(() => new InvokeDispatcher(null!));

    [Fact]
    public async Task Dispatcher_NullDefinition_Throws()
    {
        var dispatcher = new InvokeDispatcher([]);
        await Assert.ThrowsAsync<ArgumentNullException>(() => dispatcher.DispatchAsync(null!));
    }

    // ----- InMemoryInvokeFlowStore: lookups, guards, batch boundaries ----------------------------------------

    [Fact]
    public async Task InvokeStore_UnknownId_Throws()
    {
        var store = new InMemoryInvokeFlowStore([EdgeInvoke("a", 1)]);

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => store.LoadByIdAsync(999));
        Assert.Contains("999", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InvokeStore_BlankAlias_ThrowsArgumentException(string? alias)
    {
        var store = new InMemoryInvokeFlowStore([EdgeInvoke("a", 1)]);
        // The null case throws ArgumentNullException, a subtype of ArgumentException, so accept the family.
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.LoadByAliasAsync(alias!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InvokeStore_BlankBatch_ThrowsArgumentException(string? batch)
    {
        var store = new InMemoryInvokeFlowStore([EdgeInvoke("a", 1, batch: "nightly")]);
        // The null case throws ArgumentNullException, a subtype of ArgumentException, so accept the family.
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.LoadBatchAsync(batch!));
    }

    [Fact]
    public async Task InvokeStore_BatchWithNoMatches_ReturnsEmpty_NotThrow()
    {
        var store = new InMemoryInvokeFlowStore([EdgeInvoke("a", 1, batch: "nightly")]);

        var flows = await store.LoadBatchAsync("does-not-exist");

        Assert.Empty(flows);
    }

    [Fact]
    public async Task InvokeStore_BatchMatchIsCaseInsensitive()
    {
        var store = new InMemoryInvokeFlowStore([EdgeInvoke("a", 1, batch: "Nightly")]);

        var flows = await store.LoadBatchAsync("nIGHTLY");

        Assert.Equal(1, Assert.Single(flows).FlowId);
    }

    [Fact]
    public void InvokeStore_NullDefinitions_Throws()
        => Assert.Throws<ArgumentNullException>(() => new InMemoryInvokeFlowStore(null!));

    [Fact]
    public void InvokeStore_EmptyBlock_ConstructsCleanly()
    {
        var store = new InMemoryInvokeFlowStore([]);
        Assert.NotNull(store);
    }

    // ----- InMemoryServicePrincipalStore: lookups, gate, duplicates ------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SpStore_BlankAlias_ThrowsArgumentException(string? alias)
    {
        var store = new InMemoryServicePrincipalStore([EdgeProfile("deploy")]);
        // The null case throws ArgumentNullException, a subtype of ArgumentException, so accept the family.
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ResolveAsync(alias!));
    }

    [Fact]
    public void SpStore_DuplicateAliasCaseInsensitive_Throws()
    {
        var ex = Assert.Throws<SqlFlowException>(() => new InMemoryServicePrincipalStore(
        [
            EdgeProfile("deploy"),
            EdgeProfile("DEPLOY"),
        ]));
        Assert.Contains("Duplicate service-principal name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpStore_NullSecretRef_IsAllowed_AmbientCredential()
    {
        var store = new InMemoryServicePrincipalStore([EdgeProfile("deploy", secretRef: null)]);

        var profile = await store.ResolveAsync("deploy");

        Assert.Null(profile.ClientSecretRef);
    }

    [Theory]
    [InlineData("${env:OPS_SECRET}")]
    [InlineData("${keyvault:vault/secret}")]
    public async Task SpStore_WholeReference_IsAccepted(string reference)
    {
        var store = new InMemoryServicePrincipalStore([EdgeProfile("deploy", secretRef: reference)]);

        var profile = await store.ResolveAsync("deploy");

        Assert.Equal(reference, profile.ClientSecretRef);
    }

    [Theory]
    [InlineData("hunter2")]
    [InlineData("prefix ${env:S}")]
    [InlineData("${env:S} suffix")]
    [InlineData("${nocolon}")]
    public void SpStore_RestingOrPartialSecret_IsRejected(string secret)
    {
        var ex = Assert.Throws<SecretlessViolationException>(() => new InMemoryServicePrincipalStore(
        [
            EdgeProfile("deploy", secretRef: secret),
        ]));
        Assert.Equal("deploy", ex.Alias);
        Assert.Equal("ClientSecretRef", ex.Column);
    }

    [Fact]
    public void SpStore_NullProfiles_Throws()
        => Assert.Throws<ArgumentNullException>(() => new InMemoryServicePrincipalStore(null!));

    // ----- NullServicePrincipalStore: lightweight-mode contract ----------------------------------------------

    [Fact]
    public async Task NullSpStore_DoesNotSupportAliases_AndResolveExplainsWhy()
    {
        Assert.False(NullServicePrincipalStore.Instance.SupportsAliases);

        var ex = await Assert.ThrowsAsync<SqlFlowException>(
            () => NullServicePrincipalStore.Instance.ResolveAsync("deploy"));
        Assert.Contains("lightweight mode", ex.Message, StringComparison.Ordinal);
    }

    // ----- YamlInvokeFlowLoader: document-level guards -------------------------------------------------------

    [Fact]
    public void Yaml_EmptyDocument_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => InvokeLoader.Parse(""));
        Assert.Contains("the document is empty", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_InvalidGrammar_FailsWithSourceLabel()
    {
        var ex = Assert.Throws<FlowValidationException>(() => InvokeLoader.Parse("invoke: [unterminated", "doc.yaml"));
        Assert.Contains("doc.yaml", ex.Message, StringComparison.Ordinal);
        Assert.Contains("invalid YAML", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_MissingFile_Fails()
    {
        var path = Path.Combine(Path.GetTempPath(), "sqlflow-invoke-missing-" + Guid.NewGuid().ToString("N") + ".yaml");
        var ex = Assert.Throws<FlowValidationException>(() => InvokeLoader.LoadFile(path));
        Assert.Contains("not found", ex.Message, StringComparison.Ordinal);
    }

    // ----- YamlInvokeFlowLoader: invoke cross-field rules ----------------------------------------------------

    [Fact]
    public void Yaml_AdfWithRunbookSet_IsRejected()
    {
        var yaml = MinimalAdf.Replace("pipeline: pl_refresh", "pipeline: pl_refresh\n  runbook: rb_x", StringComparison.Ordinal);

        var ex = Assert.Throws<FlowValidationException>(() => InvokeLoader.Parse(yaml));
        Assert.Contains("type adf runs a pipeline", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_AutWithPipelineSet_IsRejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => InvokeLoader.Parse("""
            flowType: inv
            name: nightly
            servicePrincipals:
              ops:
                subscriptionId: s
                resourceGroup: rg
                automationAccountName: aa
            invoke:
              type: aut
              runbook: rb_x
              pipeline: pl_x
              servicePrincipal: ops
            """));
        Assert.Contains("type aut runs a runbook", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_AutWithoutAutomationAccountName_IsRejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => InvokeLoader.Parse("""
            flowType: inv
            name: nightly
            servicePrincipals:
              ops:
                subscriptionId: s
                resourceGroup: rg
            invoke:
              type: aut
              runbook: rb_x
              servicePrincipal: ops
            """));
        Assert.Contains("no automationAccountName", ex.Message, StringComparison.Ordinal);
    }

    // ----- YamlInvokeFlowLoader: name validation and duplicates ----------------------------------------------

    [Theory]
    [InlineData("bad name")]
    [InlineData("bad/name")]
    [InlineData("bad:name")]
    public void Yaml_InvalidServicePrincipalName_IsRejected(string spName)
    {
        var yaml = MinimalAdf.Replace("  deploy:", $"  {spName}:", StringComparison.Ordinal)
            .Replace("servicePrincipal: deploy", $"servicePrincipal: {spName}", StringComparison.Ordinal);

        var ex = Assert.Throws<FlowValidationException>(() => InvokeLoader.Parse(yaml));
        Assert.Contains("is invalid", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_ServicePrincipalDeclaredTwice_IsRejected()
    {
        // A duplicate key collapses in YAML, so two distinct names that collide case-insensitively prove the
        // store's case-insensitive duplicate guard rather than YAML's own last-wins merge.
        var ex = Assert.Throws<FlowValidationException>(() => InvokeLoader.Parse("""
            flowType: inv
            name: dup
            servicePrincipals:
              deploy:
                subscriptionId: s
                resourceGroup: rg
                dataFactoryName: adf
              DEPLOY:
                subscriptionId: s2
                resourceGroup: rg2
                dataFactoryName: adf2
            invoke:
              type: adf
              pipeline: pl
              servicePrincipal: deploy
            """));
        Assert.Contains("declared more than once", ex.Message, StringComparison.Ordinal);
    }

    // ----- YamlInvokeFlowLoader: parameter typing edge cases -------------------------------------------------

    [Fact]
    public void Yaml_NegativeAndNullAndNestedParameters_KeepTypes()
    {
        var yaml = MinimalAdf.Replace("  servicePrincipal: deploy", """
              servicePrincipal: deploy
              parameters:
                delta: -17
                absent:
                tags: [a, b, c]
                nested:
                  flag: false
            """, StringComparison.Ordinal);

        var doc = InvokeLoader.Parse(yaml);
        using var json = JsonDocument.Parse(doc.Definition.ParameterJson!);
        var root = json.RootElement;

        Assert.Equal(-17, root.GetProperty("delta").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("absent").ValueKind);
        Assert.Equal(3, root.GetProperty("tags").GetArrayLength());
        Assert.False(root.GetProperty("nested").GetProperty("flag").GetBoolean());
    }

    [Fact]
    public void Yaml_NoParameters_LeavesParameterJsonNull()
        => Assert.Null(InvokeLoader.Parse(MinimalAdf).Definition.ParameterJson);

    // ----- YamlInvokeFlowLoader: onErrorResume precedence ----------------------------------------------------

    [Fact]
    public void Yaml_DocumentLevelOnErrorResume_OverridesInvokeLevel()
    {
        var yaml = MinimalAdf
            .Replace("name: trigger-refresh", "name: trigger-refresh\nonErrorResume: false", StringComparison.Ordinal)
            .Replace("servicePrincipal: deploy", "servicePrincipal: deploy\n  onErrorResume: true", StringComparison.Ordinal);

        Assert.False(InvokeLoader.Parse(yaml).Definition.OnErrorResume);
    }

    [Fact]
    public void Yaml_InvokeLevelOnErrorResume_AppliesWhenDocumentLevelOmitted()
    {
        var yaml = MinimalAdf.Replace(
            "servicePrincipal: deploy", "servicePrincipal: deploy\n  onErrorResume: false", StringComparison.Ordinal);

        Assert.False(InvokeLoader.Parse(yaml).Definition.OnErrorResume);
    }

    // ----- StableFlowId determinism over the invoke surface --------------------------------------------------

    [Fact]
    public void Yaml_FlowId_IsDeterministicPositive_AndNameDependent()
    {
        var first = InvokeLoader.Parse(MinimalAdf).Definition.FlowId;
        var second = InvokeLoader.Parse(MinimalAdf).Definition.FlowId;
        var renamed = InvokeLoader.Parse(
            MinimalAdf.Replace("name: trigger-refresh", "name: trigger-other", StringComparison.Ordinal)).Definition.FlowId;

        Assert.True(first > 0);
        Assert.Equal(first, second);
        Assert.NotEqual(first, renamed);
    }

    // ----- Invoke hooks across document kinds (in-memory) ----------------------------------------------------

    [Fact]
    public void Hooks_Ingestion_PreInvokeOnly_LeavesPostNull()
    {
        var doc = new YamlIngestionFlowLoader().Parse("""
            flowType: ing
            name: orders
            connections:
              sink: ${env:SINK}
            source: { server: sink, object: db.dbo.Src }
            target: { server: sink, object: db.dbo.Trg }
            preInvoke: refresh-marts
            servicePrincipals:
              deploy:
                subscriptionId: s
                resourceGroup: rg
                dataFactoryName: adf
            invokes:
              refresh-marts:
                type: adf
                pipeline: pl_refresh
                servicePrincipal: deploy
            """);

        Assert.Equal("refresh-marts", doc.Flow.Process.PreInvokeAlias);
        Assert.Null(doc.Flow.Process.PostInvokeAlias);
        Assert.Single(doc.Invokes);
    }

    [Fact]
    public void Hooks_Ingestion_MultipleInvokesDeclared_AllReachTheModel()
    {
        var doc = new YamlIngestionFlowLoader().Parse("""
            flowType: ing
            name: orders
            connections:
              sink: ${env:SINK}
            source: { server: sink, object: db.dbo.Src }
            target: { server: sink, object: db.dbo.Trg }
            preInvoke: warm-cache
            postInvoke: refresh-marts
            servicePrincipals:
              deploy:
                subscriptionId: s
                resourceGroup: rg
                dataFactoryName: adf
                automationAccountName: aa
            invokes:
              warm-cache:
                type: aut
                runbook: rb_warm
                servicePrincipal: deploy
              refresh-marts:
                type: adf
                pipeline: pl_refresh
                servicePrincipal: deploy
            """);

        Assert.Equal("warm-cache", doc.Flow.Process.PreInvokeAlias);
        Assert.Equal("refresh-marts", doc.Flow.Process.PostInvokeAlias);
        Assert.Equal(2, doc.Invokes.Count);
    }

    [Fact]
    public void Hooks_DuplicateInvokeNameInBlock_IsRejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => new YamlIngestionFlowLoader().Parse("""
            flowType: ing
            name: orders
            connections:
              sink: ${env:SINK}
            source: { server: sink, object: db.dbo.Src }
            target: { server: sink, object: db.dbo.Trg }
            servicePrincipals:
              deploy:
                subscriptionId: s
                resourceGroup: rg
                dataFactoryName: adf
            invokes:
              Refresh:
                type: adf
                pipeline: pl_a
                servicePrincipal: deploy
              REFRESH:
                type: adf
                pipeline: pl_b
                servicePrincipal: deploy
            """));
        Assert.Contains("declared more than once", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Hooks_PreInvokeReferenceIsCaseInsensitive()
    {
        var doc = new YamlIngestionFlowLoader().Parse("""
            flowType: ing
            name: orders
            connections:
              sink: ${env:SINK}
            source: { server: sink, object: db.dbo.Src }
            target: { server: sink, object: db.dbo.Trg }
            preInvoke: REFRESH-MARTS
            servicePrincipals:
              deploy:
                subscriptionId: s
                resourceGroup: rg
                dataFactoryName: adf
            invokes:
              refresh-marts:
                type: adf
                pipeline: pl_refresh
                servicePrincipal: deploy
            """);

        // The reference is resolved case-insensitively, but the original-cased reference text is kept.
        Assert.Equal("REFRESH-MARTS", doc.Flow.Process.PreInvokeAlias);
    }

    /// <summary>A test double that handles exactly one invoke type and runs a supplied function, so the
    /// dispatcher's routing, timing, and failure contract can be exercised without an Azure call. Uniquely named
    /// to avoid colliding with the FakeExecutor in InvokeDispatcherTests.</summary>
    private sealed class EdgeExecutor : IInvokeExecutor
    {
        private readonly InvokeType _type;
        private readonly Func<InvokeDefinition, InvokeExecution> _run;

        public EdgeExecutor(InvokeType type, Func<InvokeDefinition, InvokeExecution> run)
        {
            _type = type;
            _run = run;
        }

        public bool CanHandle(InvokeType type) => type == _type;

        public Task<InvokeExecution> ExecuteAsync(InvokeDefinition definition, CancellationToken ct = default)
            => Task.FromResult(_run(definition));
    }
}
