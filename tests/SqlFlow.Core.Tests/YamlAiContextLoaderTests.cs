using SqlFlow.Core.AiContext;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Core.Tests;

/// <summary>
/// Reading an AI context file: the form a context takes, everything the reader refuses and the line it names, and that
/// what the writer produces reads back as the same context. A table in a context opens every column but those under its
/// <c>deny:</c>; <c>columns:</c> only says what columns mean. The reader is strict because a context is approved as a
/// whole: a key it silently dropped would leave a denial, a description or a join out of what an admin approved.
/// </summary>
public sealed class YamlAiContextLoaderTests
{
    // Line numbers below are counted in this text, from 1.
    private const string Valid = """
        aiContext: sales
        description: Orders and customers of the sales mart.
        instructions: |
          Amounts are in the reporting currency, excluding tax.
          Count an order once, by OrderId.
        tables:
          - table: dw.sales.Orders
            datasource: ${env:SQLFLOW_DW}
            name: Order lines
            description: One row per order line.
            synonyms: [sales, purchases]
            key: [OrderId, LineNo]
            columns:
              - column: Amount
                description: Line amount excluding tax.
                synonyms: revenue
            deny: [CostPrice, InternalNote]
            measures:
              - name: net_revenue
                expression: SUM(Amount)
                description: Revenue excluding tax.
            joins:
              - to: dw.sales.Customers
                columns: [CustomerId]
                toColumns: [Id]
                type: Left
                description: Every order has a customer.
          - table: sales.Customers
            deny: Email
          - table: sales.Regions
        examples:
          - question: What was revenue last month?
            sql: SELECT SUM(Amount) FROM sales.Orders
          - question: How many customers do we have?
            sql: SELECT COUNT(*) FROM sales.Customers
            datasource: "@dw"
        """;

    [Theory]
    [InlineData("ai-context.yaml", true)]
    [InlineData("AI-CONTEXT.YAML", true)]
    [InlineData("sales.ai-context.yaml", true)]
    [InlineData("contexts/finance/finance.ai-context.yaml", true)]
    [InlineData("ai-context.yml", false)]
    [InlineData("ai-contexts.yaml", false)]
    [InlineData("sales.ai-context.yaml.bak", false)]
    [InlineData("salesai-context.yaml", false)]
    public void IsAiContextFile_ByName(string path, bool expected)
        => Assert.Equal(expected, YamlAiContextLoader.IsAiContextFile(path));

    [Fact]
    public void AValidFile_ReadsIntoTheContextItWrites()
    {
        var parsed = YamlAiContextLoader.Parse(Valid, "sales.ai-context.yaml");

        Assert.Empty(parsed.Problems);
        var document = Assert.IsType<AiContextDocument>(parsed.Document);
        Assert.Equal("sales", document.Name);
        Assert.Equal("sales", parsed.Name);
        Assert.Equal("Orders and customers of the sales mart.", document.Description);
        Assert.StartsWith("Amounts are in the reporting currency", document.Instructions, StringComparison.Ordinal);
        Assert.Equal(3, document.Tables.Count);

        var orders = document.Tables[0];
        Assert.Equal(new AiContextTableName("dw", "sales", "Orders", "${env:SQLFLOW_DW}"), orders.Table);
        Assert.Equal("Order lines", orders.BusinessName);
        Assert.Equal(["sales", "purchases"], orders.Synonyms);
        Assert.Equal(["OrderId", "LineNo"], orders.Key);
        var amount = Assert.Single(orders.Columns);
        Assert.Equal("Amount", amount.Name);
        Assert.Equal("Line amount excluding tax.", amount.Description);
        Assert.Equal(["revenue"], amount.Synonyms);
        Assert.Equal(["CostPrice", "InternalNote"], orders.Deny);
        var measure = Assert.Single(orders.Measures);
        Assert.Equal(new AiContextMeasure("net_revenue", "SUM(Amount)", "Revenue excluding tax."), measure);
        var join = Assert.Single(orders.Joins);
        Assert.Equal(new AiContextTableName("dw", "sales", "Customers", null), join.To);
        Assert.Equal(["CustomerId"], join.Columns);
        Assert.Equal(["Id"], join.ToColumns);
        Assert.Equal(AiContextJoinTypes.Left, join.JoinType);
        Assert.Equal(7, orders.Line);

        var customers = document.Tables[1];
        Assert.Equal(new AiContextTableName(null, "sales", "Customers", null), customers.Table);
        Assert.Equal("sales.Customers", customers.Table.Display);
        Assert.Equal(["Email"], customers.Deny);
        Assert.Empty(customers.Columns);

        // A table on its own opens every column it has.
        var regions = document.Tables[2];
        Assert.Empty(regions.Deny);
        Assert.Empty(regions.Columns);

        Assert.Equal(2, document.Examples.Count);
        Assert.Equal("@dw", document.Examples[1].Datasource);
    }

