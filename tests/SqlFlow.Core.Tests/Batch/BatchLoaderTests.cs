using SqlFlow.Core;
using SqlFlow.Core.Batch;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests.Batch;

/// <summary>The flowType: batch document contract: a valid document maps to a <see cref="BatchFlow"/>, the
/// document loader dispatches on it, and the failure/concurrency knobs parse with sane defaults and clear
/// errors.</summary>
public sealed class BatchLoaderTests
{
    private static YamlBatchFlowLoader Loader() => new();

    [Fact]
    public void Parse_MapsTheWholeDocument()
    {
        var doc = Loader().Parse("""
            flowType: batch
            name: nightly
            description: the nightly load
            members:
              include:
                - "raw/*.flow.yaml"
                - "dim/*.flow.yaml"
              exclude:
                - "raw/scratch.flow.yaml"
              inactive:
                - "dim/legacy.flow.yaml"
            ignoreErrors:
              - "optional/*.flow.yaml"
            onError: continue
            maxParallel: 4
            connect: always
            """);

        Assert.Equal("nightly", doc.Flow.SysAlias);
        Assert.Equal(["raw/*.flow.yaml", "dim/*.flow.yaml"], doc.Flow.Include);
        Assert.Equal(["raw/scratch.flow.yaml"], doc.Flow.Exclude);
        Assert.Equal(["dim/legacy.flow.yaml"], doc.Flow.Inactive);
        Assert.Equal(["optional/*.flow.yaml"], doc.Flow.IgnoreErrors);
        Assert.Equal(BatchErrorMode.Continue, doc.Flow.OnError);
        Assert.Equal(4, doc.Flow.MaxParallel);
        Assert.Equal(BatchConnectMode.Always, doc.Flow.Connect);
    }

    [Fact]
    public void Defaults_AreApplied()
    {
        var doc = Loader().Parse("""
            flowType: batch
            name: minimal
            members:
              include:
                - "*.flow.yaml"
            """);

        Assert.Equal(BatchErrorMode.Stop, doc.Flow.OnError);
        Assert.Equal(0, doc.Flow.MaxParallel);
        Assert.Equal(BatchConnectMode.Auto, doc.Flow.Connect);
        Assert.Empty(doc.Flow.Exclude);
    }

    [Fact]
    public void DocumentLoader_DispatchesOnFlowType()
    {
        var documents = new YamlDocumentLoader(
            new YamlFlowLoader(), new YamlIngestionFlowLoader(), new YamlExportFlowLoader(),
            new YamlStoredProcedureFlowLoader(), new YamlInvokeFlowLoader(), new YamlHealthCheckFlowLoader(),
            new YamlSourceControlFlowLoader(), new YamlBatchFlowLoader(), new YamlAcquireFlowLoader(), new YamlCopyFlowLoader(), new YamlSftpFlowLoader(), new YamlCalendarFlowLoader(), new YamlTranslateFlowLoader());

        var doc = Assert.IsType<BatchFlowDocument>(documents.Parse("""
            flowType: batch
            name: nightly
            members:
              include: ["*.flow.yaml"]
            """));
        Assert.Equal("nightly", doc.Document.Flow.SysAlias);
    }

    [Fact]
    public void MissingInclude_IsRejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader().Parse("""
            flowType: batch
            name: bad
            """));
        Assert.Contains("members.include", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownOnError_IsRejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader().Parse("""
            flowType: batch
            name: bad
            members:
              include: ["*.flow.yaml"]
            onError: halt
            """));
        Assert.Contains("onError", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeMaxParallel_IsRejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader().Parse("""
            flowType: batch
            name: bad
            members:
              include: ["*.flow.yaml"]
            maxParallel: -1
            """));
        Assert.Contains("maxParallel", ex.Message, StringComparison.Ordinal);
    }
}
