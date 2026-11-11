using SqlFlow.Core;
using SqlFlow.Core.Dataverse;
using SqlFlow.Core.Model;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests.Dataverse;

/// <summary>
/// The dataverse source's options, parsed at run time and validated as written: every refusal names the option it is
/// about, a secret is only ever a reference, and the engine's file-only selections are refused rather than ignored.
/// </summary>
public sealed class DataverseSourceOptionsTests
{
    private static SourceSpec Spec(string? location, params (string Key, string? Value)[] options)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["table"] = "account" };
        foreach (var (key, value) in options)
        {
            map[key] = value;
        }

        return new SourceSpec { Type = "dataverse", Location = location, Options = map };
    }

    private static string Refusal(SourceSpec spec) => Assert.Throws<SqlFlowException>(() => DataverseSourceOptions.Parse(spec)).Message;

    [Fact]
    public void A_minimal_source_reads_every_column_of_the_table_with_the_defaults()
    {
        var options = DataverseSourceOptions.Parse(Spec("https://contoso.crm4.dynamics.com/"));

        Assert.Equal(new Uri("https://contoso.crm4.dynamics.com/"), options.Environment);
        Assert.Equal("account", options.Table);
        Assert.Empty(options.Columns);
        Assert.Equal(5000, options.PageSize);
        Assert.Null(options.ServicePrincipal);
        Assert.Equal(new Uri("https://contoso.crm4.dynamics.com/api/data/v9.2/"), options.ApiRoot);
        Assert.Equal("https://contoso.crm4.dynamics.com/.default", options.Scope);
        Assert.Null(options.WatermarkBound);
        Assert.Null(options.OrderColumn);
    }

    [Theory]
    [InlineData(null, "needs 'location'")]
    [InlineData("contoso.crm4.dynamics.com", "not an https environment URL")]
    [InlineData("http://contoso.crm4.dynamics.com", "not an https environment URL")]
    [InlineData("https://contoso.crm4.dynamics.com/api/data/v9.2", "environment URL alone")]
    [InlineData("https://contoso.crm4.dynamics.com/?x=1", "environment URL alone")]
    public void A_location_that_is_not_an_environment_url_is_refused(string? location, string expected)
        => Assert.Contains(expected, Refusal(Spec(location)), StringComparison.Ordinal);

    [Fact]
    public void Logical_names_are_validated_and_lower_cased()
    {
        var options = DataverseSourceOptions.Parse(Spec("https://contoso.crm4.dynamics.com", ("table", "Cr6b2_Project"), ("columns", "Name, cr6b2_Code")));

        Assert.Equal("cr6b2_project", options.Table);
        Assert.Equal(["name", "cr6b2_code"], options.Columns);
        Assert.Contains("not a Dataverse logical name", Refusal(Spec("https://contoso.crm4.dynamics.com", ("table", "account; drop"))), StringComparison.Ordinal);
        Assert.Contains("needs the option 'table'", Refusal(Spec("https://contoso.crm4.dynamics.com", ("table", null))), StringComparison.Ordinal);
    }

    [Fact]
    public void Columns_and_excluded_columns_cannot_be_combined_and_a_name_may_not_repeat()
    {
        Assert.Contains("not both", Refusal(Spec("https://contoso.crm4.dynamics.com", ("columns", "name"), ("excludeColumns", "description"))), StringComparison.Ordinal);
        Assert.Contains("names the column 'name' twice", Refusal(Spec("https://contoso.crm4.dynamics.com", ("columns", "name,NAME"))), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pageSize", "0")]
    [InlineData("pageSize", "5001")]
    [InlineData("pageSize", "many")]
    [InlineData("timeoutSeconds", "5")]
    [InlineData("maxAttempts", "21")]
    [InlineData("maxRetryDelaySeconds", "0")]
    [InlineData("maxPageMegabytes", "4096")]
    public void A_number_out_of_its_range_is_refused_naming_the_option(string option, string value)
        => Assert.Contains($"'{option}' value '{value}'", Refusal(Spec("https://contoso.crm4.dynamics.com", (option, value))), StringComparison.Ordinal);

    [Fact]
    public void A_service_principal_needs_all_three_parts_and_its_secret_as_a_reference()
    {
        var full = DataverseSourceOptions.Parse(Spec(
            "https://contoso.crm4.dynamics.com",
            ("tenantId", "00000000-0000-0000-0000-000000000001"),
            ("clientId", "${env:DV_CLIENT_ID}"),
            ("clientSecret", "${keyvault:kv/dv-secret}")));
        Assert.Equal("${keyvault:kv/dv-secret}", full.ServicePrincipal!.ClientSecretReference);

        Assert.Contains("needs all of", Refusal(Spec("https://contoso.crm4.dynamics.com", ("tenantId", "t"), ("clientId", "c"))), StringComparison.Ordinal);
        var literal = Refusal(Spec("https://contoso.crm4.dynamics.com", ("tenantId", "t"), ("clientId", "c"), ("clientSecret", "s3cr3t-value")));
        Assert.Contains("never a literal", literal, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t-value", literal, StringComparison.Ordinal);
    }

    [Fact]
    public void An_authority_host_applies_only_to_an_explicit_service_principal()
        => Assert.Contains("AZURE_AUTHORITY_HOST", Refusal(Spec("https://contoso.crm4.dynamics.com", ("authorityHost", "https://login.microsoftonline.us"))), StringComparison.Ordinal);

    [Theory]
    [InlineData("initFromFileDate", "a backfill window")]
    [InlineData("initToFileDate", "a backfill window")]
    [InlineData("incrementalAfterDate", "a file-date incremental watermark")]
    [InlineData("srcFile", "a file pattern")]
    public void A_file_only_selection_the_engine_injects_is_refused_not_ignored(string option, string meaning)
    {
        var message = Refusal(Spec("https://contoso.crm4.dynamics.com", (option, "2026-01-01")));
        Assert.Contains(meaning, message, StringComparison.Ordinal);
        Assert.Contains("--full", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_engine_bound_and_order_are_read_from_the_injected_options()
    {
        var options = DataverseSourceOptions.Parse(Spec(
            "https://contoso.crm4.dynamics.com",
            (WatermarkPredicate.ColumnOption, "VersionNumber"),
            (WatermarkPredicate.ValueOption, "638183964640916100"),
            (WatermarkPredicate.KindOption, "Whole"),
            (WatermarkPredicate.OrderColumnOption, "versionnumber")));

        Assert.Equal(new DataverseWatermarkBound("versionnumber", "638183964640916100", WatermarkKind.Whole), options.WatermarkBound);
        Assert.Equal("versionnumber", options.OrderColumn);
    }

    [Fact]
    public void Validation_as_written_refuses_an_unknown_option_and_a_file_date_incremental_block()
    {
        var typo = Assert.Throws<SqlFlowException>(() =>
            DataverseSourceOptions.ValidateAuthored(Spec("https://contoso.crm4.dynamics.com", ("tabel", "account")), incremental: null));
        Assert.Contains("does not take the option 'tabel'", typo.Message, StringComparison.Ordinal);

        var fileDate = Assert.Throws<SqlFlowException>(() =>
            DataverseSourceOptions.ValidateAuthored(Spec("https://contoso.crm4.dynamics.com"), new IncrementalSpec()));
        Assert.Contains("needs 'watermarkColumn'", fileDate.Message, StringComparison.Ordinal);

        // A location that names a reference is resolved only when the flow runs, so it is not checked as written.
        DataverseSourceOptions.ValidateAuthored(Spec("${env:DV_URL}"), new IncrementalSpec { WatermarkColumn = "versionnumber" });
    }

    [Fact]
    public void The_yaml_loader_refuses_a_literal_secret_naming_the_file()
    {
        const string yaml = """
            name: crm_account_01_dv
            source:
              type: dataverse
              location: https://contoso.crm4.dynamics.com
              options:
                table: account
                tenantId: 00000000-0000-0000-0000-000000000001
                clientId: 00000000-0000-0000-0000-000000000002
                clientSecret: hunter2-literal
            target:
              connection: ${env:SQLFLOW_DW}
              schema: pre
              table: Crm_Account
            """;

        var ex = Assert.Throws<FlowValidationException>(() => new YamlFlowLoader().Parse(yaml, "crm_account_01_dv.yaml"));
        Assert.StartsWith("crm_account_01_dv.yaml: ", ex.Message, StringComparison.Ordinal);
        Assert.Contains("never a literal", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2-literal", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_yaml_loader_accepts_a_watermarked_dataverse_flow()
    {
        const string yaml = """
            name: crm_account_01_dv
            source:
              type: Dataverse
              location: ${env:DV_URL}
              options:
                table: account
                excludeColumns: description
                formattedValues: "true"
            target:
              connection: ${env:SQLFLOW_DW}
              schema: pre
              table: Crm_Account
            incremental:
              watermarkColumn: versionnumber
            """;

        var flow = new YamlFlowLoader().Parse(yaml, "crm_account_01_dv.yaml");
        Assert.True(DataverseSourceOptions.IsDataverse(flow.Source.Type));
        Assert.Equal("versionnumber", flow.Incremental!.WatermarkColumn);
        Assert.Equal("${env:DV_URL}/account", DataverseSourceOptions.Identity(flow.Source));
    }
}