    [Fact]
    public void TheWriter_WritesAFileThatReadsBackAsTheSameContext()
    {
        var original = Assert.IsType<AiContextDocument>(YamlAiContextLoader.Parse(Valid, "a.ai-context.yaml").Document);

        var written = AiContextYamlWriter.Write(original);
        var reread = YamlAiContextLoader.Parse(written, "b.ai-context.yaml");

        Assert.Empty(reread.Problems);
        var copy = Assert.IsType<AiContextDocument>(reread.Document);
        Assert.Equal(original.Name, copy.Name);
        Assert.Equal(original.Description, copy.Description);
        Assert.Equal(original.Instructions, copy.Instructions);
        Assert.Equal(original.Tables.Count, copy.Tables.Count);
        for (var i = 0; i < original.Tables.Count; i++)
        {
            var a = original.Tables[i];
            var b = copy.Tables[i];
            Assert.Equal(a.Table, b.Table);
            Assert.Equal(a.BusinessName, b.BusinessName);
            Assert.Equal(a.Description, b.Description);
            Assert.Equal(a.Synonyms, b.Synonyms);
            Assert.Equal(a.Key, b.Key);
            Assert.Equal(a.Deny, b.Deny);
            Assert.Equal(a.Columns.Select(c => (c.Name, c.Description, string.Join('|', c.Synonyms))),
                b.Columns.Select(c => (c.Name, c.Description, string.Join('|', c.Synonyms))));
            Assert.Equal(a.Measures, b.Measures);
            Assert.Equal(a.Joins.Select(j => (j.To, string.Join(',', j.Columns), string.Join(',', j.ToColumns), j.JoinType, j.Description)),
                b.Joins.Select(j => (j.To, string.Join(',', j.Columns), string.Join(',', j.ToColumns), j.JoinType, j.Description)));
        }

        Assert.Equal(original.Examples, copy.Examples);
    }

    [Theory]
    [InlineData("Sales Mart", "dbo", "Order Lines")]
    [InlineData("dw", "sales.v2", "Orders")]
    [InlineData("dw", "sales", "true")]
    [InlineData("dw", "sales", "[Odd]")]
    [InlineData("dw", "sales", "12345")]
    public void TheWriter_QuotesWhatPlainTextWouldMisread(string database, string schema, string name)
    {
        var document = new AiContextDocument(
            "quoting", "Plain: or not # here", "Line one\n  indented line two\n", [
                new AiContextTable(
                    new AiContextTableName(database, schema, name, null), "yes", "- a dash first", ["no", "on"], ["1"],
                    [new AiContextColumn("1", "null", ["~"])], ["false", "-x"], [], [], 0),
            ], [new AiContextExample("true?", "SELECT 1 AS [x: y]", "@alias")]);

        var reread = YamlAiContextLoader.Parse(AiContextYamlWriter.Write(document), "q.ai-context.yaml");

        Assert.Empty(reread.Problems);
        var copy = Assert.IsType<AiContextDocument>(reread.Document);
        Assert.Equal(new AiContextTableName(database, schema, name, null), copy.Tables[0].Table);
        Assert.Equal("yes", copy.Tables[0].BusinessName);
        Assert.Equal("- a dash first", copy.Tables[0].Description);
        Assert.Equal(["no", "on"], copy.Tables[0].Synonyms);
        Assert.Equal("null", copy.Tables[0].Columns[0].Description);
        Assert.Equal(["false", "-x"], copy.Tables[0].Deny);
        Assert.Equal("Plain: or not # here", copy.Description);
        Assert.Equal("Line one\n  indented line two", copy.Instructions);
        Assert.Equal(document.Examples, copy.Examples);
    }

