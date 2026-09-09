using SqlFlow.Core.Runs;
using SqlFlow.Lineage.Collection;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The document-envelope <c>mode:</c> key: every flow kind can declare <c>mode: manual</c> to opt out of
/// automatic execution (schedule fires and Node-scope descendant expansion, which key on the pipeline row's
/// ExecutionMode), while remaining runnable by a direct trigger. The envelope parse is the single vocabulary;
/// the header projection is what carries it onto the pipeline row at sync, so both are pinned here for the
/// kinds a deactivated source actually uses (file, ing, cpy, api).
/// </summary>
public sealed class DocumentModeTests
{
    private static YamlDocumentLoader Loader() => new(
        new YamlFlowLoader(), new YamlIngestionFlowLoader(), new YamlExportFlowLoader(),
        new YamlStoredProcedureFlowLoader(), new YamlInvokeFlowLoader(), new YamlHealthCheckFlowLoader(),
        new YamlSourceControlFlowLoader(), new YamlBatchFlowLoader(), new YamlAcquireFlowLoader(),
        new YamlCopyFlowLoader(), new YamlSftpFlowLoader(), new YamlCalendarFlowLoader(), new YamlTranslateFlowLoader());

    private const string FileFlowYaml = """
        name: t_file
        mode: manual
        source:
          type: csv
          location: /data/x.csv
        target:
          connection: ${env:CS}
          schema: pre
          table: T
        """;

    private const string IngestionYaml = """
        flowType: ing
        name: t_ing
        mode: manual
        connections:
          pre: ${env:PRE}
          ods: ${env:ODS}
        source:
          server: pre
          object: "[db].[pre].[v_T]"
        target:
          server: ods
          object: "[db].[arc].[T]"
        """;

    private const string CopyYaml = """
        flowType: cpy
        name: t_cpy
        mode: manual
        source:
          location: /data/in
        target:
          location: /data/out
        """;

    [Fact]
    public void FileFlow_CarriesManualMode_OnDocumentAndHeader()
    {
        var doc = Loader().Parse(FileFlowYaml);
        Assert.Equal(ExecutionMode.Manual, doc.Mode);

        var header = Assert.Single(FlowDocumentHeaders.Project(doc));
        Assert.Equal(ExecutionMode.Manual, header.Mode);
    }

    [Fact]
    public void IngestionFlow_CarriesManualMode_OnDocumentAndHeader()
    {
        var doc = Loader().Parse(IngestionYaml);
        Assert.Equal(ExecutionMode.Manual, doc.Mode);

        var header = Assert.Single(FlowDocumentHeaders.Project(doc));
        Assert.Equal(ExecutionMode.Manual, header.Mode);
    }

    [Fact]
    public void CopyFlow_CarriesManualMode_OnDocumentAndHeader()
    {
        var doc = Loader().Parse(CopyYaml);
        Assert.Equal(ExecutionMode.Manual, doc.Mode);

        var header = Assert.Single(FlowDocumentHeaders.Project(doc));
        Assert.Equal(ExecutionMode.Manual, header.Mode);
    }

    [Fact]
    public void AbsentMode_DefaultsToAuto()
    {
        var doc = Loader().Parse(FileFlowYaml.Replace("mode: manual\n", string.Empty, StringComparison.Ordinal));
        Assert.Equal(ExecutionMode.Auto, doc.Mode);
        Assert.Equal(ExecutionMode.Auto, Assert.Single(FlowDocumentHeaders.Project(doc)).Mode);
    }

    [Fact]
    public void DisabledMode_CarriesThrough_MarkingADeactivatedPipeline()
    {
        var doc = Loader().Parse(IngestionYaml.Replace("mode: manual", "mode: disabled", StringComparison.Ordinal));
        Assert.Equal(ExecutionMode.Disabled, doc.Mode);
        Assert.Equal(ExecutionMode.Disabled, Assert.Single(FlowDocumentHeaders.Project(doc)).Mode);
    }

    [Fact]
    public void UnknownMode_IsRejected()
    {
        var ex = Assert.Throws<SqlFlow.Core.FlowValidationException>(
            () => Loader().Parse(FileFlowYaml.Replace("mode: manual", "mode: paused", StringComparison.Ordinal)));
        Assert.Contains("auto, manual, disabled", ex.Message, StringComparison.Ordinal);
    }
}
