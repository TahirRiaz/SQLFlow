using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>The stored-procedure YAML surface (flowType: sp): the smallest document maps with the documented
/// defaults, the procedure is a strict three-part name, and a foreign-provider server fails at parse time.</summary>
public sealed class YamlStoredProcedureFlowLoaderTests
{
    private static readonly YamlStoredProcedureFlowLoader Loader = new();

    private const string Minimal = """
        flowType: sp
        name: refresh-marts
        connections:
          dwh: ${env:DWH}
        procedure:
          server: dwh
          object: DW.dbo.usp_RefreshMarts
        """;

    [Fact]
    public void Minimal_MapsWithDefaults()
    {
        var doc = Loader.Parse(Minimal);
        var flow = doc.Flow;

        Assert.Equal("refresh-marts", flow.SysAlias);
        Assert.True(flow.FlowId > 0);
        Assert.Equal("dwh", flow.Server);
        Assert.Equal("@dwh", flow.ConnectionReference);
        Assert.Equal("DW", flow.Procedure.Database);
        Assert.Equal("dbo", flow.Procedure.Schema);
        Assert.Equal("usp_RefreshMarts", flow.Procedure.Name);
        Assert.Equal("[DW].[dbo].[usp_RefreshMarts]", flow.Procedure.QualifiedName);
        Assert.True(flow.OnErrorResume);
        Assert.Null(flow.Batch);
        Assert.Null(flow.Description);
        Assert.Equal("sp", flow.FlowType);

        var connection = Assert.Single(doc.Connections);
        Assert.Equal("dwh", connection.Alias);
        Assert.Equal(DataSourceKind.MSSQL, connection.Kind);
        Assert.Equal(CredentialMode.InlineConnectionString, connection.Credential.Mode);
    }

    [Fact]
    public void FullDocument_MapsEveryField()
    {
        var doc = Loader.Parse("""
            flowType: sp
            name: nightly-refresh
            description: Rebuilds the reporting marts.
            batch: nightly
            connections:
              dwh: ${env:DWH}
            procedure:
              server: dwh
              object: DW.dbo.usp_Nightly
            onErrorResume: false
            """);
        var flow = doc.Flow;

        Assert.Equal("nightly-refresh", flow.SysAlias);
        Assert.Equal("Rebuilds the reporting marts.", flow.Description);
        Assert.Equal("nightly", flow.Batch);
        Assert.False(flow.OnErrorResume);
    }

    [Fact]
    public void InlineConnection_SynthesizesTheTargetName()
    {
        var doc = Loader.Parse("""
            flowType: sp
            name: inline-proc
            procedure:
              connection: ${env:DWH}
              object: DW.dbo.usp_X
            """);

        Assert.Equal("target", doc.Flow.Server);
        var connection = Assert.Single(doc.Connections);
        Assert.Equal("target", connection.Alias);
    }

    [Theory]
    [InlineData("name: refresh-marts", "name: ''", "'name' is required for a stored-procedure flow.")]
    [InlineData("object: DW.dbo.usp_RefreshMarts", "object: ''", "'procedure.object' is required (a three-part name like Database.Schema.Procedure).")]
    [InlineData("object: DW.dbo.usp_RefreshMarts", "object: dbo.usp_RefreshMarts", "'procedure.object'")]
    public void MissingRequirements_FailWithThePath(string find, string replace, string expectedError)
    {
        var yaml = Minimal.Replace(find, replace, StringComparison.Ordinal);

        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains(expectedError, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingProcedureBlock_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("flowType: sp\nname: x\n"));
        Assert.Contains("'procedure' is required.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForeignProviderServer_IsRejectedAtParseTime()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: sp
            name: bad-proc
            connections:
              shop:
                provider: postgres
                connection: ${env:SHOP}
            procedure:
              server: shop
              object: db.public.refresh
            """));

        Assert.Contains("the procedure connection 'shop' is 'PostgreSQL'; a stored-procedure flow's server must be SQL Server (mssql or azdb)",
            ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentLoader_DispatchesSp()
    {
        var documents = new YamlDocumentLoader(
            new YamlFlowLoader(), new YamlIngestionFlowLoader(), new YamlExportFlowLoader(), new YamlStoredProcedureFlowLoader(), new YamlInvokeFlowLoader(), new YamlHealthCheckFlowLoader(), new YamlSourceControlFlowLoader(), new YamlBatchFlowLoader(), new YamlAcquireFlowLoader(), new YamlCopyFlowLoader(), new YamlSftpFlowLoader(), new YamlCalendarFlowLoader(), new YamlTranslateFlowLoader());

        var doc = Assert.IsType<StoredProcedureFlowDocument>(documents.Parse(Minimal));
        Assert.Equal("refresh-marts", doc.Document.Flow.SysAlias);
    }
}