    [Theory]
    [InlineData("[Sales Mart].dbo.[Order Lines]", "Sales Mart", "dbo", "Order Lines")]
    [InlineData("\"Sales Mart\".dbo.\"Order Lines\"", "Sales Mart", "dbo", "Order Lines")]
    [InlineData("dw.[sales.v2].Orders", "dw", "sales.v2", "Orders")]
    [InlineData("dw.sales.[Odd]]Name]", "dw", "sales", "Odd]Name")]
    [InlineData("sales.Orders", null, "sales", "Orders")]
    public void ATableName_ReadsBracketsQuotesAndTwoParts(string text, string? database, string schema, string name)
    {
        var yaml = $"aiContext: names\ntables:\n  - table: '{text.Replace("'", "''", StringComparison.Ordinal)}'\n";

        var parsed = YamlAiContextLoader.Parse(yaml, "n.ai-context.yaml");

        Assert.Empty(parsed.Problems);
        Assert.Equal(new AiContextTableName(database, schema, name, null), parsed.Document!.Tables[0].Table);
    }

    [Theory]
    [InlineData("Orders")]
    [InlineData("srv.dw.sales.Orders")]
    [InlineData("dw..Orders")]
    [InlineData("dw.sales.")]
    [InlineData("dw.[sales.Orders")]
    [InlineData("dw.[sales]x.Orders")]
    public void ATableName_ThatIsNotOneOrTwoDotsIsRefused(string text)
        => AssertProblem($"aiContext: names\ntables:\n  - table: '{text}'\n", 3, "is not a table name");

    [Fact]
    public void AnEmptyFile_ARootThatIsNotAMapping_AndTwoDocuments_AreRefused()
    {
        AssertProblem(string.Empty, null, "the file is empty");
        AssertProblem("- just\n- a list\n", 1, "must be a mapping");
        AssertProblem("aiContext: one\ntables: []\n---\naiContext: two\n", null, "holds 2 YAML documents");
    }

    [Fact]
    public void InvalidYaml_IsRefused()
        => AssertProblem("aiContext: broken\ntables:\n  - table: dw.sales.Orders\n    deny: [Id\n", null, "the file is not valid YAML");

    [Fact]
    public void ADuplicateKey_IsInvalidYaml()
        => AssertProblem("aiContext: one\naiContext: two\ntables: []\n", null, "not valid YAML");

    [Theory]
    [InlineData("tables:\n  - table: dw.s.T\n", "has no 'aiContext'")]
    [InlineData("aiContext: ''\ntables:\n  - table: dw.s.T\n", "is blank")]
    [InlineData("aiContext: Sales\ntables:\n  - table: dw.s.T\n", "is not a valid context name")]
    [InlineData("aiContext: -sales\ntables:\n  - table: dw.s.T\n", "is not a valid context name")]
    [InlineData("aiContext: sales mart\ntables:\n  - table: dw.s.T\n", "is not a valid context name")]
    [InlineData("aiContext: [sales]\ntables:\n  - table: dw.s.T\n", "must be text")]
    public void TheNameIsRequired_AndIsLowercaseWordsDotsDashesAndUnderscores(string yaml, string problem)
    {
        var parsed = AssertProblem(yaml, null, problem);
        Assert.Null(parsed.Name);
    }

    [Theory]
    [InlineData("sales")]
    [InlineData("sales-mart.v2")]
    [InlineData("0_finance")]
    public void AValidName_IsReadEvenWhenTheRestOfTheFileHasProblems(string name)
    {
        var parsed = YamlAiContextLoader.Parse($"aiContext: {name}\ntables: []\n", "x.ai-context.yaml");
        Assert.Equal(name, parsed.Name);
        Assert.Null(parsed.Document);
        Assert.Contains(parsed.Problems, p => p.Contains("'tables' is empty", StringComparison.Ordinal));
    }

    [Fact]
    public void ANameLongerThanTheLimit_IsRefused()
        => AssertProblem($"aiContext: {new string('a', AiContextLimits.MaxNameLength + 1)}\ntables: []\n", 1, "may be at most 100");

