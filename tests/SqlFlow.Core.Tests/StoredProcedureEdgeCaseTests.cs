using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.StoredProcedures.Legacy;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Non-overlapping edge cases for the stored-procedure feature area (the legacy mapper and the YAML loader).
/// These probe boundaries the happy-path suites do not: required-field validation beyond trgServer, the
/// optional-field NULL-to-default contract, three/four-part procedure parsing (brackets, escapes, dropped
/// leading server part), server/connection resolution conflicts, provider rejection, name and document
/// validation, and the deterministic name-derived flow id. Everything is pure and in-memory so the suite
/// always runs.
/// </summary>
public sealed class StoredProcedureEdgeCaseTests
{
    // A complete legacy row with only the mandatory fields filled, so each test mutates exactly the column
    // it exercises. Uniquely named to avoid colliding with helpers in sibling files of this namespace.
    private static LegacyStoredProcedureRow EdgeRow() => new()
    {
        FlowID = 42,
        SysAlias = "sys",
        trgServer = "dw",
        trgDBSchSP = "[DW].[dbo].[usp_Refresh]",
    };

    private static readonly YamlStoredProcedureFlowLoader EdgeLoader = new();

    // The smallest valid stored-procedure document; tests rewrite individual lines to probe one rule each.
    private const string EdgeMinimal = """
        flowType: sp
        name: refresh-marts
        connections:
          dwh: ${env:DWH}
        procedure:
          server: dwh
          object: DW.dbo.usp_RefreshMarts
        """;

