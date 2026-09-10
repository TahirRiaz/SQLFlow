using SqlFlow.Core;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests.SourceControl;

/// <summary>
/// The flowType: scm document contract: a valid document maps to a <see cref="SourceControlDocument"/>, the
/// document loader dispatches on the flowType key, and the secretless guard rejects a literal git credential or
/// a foreign-provider source at parse time (never deep in a run).
/// </summary>
public sealed class SourceControlLoaderTests
{
    private const string Valid = """
        flowType: scm
        name: warehouse-scm
        description: nightly schema snapshot
        batch: schema-history
        connections:
          DW:
        source:
          server: DW
          database: Warehouse
        repository:
          path: ./scm/warehouse
          remote: ${env:SCM_REMOTE}
          branch: release
          username: ${env:SCM_USER}
          secret: ${env:SCM_TOKEN}
          author:
            name: SQLFlow Bot
            email: bot@sqlflow.io
        scripting:
          data:
            - dbo.Config
            - "[ref].[Calendar]"
          exclude:
            - SecurityPolicy
        """;

    private static YamlSourceControlFlowLoader Loader() => new();

    [Fact]
    public void Parse_MapsTheWholeDocument()
    {
        var doc = Loader().Parse(Valid);

        Assert.Equal("warehouse-scm", doc.Flow.SysAlias);
        Assert.Equal("nightly schema snapshot", doc.Flow.Description);
        Assert.Equal("schema-history", doc.Flow.Batch);
        Assert.Equal("DW", doc.Flow.Server);
        Assert.Equal("@DW", doc.Flow.ConnectionReference);
        Assert.Equal("Warehouse", doc.Flow.Database);

        Assert.Equal("./scm/warehouse", doc.Flow.Repository.WorkingDirectory);
        Assert.Equal("${env:SCM_REMOTE}", doc.Flow.Repository.Remote);
        Assert.Equal("release", doc.Flow.Repository.Branch);
        Assert.Equal("${env:SCM_USER}", doc.Flow.Repository.Username);
        Assert.Equal("${env:SCM_TOKEN}", doc.Flow.Repository.Secret);
        Assert.Equal("SQLFlow Bot", doc.Flow.Repository.AuthorName);
        Assert.Equal("bot@sqlflow.io", doc.Flow.Repository.AuthorEmail);

        // Data-table names normalize to schema.table, brackets stripped.
        Assert.Equal(["dbo.Config", "ref.Calendar"], doc.Flow.Scripting.DataTables);
        Assert.Equal(["SecurityPolicy"], doc.Flow.Scripting.ExcludeTypes);

        Assert.Single(doc.Connections);
        Assert.Equal("DW", doc.Connections[0].Alias);
    }

    [Fact]
    public void Defaults_AreApplied_ForAMinimalDocument()
    {
        var doc = Loader().Parse("""
            flowType: scm
            name: local-snapshot
            connections:
              DW:
            source:
              server: DW
            repository:
              path: ./snapshot
            """);

        Assert.Null(doc.Flow.Database);
        Assert.Null(doc.Flow.Batch);
        Assert.Null(doc.Flow.Repository.Remote);
        Assert.Equal("main", doc.Flow.Repository.Branch);
        Assert.Equal("SQLFlow", doc.Flow.Repository.AuthorName);
        Assert.Empty(doc.Flow.Scripting.DataTables);
        Assert.Empty(doc.Flow.Scripting.IncludeTypes);
    }

    [Fact]
    public void DocumentLoader_DispatchesOnFlowType()
    {
        var documents = new YamlDocumentLoader(
            new YamlFlowLoader(), new YamlIngestionFlowLoader(), new YamlExportFlowLoader(),
            new YamlStoredProcedureFlowLoader(), new YamlInvokeFlowLoader(), new YamlHealthCheckFlowLoader(),
            new YamlSourceControlFlowLoader(), new YamlBatchFlowLoader(), new YamlAcquireFlowLoader(), new YamlCopyFlowLoader(), new YamlSftpFlowLoader(), new YamlCalendarFlowLoader(), new YamlTranslateFlowLoader());

        var doc = Assert.IsType<SourceControlFlowDocument>(documents.Parse(Valid));
        Assert.Equal("warehouse-scm", doc.Document.Flow.SysAlias);
    }

    [Fact]
    public void LiteralSecret_IsRejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader().Parse("""
            flowType: scm
            name: bad
            connections:
              DW:
            source:
              server: DW
            repository:
              path: ./r
              remote: ${env:R}
              secret: hunter2
            """));
        Assert.Contains("repository.secret", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteWithoutSecret_IsRejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader().Parse("""
            flowType: scm
            name: bad
            connections:
              DW:
            source:
              server: DW
            repository:
              path: ./r
              remote: ${env:R}
            """));
        Assert.Contains("repository.secret", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownObjectType_IsRejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader().Parse("""
            flowType: scm
            name: bad
            connections:
              DW:
            source:
              server: DW
            repository:
              path: ./r
            scripting:
              include:
                - Tables
            """));
        Assert.Contains("unknown object type", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForeignProviderSource_IsRejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader().Parse("""
            flowType: scm
            name: bad
            connections:
              PG:
                provider: postgres
                connection: ${env:PG}
            source:
              server: PG
            repository:
              path: ./r
            """));
        Assert.Contains("must be SQL Server", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRepository_IsRejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader().Parse("""
            flowType: scm
            name: bad
            connections:
              DW:
            source:
              server: DW
            """));
        Assert.Contains("repository", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