    [Fact]
    public void AnUnknownKey_IsAProblem_AndAMiscasedOneSaysHowToSpellIt()
    {
        AssertProblem(Valid.Replace("    name: Order lines", "    nmae: Order lines", StringComparison.Ordinal), 9, "unknown key 'nmae'");
        AssertProblem(Valid.Replace("    name: Order lines", "    Name: Order lines", StringComparison.Ordinal), 9, "write 'name'");
        AssertProblem(Valid.Replace("    deny: [CostPrice, InternalNote]", "    allow: [Amount]", StringComparison.Ordinal), 17, "unknown key 'allow'");
        AssertProblem(Valid + "\nowner: data team\n", 37, "unknown key 'owner'");
        AssertProblem(Valid.Replace("        description: Line amount excluding tax.", "        descripton: x", StringComparison.Ordinal), 15, "unknown key 'descripton'");
    }

    [Fact]
    public void TablesAreRequired_AndAnEmptyListIsRefused()
    {
        AssertProblem("aiContext: sales\n", 1, "lists no 'tables'");
        AssertProblem("aiContext: sales\ntables: []\n", 2, "'tables' is empty");
        AssertProblem("aiContext: sales\ntables: dw.sales.Orders\n", 2, "'tables' must be a list");
    }

    [Fact]
    public void ATableDescribedTwice_IsRefused()
        => AssertProblem("aiContext: s\ntables:\n  - table: dw.sales.Orders\n  - table: DW.SALES.ORDERS\n", 4, "is already described on line 3");

    [Fact]
    public void TheSameNameOnTwoDatasources_IsTwoTables()
    {
        var parsed = YamlAiContextLoader.Parse(
            "aiContext: s\ntables:\n  - table: dw.sales.Orders\n    datasource: '@a'\n  - table: dw.sales.Orders\n    datasource: '@b'\n",
            "x.ai-context.yaml");
        Assert.Empty(parsed.Problems);
        Assert.Equal(2, parsed.Document!.Tables.Count);
    }

    [Theory]
    [InlineData("    columns: [Amount]\n", "describes a column")]
    [InlineData("    columns: Amount\n", "must be a list")]
    [InlineData("    columns:\n      - column: Amount\n", "gives neither a description nor synonyms")]
    [InlineData("    columns:\n      - { column: Amount, description: a }\n      - { column: AMOUNT, description: b }\n", "is described twice")]
    [InlineData("    columns:\n      - column: ''\n        description: a\n", "is blank")]
    [InlineData("    columns:\n      - description: no name\n", "has no 'column'")]
    [InlineData("    columns:\n      - { column: Email, description: a }\n    deny: [Email]\n", "is both described and denied")]
    public void AColumnsEntry_DescribesAColumn_AndOpensNothing(string columns, string problem)
        => AssertProblem($"aiContext: s\ntables:\n  - table: dw.sales.Orders\n{columns}", null, problem);

    [Theory]
    [InlineData("    deny: [Email, EMAIL]\n", 4, "is denied twice")]
    [InlineData("    deny: ['']\n", 4, "must be non-blank text")]
    [InlineData("    deny:\n      - { column: Email }\n", 5, "must be non-blank text")]
    public void TheDenyList_NamesEachColumnOnce(string deny, int line, string problem)
        => AssertProblem($"aiContext: s\ntables:\n  - table: dw.sales.Orders\n{deny}", line, problem);

    [Fact]
    public void ADeniedNameLongerThanAnIdentifier_IsRefused()
        => AssertProblem(
            $"aiContext: s\ntables:\n  - table: dw.sales.Orders\n    deny: [{new string('c', AiContextLimits.MaxIdentifierLength + 1)}]\n",
            4, "is not a column name");

    [Theory]
    [InlineData("    key: [Email]\n", "is denied; a key is made of open columns")]
    [InlineData("    key: [Id, id]\n", "names a column twice")]
    [InlineData("    key: ['Id,Name']\n", "whose comma keeps it")]
    public void TheKey_IsMadeOfOpenColumns_OnceEach_WithoutCommas(string key, string problem)
        => AssertProblem($"aiContext: s\ntables:\n  - table: dw.sales.Orders\n    deny: [Email]\n{key}", null, problem);

    [Fact]
    public void AKeyOfOneColumn_MayBePlainText()
    {
        var parsed = YamlAiContextLoader.Parse("aiContext: s\ntables:\n  - table: dw.sales.Orders\n    key: Id\n", "x.ai-context.yaml");
        Assert.Empty(parsed.Problems);
        Assert.Equal(["Id"], parsed.Document!.Tables[0].Key);
    }