    // ---------------------------------------------------------------------------------------------------
    // Legacy mapper: required-field validation
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Mapper_NullRow_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => StoredProcedureFlowMapper.FromLegacy(null!));
    }

    [Fact]
    public void Mapper_MissingSysAlias_Throws()
    {
        var row = EdgeRow();
        row.SysAlias = null;
        Assert.Throws<SqlFlowException>(() => StoredProcedureFlowMapper.FromLegacy(row));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("   \r\n  ")]
    public void Mapper_BlankSysAlias_TreatedAsMissing(string blank)
    {
        var row = EdgeRow();
        row.SysAlias = blank;
        Assert.Throws<SqlFlowException>(() => StoredProcedureFlowMapper.FromLegacy(row));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\t")]
    public void Mapper_BlankTrgServer_TreatedAsMissing(string blank)
    {
        var row = EdgeRow();
        row.trgServer = blank;
        Assert.Throws<SqlFlowException>(() => StoredProcedureFlowMapper.FromLegacy(row));
    }

    [Theory]
    [InlineData("trgServer")]
    [InlineData("SysAlias")]
    [InlineData("trgDBSchSP")]
    public void Mapper_MissingRequired_NamesFlowAndColumn(string column)
    {
        var row = EdgeRow();
        switch (column)
        {
            case "trgServer":
                row.trgServer = null;
                break;
            case "SysAlias":
                row.SysAlias = null;
                break;
            default:
                row.trgDBSchSP = null;
                break;
        }

        var ex = Assert.Throws<SqlFlowException>(() => StoredProcedureFlowMapper.FromLegacy(row));
        Assert.Contains("42", ex.Message, StringComparison.Ordinal);
        Assert.Contains(column, ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------
    // Legacy mapper: optional-field NULL-to-default contract
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, true)]   // legacy NULL resolves to the documented default (true)
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Mapper_OnErrorResume_DefaultsTrueOnNull(bool? stored, bool expected)
    {
        var row = EdgeRow();
        row.OnErrorResume = stored;
        Assert.Equal(expected, StoredProcedureFlowMapper.FromLegacy(row).OnErrorResume);
    }

    [Theory]
    [InlineData(null, false)]  // legacy NULL resolves to false
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Mapper_DeactivateFromBatch_DefaultsFalseOnNull(bool? stored, bool expected)
    {
        var row = EdgeRow();
        row.DeactivateFromBatch = stored;
        Assert.Equal(expected, StoredProcedureFlowMapper.FromLegacy(row).DeactivateFromBatch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Mapper_BlankOptionalStrings_BecomeNull(string? blank)
    {
        var row = EdgeRow();
        row.Batch = blank;
        row.PostInvokeAlias = blank;
        row.Description = blank;
        row.CreatedBy = blank;

        var flow = StoredProcedureFlowMapper.FromLegacy(row);
        Assert.Null(flow.Batch);
        Assert.Null(flow.PostInvokeAlias);
        Assert.Null(flow.Description);
        Assert.Null(flow.CreatedBy);
    }

    [Fact]
    public void Mapper_BlankFlowType_FallsBackToSp()
    {
        var row = EdgeRow();
        row.FlowType = "   ";
        Assert.Equal("sp", StoredProcedureFlowMapper.FromLegacy(row).FlowType);
    }

    [Fact]
    public void Mapper_NonBlankFlowType_IsPreserved()
    {
        var row = EdgeRow();
        row.FlowType = "sp-custom";
        Assert.Equal("sp-custom", StoredProcedureFlowMapper.FromLegacy(row).FlowType);
    }

    [Fact]
    public void Mapper_OptionalScalars_ArePreservedVerbatim()
    {
        var created = new DateTime(2021, 3, 14, 9, 26, 53, DateTimeKind.Utc);
        var row = EdgeRow();
        row.Batch = "nightly";
        row.PostInvokeAlias = "after";
        row.Description = "Refresh the marts.";
        row.CreatedBy = "ci";
        row.CreatedDate = created;
        row.FromObjectMK = 7;
        row.ToObjectMK = 11;

        var flow = StoredProcedureFlowMapper.FromLegacy(row);
        Assert.Equal("nightly", flow.Batch);
        Assert.Equal("after", flow.PostInvokeAlias);
        Assert.Equal("Refresh the marts.", flow.Description);
        Assert.Equal("ci", flow.CreatedBy);
        Assert.Equal(created, flow.CreatedDate);
        Assert.Equal(7, flow.FromObjectMK);
        Assert.Equal(11, flow.ToObjectMK);
    }

    [Fact]
    public void Mapper_NullLineageAndDate_StayNull()
    {
        var row = EdgeRow();
        row.FromObjectMK = null;
        row.ToObjectMK = null;
        row.CreatedDate = null;

        var flow = StoredProcedureFlowMapper.FromLegacy(row);
        Assert.Null(flow.FromObjectMK);
        Assert.Null(flow.ToObjectMK);
        Assert.Null(flow.CreatedDate);
    }

    // ---------------------------------------------------------------------------------------------------
    // Legacy mapper: procedure (three-part) name parsing
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Mapper_PlainThreePartName_Splits()
    {
        var row = EdgeRow();
        row.trgDBSchSP = "DW.dbo.usp_Plain";

        var proc = StoredProcedureFlowMapper.FromLegacy(row).Procedure;
        Assert.Equal("DW", proc.Database);
        Assert.Equal("dbo", proc.Schema);
        Assert.Equal("usp_Plain", proc.Name);
    }

    [Fact]
    public void Mapper_FourPartName_DropsLeadingServerPart()
    {
        // GetValidSrcTrgName semantics: a stray leading server part is dropped, the rightmost three win.
        var row = EdgeRow();
        row.trgDBSchSP = "SRV.DW.dbo.usp_Refresh";

        var proc = StoredProcedureFlowMapper.FromLegacy(row).Procedure;
        Assert.Equal("DW", proc.Database);
        Assert.Equal("dbo", proc.Schema);
        Assert.Equal("usp_Refresh", proc.Name);
    }

    [Fact]
    public void Mapper_BracketedPartWithDot_IsOneIdentifier()
    {
        // The '.' inside brackets is part of the schema identifier, not a separator.
        var row = EdgeRow();
        row.trgDBSchSP = "[DW].[my.schema].[usp_X]";

        var proc = StoredProcedureFlowMapper.FromLegacy(row).Procedure;
        Assert.Equal("DW", proc.Database);
        Assert.Equal("my.schema", proc.Schema);
        Assert.Equal("usp_X", proc.Name);
    }

    [Fact]
    public void Mapper_BracketedNameWithEscapedBracket_IsUnescaped()
    {
        // An inner ']' is escaped as ']]' in the bracketed token and unescaped on parse.
        var row = EdgeRow();
        row.trgDBSchSP = "[DW].[dbo].[odd]]name]";

        var proc = StoredProcedureFlowMapper.FromLegacy(row).Procedure;
        Assert.Equal("odd]name", proc.Name);
        // And the qualified form re-escapes it losslessly.
        Assert.Equal("[DW].[dbo].[odd]]name]", proc.QualifiedName);
    }

    [Theory]
    [InlineData("usp_X")]                 // one part
    [InlineData("dbo.usp_X")]             // two parts
    [InlineData("[dbo].[usp_X]")]         // two bracketed parts
    public void Mapper_FewerThanThreeParts_Throws(string name)
    {
        var row = EdgeRow();
        row.trgDBSchSP = name;
        Assert.Throws<SqlFlowException>(() => StoredProcedureFlowMapper.FromLegacy(row));
    }

    [Theory]
    [InlineData("DW..usp_X")]             // empty schema
    [InlineData(".dbo.usp_X")]            // empty database
    [InlineData("DW.dbo.")]               // empty object
    public void Mapper_EmptyPart_Throws(string name)
    {
        var row = EdgeRow();
        row.trgDBSchSP = name;
        Assert.Throws<SqlFlowException>(() => StoredProcedureFlowMapper.FromLegacy(row));
    }

    [Fact]
    public void Mapper_BlankProcedure_WrapsWithColumnContext()
    {
        var row = EdgeRow();
        row.trgDBSchSP = "  ";
        var ex = Assert.Throws<SqlFlowException>(() => StoredProcedureFlowMapper.FromLegacy(row));
        Assert.Contains("trgDBSchSP", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Mapper_ConnectionReference_PrefixesServerWithAt()
    {
        var row = EdgeRow();
        row.trgServer = "AdventureWorks";
        Assert.Equal("@AdventureWorks", StoredProcedureFlowMapper.FromLegacy(row).ConnectionReference);
    }

    [Fact]
    public void Mapper_FlowIdAndServer_AreCarriedThrough()
    {
        var row = EdgeRow();
        row.FlowID = 9001;
        row.trgServer = "edw";

        var flow = StoredProcedureFlowMapper.FromLegacy(row);
        Assert.Equal(9001, flow.FlowId);
        Assert.Equal("edw", flow.Server);
    }

    // ---------------------------------------------------------------------------------------------------
    // YAML loader: server / connection resolution
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Yaml_ServerAndConnectionTogether_Rejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader.Parse("""
            flowType: sp
            name: clash
            connections:
              dwh: ${env:DWH}
            procedure:
              server: dwh
              connection: ${env:OTHER}
              object: DW.dbo.usp_X
            """));

        Assert.Contains("use exactly one", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_ServerReferencesUndeclaredConnection_Rejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader.Parse("""
            flowType: sp
            name: ghost-server
            connections:
              dwh: ${env:DWH}
            procedure:
              server: missing
              object: DW.dbo.usp_X
            """));

        Assert.Contains("not declared under 'connections:'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_InlineConnectionCollidesWithDeclaredTarget_Rejected()
    {
        // A direct connection: synthesizes the reserved name 'target'; a user-declared 'target' collides.
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader.Parse("""
            flowType: sp
            name: collide
            connections:
              target: ${env:DWH}
            procedure:
              connection: ${env:OTHER}
              object: DW.dbo.usp_X
            """));

        Assert.Contains("already declares 'target'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_NeitherServerNorConnection_Rejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader.Parse("""
            flowType: sp
            name: no-endpoint
            procedure:
              object: DW.dbo.usp_X
            """));

        Assert.Contains("needs a connection", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_ServerNameIsTrimmedBeforeMatching()
    {
        var doc = EdgeLoader.Parse("""
            flowType: sp
            name: trimmed
            connections:
              dwh: ${env:DWH}
            procedure:
              server: '  dwh  '
              object: DW.dbo.usp_X
            """);

        Assert.Equal("dwh", doc.Flow.Server);
    }

    // ---------------------------------------------------------------------------------------------------
    // YAML loader: provider rejection (the server must be SQL Server)
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Yaml_AzdbServer_IsAccepted()
    {
        var doc = EdgeLoader.Parse("""
            flowType: sp
            name: azdb-ok
            connections:
              dwh:
                provider: azdb
                connection: ${env:DWH}
            procedure:
              server: dwh
              object: DW.dbo.usp_X
            """);

        var connection = Assert.Single(doc.Connections);
        Assert.Equal(DataSourceKind.AZDB, connection.Kind);
        Assert.Equal("dwh", doc.Flow.Server);
    }

    [Theory]
    [InlineData("mysql", "MySQL")]
    [InlineData("postgres", "PostgreSQL")]
    public void Yaml_ForeignProviderServer_Rejected(string provider, string rendered)
    {
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader.Parse($$"""
            flowType: sp
            name: foreign
            connections:
              shop:
                provider: {{provider}}
                connection: ${env:SHOP}
            procedure:
              server: shop
              object: db.public.usp_X
            """));

        Assert.Contains($"is '{rendered}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("must be SQL Server (mssql or azdb)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_UnknownProvider_Rejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader.Parse("""
            flowType: sp
            name: bogus-provider
            connections:
              dwh:
                provider: db2
                connection: ${env:DWH}
            procedure:
              server: dwh
              object: DW.dbo.usp_X
            """));

        Assert.Contains("unknown provider 'db2'", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------
    // YAML loader: connection-block validation
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Yaml_InvalidConnectionName_Rejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader.Parse("""
            flowType: sp
            name: bad-conn-name
            connections:
              "has space": ${env:DWH}
            procedure:
              server: "has space"
              object: DW.dbo.usp_X
            """));

        Assert.Contains("is invalid", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_BareConnection_ResolvesTheConventionalReference()
    {
        // A bare alias (no value) takes the canonical ${env:SQLFLOW_CONN_<NAME>} reference.
        var doc = EdgeLoader.Parse("""
            flowType: sp
            name: bare
            connections:
              dwh:
            procedure:
              server: dwh
              object: DW.dbo.usp_X
            """);

        var connection = Assert.Single(doc.Connections);
        Assert.Equal("${env:SQLFLOW_CONN_DWH}", connection.ConnectionRef);
        Assert.Equal(DataSourceKind.MSSQL, connection.Kind);
    }

    // ---------------------------------------------------------------------------------------------------
    // YAML loader: procedure.object validation
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Yaml_MissingObjectKey_FailsWithThePath()
    {
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader.Parse("""
            flowType: sp
            name: no-object
            connections:
              dwh: ${env:DWH}
            procedure:
              server: dwh
            """));

        Assert.Contains("'procedure.object' is required", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("usp_X")]
    [InlineData("dbo.usp_X")]
    [InlineData("DW..usp_X")]
    public void Yaml_ObjectNotThreeParts_FailsWithThePath(string badObject)
    {
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader.Parse($$"""
            flowType: sp
            name: bad-object
            connections:
              dwh: ${env:DWH}
            procedure:
              server: dwh
              object: {{badObject}}
            """));

        Assert.Contains("'procedure.object'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_FourPartObject_DropsLeadingServerPart()
    {
        var doc = EdgeLoader.Parse("""
            flowType: sp
            name: four-part
            connections:
              dwh: ${env:DWH}
            procedure:
              server: dwh
              object: SRV.DW.dbo.usp_X
            """);

        Assert.Equal("DW", doc.Flow.Procedure.Database);
        Assert.Equal("dbo", doc.Flow.Procedure.Schema);
        Assert.Equal("usp_X", doc.Flow.Procedure.Name);
    }

    // ---------------------------------------------------------------------------------------------------
    // YAML loader: document-level validation and identity
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Yaml_EmptyDocument_Rejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader.Parse("   \n  \n"));
        Assert.Contains("the document is empty", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_MalformedYaml_WrappedAsValidationError()
    {
        // A broken block mapping (unclosed flow scalar) surfaces as a FlowValidationException, not a raw
        // YamlException, with the source label.
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader.Parse("name: [unterminated"));
        Assert.Contains("invalid YAML", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_FlowValidationException_IsASqlFlowException()
    {
        // Callers that catch the base SqlFlowException must also catch validation failures.
        var ex = Assert.ThrowsAny<SqlFlowException>(() => EdgeLoader.Parse("flowType: sp\nname: x\n"));
        Assert.IsType<FlowValidationException>(ex);
    }

    [Fact]
    public void Yaml_BlankBatchAndDescription_BecomeNull()
    {
        var doc = EdgeLoader.Parse("""
            flowType: sp
            name: blanks
            description: '   '
            batch: ''
            connections:
              dwh: ${env:DWH}
            procedure:
              server: dwh
              object: DW.dbo.usp_X
            """);

        Assert.Null(doc.Flow.Batch);
        Assert.Null(doc.Flow.Description);
    }

    [Fact]
    public void Yaml_ExplicitOnErrorResumeTrue_IsHonored()
    {
        var doc = EdgeLoader.Parse("""
            flowType: sp
            name: explicit-true
            connections:
              dwh: ${env:DWH}
            procedure:
              server: dwh
              object: DW.dbo.usp_X
            onErrorResume: true
            """);

        Assert.True(doc.Flow.OnErrorResume);
    }

    [Fact]
    public void Yaml_UnicodeName_IsAccepted()
    {
        // The flow name carries no charset restriction (only connection names do); a non-ASCII name is fine.
        var doc = EdgeLoader.Parse("""
            flowType: sp
            name: oppdater-malttabeller-æøå
            connections:
              dwh: ${env:DWH}
            procedure:
              server: dwh
              object: DW.dbo.usp_X
            """);

        Assert.Equal("oppdater-malttabeller-æøå", doc.Flow.SysAlias);
        Assert.True(doc.Flow.FlowId > 0);
    }

    [Fact]
    public void Yaml_StableFlowId_IsDeterministicAndPositive()
    {
        var first = EdgeLoader.Parse(EdgeMinimal).Flow.FlowId;
        var second = EdgeLoader.Parse(EdgeMinimal).Flow.FlowId;

        Assert.Equal(first, second);
        Assert.True(first > 0, $"expected a positive flow id but got {first.ToString(CultureInfo.InvariantCulture)}.");
    }

    [Fact]
    public void Yaml_DifferentNames_YieldDifferentFlowIds()
    {
        var a = EdgeLoader.Parse(EdgeMinimal).Flow.FlowId;
        var b = EdgeLoader.Parse(EdgeMinimal.Replace("refresh-marts", "refresh-cubes", StringComparison.Ordinal)).Flow.FlowId;

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Yaml_QualifiedName_IsFullyBracketed()
    {
        var doc = EdgeLoader.Parse("""
            flowType: sp
            name: qualified
            connections:
              dwh: ${env:DWH}
            procedure:
              server: dwh
              object: DW.Reporting.usp_Build
            """);

        Assert.Equal("[DW].[Reporting].[usp_Build]", doc.Flow.Procedure.QualifiedName);
    }

    [Fact]
    public void Yaml_MismatchedFlowTypeField_IsTrustedToTheDispatcher()
    {
        // The loader itself does not re-check flowType (the document dispatcher already routed here); a stray
        // value on the field is therefore mapped as a stored-procedure flow, which keeps the single code path.
        var doc = EdgeLoader.Parse("""
            flowType: ing
            name: still-sp
            connections:
              dwh: ${env:DWH}
            procedure:
              server: dwh
              object: DW.dbo.usp_X
            """);

        Assert.Equal("sp", doc.Flow.FlowType);
        Assert.Equal("still-sp", doc.Flow.SysAlias);
    }

    [Fact]
    public void Yaml_NoInvokesDeclared_CollectionsAreEmptyAndHookNull()
    {
        var doc = EdgeLoader.Parse(EdgeMinimal);

        Assert.Empty(doc.Invokes);
        Assert.Empty(doc.ServicePrincipals);
        Assert.Null(doc.Flow.PostInvokeAlias);
    }

    // ---------------------------------------------------------------------------------------------------
    // YAML loader: file entry points
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Yaml_LoadFile_MissingPath_Rejected()
    {
        var missing = Path.Combine(Path.GetTempPath(), "sqlflow-sp-edge-does-not-exist-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".yaml");
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader.LoadFile(missing));
        Assert.Contains("Pipeline file not found", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Yaml_LoadFile_BlankPath_Rejected(string blank)
    {
        Assert.Throws<ArgumentException>(() => EdgeLoader.LoadFile(blank));
    }
}
