using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Core.Invoke;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>The standalone invoke YAML surface (flowType: inv): the document maps to an InvokeDefinition with
/// the documented defaults, parameter scalars keep their YAML types into the parameter JSON, the secretless
/// gate rejects a pasted secret at parse time, and every cross-field requirement fails with the YAML path.</summary>
public sealed class YamlInvokeFlowLoaderTests
{
    private static readonly YamlInvokeFlowLoader Loader = new();

    private const string Minimal = """
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

    [Fact]
    public void Minimal_MapsWithDefaults()
    {
        var doc = Loader.Parse(Minimal);
        var definition = doc.Definition;

        Assert.Equal("trigger-refresh", definition.InvokeAlias);
        Assert.Equal("trigger-refresh", definition.SysAlias);
        Assert.True(definition.FlowId > 0);
        Assert.Equal(InvokeType.AzureDataFactory, definition.InvokeType);
        Assert.Equal("pl_refresh", definition.PipelineName);
        Assert.Null(definition.RunbookName);
        Assert.Equal("@deploy", definition.TargetServicePrincipalReference);
        Assert.Null(definition.ParameterJson);
        Assert.True(definition.OnErrorResume);
        Assert.Equal("adf", definition.FlowType);

        var sp = Assert.Single(doc.ServicePrincipals);
        Assert.Equal("deploy", sp.Alias);
        Assert.Equal("rg-data", sp.ResourceGroup);
        Assert.Equal("adf-prod", sp.DataFactoryName);
        Assert.Null(sp.ClientSecretRef);   // ambient Azure credential
    }

    [Fact]
    public void Automation_MapsRunbook_AndSecretReference()
    {
        var doc = Loader.Parse("""
            flowType: inv
            name: nightly-runbook
            batch: nightly
            servicePrincipals:
              ops:
                tenantId: t-1
                clientId: c-1
                clientSecret: ${env:OPS_SP_SECRET}
                subscriptionId: s-1
                resourceGroup: rg-ops
                automationAccountName: aa-ops
            invoke:
              type: aut
              runbook: rb_cleanup
              servicePrincipal: ops
              onErrorResume: false
            """);

        Assert.Equal(InvokeType.AzureAutomation, doc.Definition.InvokeType);
        Assert.Equal("rb_cleanup", doc.Definition.RunbookName);
        Assert.Equal("nightly", doc.Definition.Batch);
        Assert.False(doc.Definition.OnErrorResume);
        Assert.Equal("${env:OPS_SP_SECRET}", doc.ServicePrincipals.Single().ClientSecretRef);
    }

    [Fact]
    public void Parameters_KeepTheirYamlTypes_IntoTheJson()
    {
        var doc = Loader.Parse(Minimal.Replace("  servicePrincipal: deploy", """
              servicePrincipal: deploy
              parameters:
                env: prod
                fullLoad: true
                batchSize: 5000
                ratio: 0.25
                forcedString: "true"
                window:
                  from: 2024-01-01
                  days: 7
                regions: [nor, swe]
            """, StringComparison.Ordinal));

        using var json = JsonDocument.Parse(doc.Definition.ParameterJson!);
        var root = json.RootElement;
        Assert.Equal("prod", root.GetProperty("env").GetString());
        Assert.True(root.GetProperty("fullLoad").GetBoolean());                  // plain scalar stays a boolean
        Assert.Equal(5000, root.GetProperty("batchSize").GetInt32());            // plain scalar stays a number
        Assert.Equal(0.25, root.GetProperty("ratio").GetDouble());
        Assert.Equal("true", root.GetProperty("forcedString").GetString());      // quoting forces a string
        Assert.Equal("2024-01-01", root.GetProperty("window").GetProperty("from").GetString());
        Assert.Equal(7, root.GetProperty("window").GetProperty("days").GetInt32());
        Assert.Equal(2, root.GetProperty("regions").GetArrayLength());
    }

    [Fact]
    public void TypeOmitted_DefaultsToAutomation_MirroringTheLegacyColumnDefault()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: inv
            name: x
            servicePrincipals:
              ops: { subscriptionId: s, resourceGroup: rg, automationAccountName: aa }
            invoke:
              pipeline: pl_x
              servicePrincipal: ops
            """));

        // No type means aut (the legacy column default), so the runbook is what is missing.
        Assert.Contains("'invoke.runbook' is required when type is aut.", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("name: trigger-refresh", "name: ''", "'name' is required for an invoke flow.")]
    [InlineData("pipeline: pl_refresh", "runbook: rb_x", "'invoke.pipeline' is required when type is adf.")]
    [InlineData("servicePrincipal: deploy", "servicePrincipal: ''", "'invoke.servicePrincipal' is required")]
    [InlineData("servicePrincipal: deploy", "servicePrincipal: ghost", "references 'ghost', which is not declared under 'servicePrincipals:'")]
    [InlineData("subscriptionId: 00000000-0000-0000-0000-000000000001", "subscriptionId: ''", "'servicePrincipals.deploy.subscriptionId' is required.")]
    [InlineData("resourceGroup: rg-data", "resourceGroup: ''", "'servicePrincipals.deploy.resourceGroup' is required.")]
    [InlineData("dataFactoryName: adf-prod", "automationAccountName: aa-x", "is an adf invoke, but service principal 'deploy' has no dataFactoryName")]
    [InlineData("type: adf", "type: ps", "removed as a code-injection risk")]
    [InlineData("type: adf", "type: ssis", "Unknown InvokeType 'ssis'")]
    public void Requirements_FailWithThePath(string find, string replace, string expectedError)
    {
        var yaml = Minimal.Replace(find, replace, StringComparison.Ordinal);

        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains(expectedError, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingInvokeBlock_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("flowType: inv\nname: x\n"));
        Assert.Contains("'invoke' is required.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PastedSecret_IsRejectedAtParseTime()
    {
        var yaml = Minimal.Replace("dataFactoryName: adf-prod", """
            dataFactoryName: adf-prod
                tenantId: t-1
                clientId: c-1
                clientSecret: hunter2-not-a-reference
            """, StringComparison.Ordinal);

        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains("'servicePrincipals.deploy.clientSecret' must be a whole ${env:NAME} or ${keyvault:vault/secret} reference",
            ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SecretWithoutTenantAndClient_Fails()
    {
        var yaml = Minimal.Replace("dataFactoryName: adf-prod",
            "dataFactoryName: adf-prod\n    clientSecret: ${env:S}", StringComparison.Ordinal);

        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains("sets clientSecret, so tenantId and clientId are required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_MapsFolderGlobAndMask()
    {
        var doc = Loader.Parse(Minimal.Replace("  servicePrincipal: deploy", """
              servicePrincipal: deploy
              output:
                location: ./raw/orders
                srcFile: orders_*.csv
                srcPathMask: .*/orders/.*
            """, StringComparison.Ordinal));

        var output = Assert.Single(doc.Definition.Outputs);
        Assert.Equal("./raw/orders", output.Location);
        Assert.Equal("orders_*.csv", output.SrcFile);
        Assert.Equal(".*/orders/.*", output.SrcPathMask);
    }

    [Fact]
    public void Outputs_ListBinds_EveryDropInOrder()
    {
        var doc = Loader.Parse(Minimal.Replace("  servicePrincipal: deploy", """
              servicePrincipal: deploy
              outputs:
                - { location: ./raw/orders, srcFile: orders_*.csv }
                - { location: ./raw/invoices, srcFile: inv_*.csv }
            """, StringComparison.Ordinal));

        Assert.Collection(doc.Definition.Outputs,
            o => Assert.Equal("./raw/orders", o.Location),
            o => Assert.Equal("./raw/invoices", o.Location));
    }

    [Fact]
    public void Output_And_Outputs_Combine_SingularFirst()
    {
        var doc = Loader.Parse(Minimal.Replace("  servicePrincipal: deploy", """
              servicePrincipal: deploy
              output: { location: ./raw/orders }
              outputs:
                - { location: ./raw/invoices }
            """, StringComparison.Ordinal));

        Assert.Equal(["./raw/orders", "./raw/invoices"], doc.Definition.Outputs.Select(o => o.Location));
    }

    [Fact]
    public void Output_NoBlock_IsEmpty() => Assert.Empty(Loader.Parse(Minimal).Definition.Outputs);

    [Fact]
    public void Outputs_EntryWithoutLocation_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(Minimal.Replace("  servicePrincipal: deploy", """
              servicePrincipal: deploy
              outputs:
                - { srcFile: orders_*.csv }
            """, StringComparison.Ordinal)));
        Assert.Contains("'invoke.outputs[0].location' is required when a flow declares an output.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_WithoutLocation_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(Minimal.Replace("  servicePrincipal: deploy", """
              servicePrincipal: deploy
              output:
                srcFile: orders_*.csv
            """, StringComparison.Ordinal)));
        Assert.Contains("'invoke.output.location' is required when a flow declares an output.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_InvalidPathMask_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(Minimal.Replace("  servicePrincipal: deploy", """
              servicePrincipal: deploy
              output:
                location: ./raw/orders
                srcPathMask: "[unterminated"
            """, StringComparison.Ordinal)));
        Assert.Contains("'invoke.output.srcPathMask' is not a valid regular expression.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentLoader_DispatchesInv_AndNamesItInTheUnknownKindError()
    {
        var documents = new YamlDocumentLoader(
            new YamlFlowLoader(), new YamlIngestionFlowLoader(), new YamlExportFlowLoader(),
            new YamlStoredProcedureFlowLoader(), new YamlInvokeFlowLoader(), new YamlHealthCheckFlowLoader(), new YamlSourceControlFlowLoader(), new YamlBatchFlowLoader(), new YamlAcquireFlowLoader(), new YamlCopyFlowLoader(), new YamlSftpFlowLoader(), new YamlCalendarFlowLoader(), new YamlTranslateFlowLoader());

        var doc = Assert.IsType<InvokeFlowDocument>(documents.Parse(Minimal));
        Assert.Equal("trigger-refresh", doc.Document.Definition.InvokeAlias);

        var ex = Assert.Throws<FlowValidationException>(() => documents.Parse("flowType: bogus"));
        Assert.Contains("'inv' for an ADF/Automation trigger", ex.Message, StringComparison.Ordinal);
    }
}