    [Theory]
    [InlineData("      - name: 1net\n        expression: SUM(Amount)\n", "is not a valid measure name")]
    [InlineData("      - name: net revenue\n        expression: SUM(Amount)\n", "is not a valid measure name")]
    [InlineData("      - name: net\n", "has no 'expression'")]
    [InlineData("      - expression: SUM(Amount)\n", "has no 'name'")]
    [InlineData("      - SUM(Amount)\n", "is a mapping with 'name' and 'expression'")]
    public void AMeasure_HasAValidNameAndAnExpression(string measure, string problem)
        => AssertProblem($"aiContext: s\ntables:\n  - table: dw.sales.Orders\n    measures:\n{measure}", null, problem);

    [Fact]
    public void AMeasureName_IsUniqueAcrossTheContextsTables()
        => AssertProblem(
            "aiContext: s\ntables:\n"
            + "  - table: dw.sales.Orders\n    measures:\n      - { name: total, expression: SUM(Amount) }\n"
            + "  - table: dw.sales.Returns\n    measures:\n      - { name: TOTAL, expression: SUM(Amount) }\n",
            6, "is also declared on table 'dw.sales.Orders'");

    [Fact]
    public void AnExpressionLongerThanTheLimit_IsRefused()
        => AssertProblem(
            $"aiContext: s\ntables:\n  - table: dw.sales.Orders\n    measures:\n      - name: m\n        expression: '{new string('1', AiContextLimits.MaxExpressionLength + 1)}'\n",
            6, "may be at most 4000");

    [Theory]
    [InlineData("      - to: dw.sales.Customers\n", "names no 'columns'")]
    [InlineData("      - columns: [CustomerId]\n", "has no 'to'")]
    [InlineData("      - to: dw.sales.Customers\n        columns: [Secret]\n", "joins on 'Secret', which the table denies")]
    [InlineData("      - to: dw.sales.Customers\n        columns: [CustomerId]\n        toColumns: [Id, Other]\n", "pairs 1 column(s) with 2")]
    [InlineData("      - to: dw.sales.Customers\n        columns: [CustomerId]\n        type: full\n", "a join is 'inner' or 'left'")]
    [InlineData("      - to: Customers\n        columns: [CustomerId]\n", "is not a table name")]
    public void AJoin_NamesItsTargetAndPairsOpenColumns(string join, string problem)
        => AssertProblem($"aiContext: s\ntables:\n  - table: dw.sales.Orders\n    deny: [Secret]\n    joins:\n{join}", null, problem);

    [Fact]
    public void AJoinDeclaredTwice_IsRefused_AndToColumnsDefaultToTheSameNames()
    {
        AssertProblem(
            "aiContext: s\ntables:\n  - table: dw.sales.Orders\n    joins:\n"
            + "      - { to: dw.sales.Customers, columns: [CustomerId] }\n      - { to: DW.SALES.CUSTOMERS, columns: [customerid] }\n",
            6, "is declared twice");

        var parsed = YamlAiContextLoader.Parse(
            "aiContext: s\ntables:\n  - table: dw.sales.Orders\n    joins:\n      - { to: dw.sales.Customers, columns: [CustomerId] }\n",
            "x.ai-context.yaml");
        var join = Assert.Single(parsed.Document!.Tables[0].Joins);
        Assert.Equal(["CustomerId"], join.ToColumns);
        Assert.Equal(AiContextJoinTypes.Inner, join.JoinType);
    }

    [Theory]
    [InlineData("  - question: q\n", "has no 'sql'")]
    [InlineData("  - sql: SELECT 1\n", "has no 'question'")]
    [InlineData("  - What is it?\n", "a mapping with 'question' and 'sql'")]
    [InlineData("  - { question: Same?, sql: SELECT 1 }\n  - { question: SAME?, sql: SELECT 2 }\n", "is already given on line")]
    public void AnExample_HasAQuestionAndSql_AndAQuestionIsGivenOnce(string examples, string problem)
        => AssertProblem($"aiContext: s\ntables:\n  - table: dw.sales.Orders\nexamples:\n{examples}", null, problem);

