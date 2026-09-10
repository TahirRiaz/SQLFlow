using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Connections;
using Xunit;

namespace SqlFlow.Tests.Catalog;

/// <summary>
/// The compute-task contract's trust boundary: the payload validation that runs at BOTH ends (the control-plane
/// endpoint before enqueueing, and the worker after deserializing the queue row), plus the JSON round trip that
/// carries it through the queue. Every rejection here is one class of doomed or dangerous task that never
/// reaches a worker: unknown operations, inline connection strings, oversized/creative arguments, and
/// operations missing their required scope.
/// </summary>
public sealed class ComputeTaskPayloadTests
{
    private static ComputeTaskPayload Valid(string operation = ComputeOperations.ListObjects) => new()
    {
        Operation = operation,
        SourceRef = "${env:SQLFLOW_SOURCE}",
    };

    [Theory]
    [InlineData(ComputeOperations.TestConnection)]
    [InlineData(ComputeOperations.ListDatabases)]
    [InlineData(ComputeOperations.ListSchemas)]
    [InlineData(ComputeOperations.ListObjects)]
    public void Validate_AcceptsEveryBrowseOperation_WithMinimalArguments(string operation)
    {
        Valid(operation).Validate();
    }

    [Fact]
    public void Validate_RejectsUnknownOperation_NamingTheValidSet()
    {
        var ex = Assert.Throws<SqlFlowException>(() => Valid("dropDatabase").Validate());
        Assert.Contains("dropDatabase", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ComputeOperations.ListObjects, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    // A raw connection string can never travel through the ad-hoc queue, secretless or not.
    [InlineData("Server=prod;Database=dw;Trusted_Connection=True")]
    // A hybrid (reference plus literal fragments) is a literal, exactly like the lineage identity rule.
    [InlineData("${env:HOST};Password=hunter2")]
    // The hashed identity of an inline literal cannot be resolved back into a connection.
    [InlineData("inline:a1b2c3d4")]
    // An @alias with separators is not a single alias token.
    [InlineData("@alias;extra=1")]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsAnythingButAWholeReference(string sourceRef)
    {
        Assert.Throws<SqlFlowException>(() => (Valid() with { SourceRef = sourceRef }).Validate());
    }

    [Theory]
    [InlineData("${env:SQLFLOW_SOURCE}")]
    [InlineData("${keyvault:vault/dw-connection}")]
    [InlineData("@warehouse")]
    public void Validate_AcceptsWholeReferences(string sourceRef)
    {
        (Valid() with { SourceRef = sourceRef }).Validate();
    }

    [Fact]
    public void Validate_RejectsSearchWithoutATerm()
    {
        Assert.Throws<SqlFlowException>(() => Valid(ComputeOperations.SearchObjects).Validate());
        (Valid(ComputeOperations.SearchObjects) with { SearchTerm = "customer" }).Validate();
    }

    [Fact]
    public void Validate_RejectsIntrospectAndDetect_WithoutSchemaAndObject()
    {
        Assert.Throws<SqlFlowException>(() => Valid(ComputeOperations.IntrospectObject).Validate());
        Assert.Throws<SqlFlowException>(
            () => (Valid(ComputeOperations.IntrospectObject) with { Schema = "dbo" }).Validate());
        (Valid(ComputeOperations.IntrospectObject) with { Schema = "dbo", ObjectName = "Orders" }).Validate();
    }

    [Fact]
    public void Validate_RejectsDetectUniqueKey_OnNonSqlServerKinds()
    {
        var payload = Valid(ComputeOperations.DetectUniqueKey) with { Schema = "dbo", ObjectName = "Orders" };
        payload.Validate();
        (payload with { ProviderKind = DataSourceKind.AZDB }).Validate();
        Assert.Throws<SqlFlowException>(() => (payload with { ProviderKind = DataSourceKind.MySQL }).Validate());
        Assert.Throws<SqlFlowException>(() => (payload with { ProviderKind = DataSourceKind.PostgreSQL }).Validate());
        Assert.Throws<SqlFlowException>(() => (payload with { ProviderKind = DataSourceKind.Oracle }).Validate());
    }

    [Theory]
    [InlineData(ComputeOperations.MissingIndexes)]
    [InlineData(ComputeOperations.StatisticsHealth)]
    [InlineData(ComputeOperations.IndexUsage)]
    [InlineData(ComputeOperations.TopQueries)]
    public void Validate_AcceptsWarehouseHealthOperations_OnSqlServerKindsOnly(string operation)
    {
        var payload = Valid(operation);
        payload.Validate();
        (payload with { ProviderKind = DataSourceKind.MSSQL }).Validate();
        (payload with { ProviderKind = DataSourceKind.AZDB }).Validate();
        (payload with { Database = "dw", Limit = 50 }).Validate();
        Assert.Throws<SqlFlowException>(() => (payload with { ProviderKind = DataSourceKind.MySQL }).Validate());
        Assert.Throws<SqlFlowException>(() => (payload with { ProviderKind = DataSourceKind.PostgreSQL }).Validate());
        Assert.Throws<SqlFlowException>(() => (payload with { ProviderKind = DataSourceKind.Oracle }).Validate());
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(500)] // between 1 and 999: too small to be a meaningful sample
    [InlineData(20_000_000)]
    public void Validate_RejectsUnusableSampleSizes(int sampleSize)
    {
        var payload = Valid(ComputeOperations.DetectUniqueKey) with
        {
            Schema = "dbo", ObjectName = "Orders", SampleSize = sampleSize,
        };
        Assert.Throws<SqlFlowException>(() => payload.Validate());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)] // explicit full scan
    [InlineData(100_000)]
    public void Validate_AcceptsUsableSampleSizes(int? sampleSize)
    {
        (Valid(ComputeOperations.DetectUniqueKey) with
        {
            Schema = "dbo", ObjectName = "Orders", SampleSize = sampleSize,
        }).Validate();
    }

    [Fact]
    public void Validate_BoundsPaging()
    {
        Assert.Throws<SqlFlowException>(() => (Valid() with { Offset = -1 }).Validate());
        Assert.Throws<SqlFlowException>(() => (Valid() with { Limit = 0 }).Validate());
        Assert.Throws<SqlFlowException>(() => (Valid() with { Limit = ComputeTaskPayload.MaxLimit + 1 }).Validate());
        (Valid() with { Offset = 100, Limit = ComputeTaskPayload.MaxLimit }).Validate();
    }

    [Fact]
    public void Validate_RejectsControlCharacters_InIdentifiers()
    {
        Assert.Throws<SqlFlowException>(() => (Valid() with { Database = "d\nb" }).Validate());
        Assert.Throws<SqlFlowException>(() => (Valid() with { NameLike = "a\0b" }).Validate());
    }

    [Fact]
    public void Validate_RejectsListObjects_ThatCanNeverReturnAnything()
    {
        Assert.Throws<SqlFlowException>(
            () => (Valid() with { IncludeTables = false, IncludeViews = false }).Validate());
    }

    [Fact]
    public void Json_RoundTripsThroughTheQueueRow()
    {
        var original = Valid(ComputeOperations.DetectUniqueKey) with
        {
            SourceRef = "@warehouse",
            Database = "dw",
            Schema = "dbo",
            ObjectName = "Orders",
            SampleSize = 50_000,
            MaxKeyColumns = 3,
            MaxCandidates = 2,
            VerifyCandidates = false,
            TrustDeclaredKeys = false,
        };

        var restored = ComputeTaskPayload.FromJson(original.ToJson());

        Assert.Equal(original, restored);
    }

    [Fact]
    public void FromJson_RejectsMalformedJson_AndInvalidPayloads()
    {
        // The worker treats the queue row as a trust boundary: garbage fails with a message, never a crash.
        Assert.Throws<SqlFlowException>(() => ComputeTaskPayload.FromJson("{not json"));
        // Well-formed JSON that fails validation (an inline connection string) is refused the same way.
        var invalid = """{"operation":"listObjects","sourceRef":"Server=x;Database=y"}""";
        Assert.Throws<SqlFlowException>(() => ComputeTaskPayload.FromJson(invalid));
    }
}