    [Theory]
    [InlineData("Server=db;Password=hunter2")]
    [InlineData("${env:HOST};Password=hunter2")]
    [InlineData("dw-server")]
    public void ADatasource_IsOneReference_AndALiteralIsNeverEchoed(string literal)
    {
        var parsed = AssertProblem(
            $"aiContext: s\ntables:\n  - table: dw.sales.Orders\n    datasource: '{literal}'\n", 4, "never a literal connection string");
        Assert.DoesNotContain(parsed.Problems, p => p.Contains("hunter2", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("${env:SQLFLOW_DW}")]
    [InlineData("${keyvault:dw-connection}")]
    [InlineData("@dw")]
    public void ADatasourceReference_IsAccepted(string reference)
    {
        var parsed = YamlAiContextLoader.Parse(
            $"aiContext: s\ntables:\n  - table: dw.sales.Orders\n    datasource: '{reference}'\n", "x.ai-context.yaml");
        Assert.Empty(parsed.Problems);
        Assert.Equal(reference, parsed.Document!.Tables[0].Table.Datasource);
    }

    [Fact]
    public void Synonyms_AreBoundedOneLineAndGivenOnce()
    {
        var many = string.Join(", ", Enumerable.Range(1, AiContextLimits.MaxSynonyms + 1).Select(i => $"s{i}"));
        AssertProblem($"aiContext: s\ntables:\n  - table: dw.sales.Orders\n    synonyms: [{many}]\n", 4, "keep a search precise");
        AssertProblem("aiContext: s\ntables:\n  - table: dw.sales.Orders\n    synonyms: [sales, SALES]\n", 4, "is given twice");
        AssertProblem(
            $"aiContext: s\ntables:\n  - table: dw.sales.Orders\n    synonyms: ['{new string('w', AiContextLimits.MaxSynonymLength + 1)}']\n",
            4, "must be one line");
        AssertProblem("aiContext: s\ntables:\n  - table: dw.sales.Orders\n    synonyms: ['']\n", 4, "must be non-blank text");
    }

    [Theory]
    [InlineData("description", AiContextLimits.MaxDescriptionLength)]
    [InlineData("instructions", AiContextLimits.MaxInstructionsLength)]
    public void TheContextsTexts_AreBounded(string key, int max)
        => AssertProblem(
            $"aiContext: s\n{key}: '{new string('t', max + 1)}'\ntables:\n  - table: dw.sales.Orders\n", 2, $"may be at most {max}");

    [Fact]
    public void BlankOptionalTexts_ReadAsAbsent()
    {
        var parsed = YamlAiContextLoader.Parse(
            "aiContext: s\ndescription: '  '\ninstructions:\ntables:\n  - table: dw.sales.Orders\n    name: ''\n", "x.ai-context.yaml");
        Assert.Empty(parsed.Problems);
        Assert.Null(parsed.Document!.Description);
        Assert.Null(parsed.Document.Instructions);
        Assert.Null(parsed.Document.Tables[0].BusinessName);
    }

    [Fact]
    public void EveryProblem_IsReported_NotJustTheFirst()
    {
        var parsed = YamlAiContextLoader.Parse(
            "aiContext: s\ntables:\n  - table: Orders\n    columns: [X]\n    key: [Y]\n    deny: [Y]\n    color: blue\n", "many.ai-context.yaml");

        Assert.Null(parsed.Document);
        Assert.Equal("s", parsed.Name);
        Assert.True(parsed.Problems.Count >= 4, string.Join(Environment.NewLine, parsed.Problems));
        Assert.All(parsed.Problems, p => Assert.StartsWith("many.ai-context.yaml:", p, StringComparison.Ordinal));
    }

    private static AiContextParse AssertProblem(string yaml, int? line, string fragment)
    {
        var parsed = YamlAiContextLoader.Parse(yaml, "x.ai-context.yaml");
        Assert.Null(parsed.Document);
        var expectedPrefix = line is null ? "x.ai-context.yaml" : $"x.ai-context.yaml:{line}: ";
        Assert.True(
            parsed.Problems.Any(p => p.Contains(fragment, StringComparison.Ordinal) && p.StartsWith(expectedPrefix, StringComparison.Ordinal)),
            $"expected a problem containing '{fragment}' at {expectedPrefix}, got:{Environment.NewLine}{string.Join(Environment.NewLine, parsed.Problems)}");
        return parsed;
    }
}
