using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests;

public sealed class EvolutionDdlGeneratorTests
{
    private static readonly RelationalObject Customer = RelationalObject.Parse("[Db].[dbo].[Customer]");

    private static SqlColumn Col(string name, string type, bool nullable = true, bool identity = false, bool pk = false)
        => new() { Name = name, DataType = SqlDataType.Parse(type), IsNullable = nullable, IsIdentity = identity, IsPrimaryKey = pk };

    private static ColumnAlter Alter(string name, string from, string to, ChangeFootprint footprint, bool nullable = true)
        => new() { Name = name, FromType = SqlDataType.Parse(from), ToType = SqlDataType.Parse(to), IsNullable = nullable, Footprint = footprint };

    [Fact]
    public void CreateTable_IsGuarded_WithIdentityAndPk()
    {
        var plan = new EvolutionPlan
        {
            CreateTable = true,
            CreateColumns = [Col("Sk", "int", nullable: false, identity: true, pk: true), Col("Name", "nvarchar(50)")],
        };

        var batch = EvolutionDdlGenerator.Generate(Customer, plan, allowTableRewrite: false);

        Assert.Equal(2, batch.Statements.Count);

        // The schema is ensured first (idempotent), then the guarded create runs in the same metadata batch.
        var ensure = batch.Statements[0];
        Assert.False(ensure.IsCreateTable);
        Assert.Equal(DdlCost.MetadataOnly, ensure.Cost);
        Assert.Equal("IF SCHEMA_ID(N'dbo') IS NULL EXEC(N'CREATE SCHEMA [dbo]');", ensure.Text);

        var statement = batch.Statements[1];
        Assert.True(statement.IsCreateTable);
        Assert.Equal(DdlCost.MetadataOnly, statement.Cost);
        Assert.Contains("IF OBJECT_ID(N'[dbo].[Customer]', N'U') IS NULL", statement.Text, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [dbo].[Customer]", statement.Text, StringComparison.Ordinal);
        Assert.Contains("[Sk] int IDENTITY(1, 1) NOT NULL", statement.Text, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT [PK_Customer] PRIMARY KEY CLUSTERED ([Sk])", statement.Text, StringComparison.Ordinal);
        Assert.Contains("[Name] nvarchar(50) NULL", statement.Text, StringComparison.Ordinal);
        Assert.Equal("dbo", batch.Schema);
        Assert.Equal("Customer", batch.Table);
    }

    [Fact]
    public void AddColumn_IsIdempotentAndNullable()
    {
        var plan = new EvolutionPlan { ColumnsToAdd = [Col("Notes", "nvarchar(400)")] };
        var statement = Assert.Single(EvolutionDdlGenerator.Generate(Customer, plan, allowTableRewrite: false).Statements);

        Assert.Equal(DdlCost.MetadataOnly, statement.Cost);
        Assert.Equal(
            "IF COL_LENGTH(N'[dbo].[Customer]', N'Notes') IS NULL ALTER TABLE [dbo].[Customer] ADD [Notes] nvarchar(400) NULL;",
            statement.Text);
    }

    [Fact]
    public void MetadataOnlyAlter_PreservesNullability_AndCost()
    {
        var plan = new EvolutionPlan { ColumnsToAlter = [Alter("Code", "varchar(32)", "varchar(64)", ChangeFootprint.MetadataOnly)] };
        var statement = Assert.Single(EvolutionDdlGenerator.Generate(Customer, plan, allowTableRewrite: false).Statements);

        Assert.Equal(DdlCost.MetadataOnly, statement.Cost);
        Assert.Equal("ALTER TABLE [dbo].[Customer] ALTER COLUMN [Code] varchar(64) NULL;", statement.Text);
    }

    [Fact]
    public void RewriteAlter_RefusedWithoutOptIn()
    {
        var plan = new EvolutionPlan { ColumnsToAlter = [Alter("Id", "int", "bigint", ChangeFootprint.TableRewrite, nullable: false)] };

        var ex = Assert.Throws<SchemaRewriteNotPermittedException>(() => EvolutionDdlGenerator.Generate(Customer, plan, allowTableRewrite: false));
        Assert.Contains("Id", ex.Columns);
    }

    [Fact]
    public void RewriteAlter_EmittedWithOptIn_AsRewriteCost()
    {
        var plan = new EvolutionPlan { ColumnsToAlter = [Alter("Id", "int", "bigint", ChangeFootprint.TableRewrite, nullable: false)] };
        var statement = Assert.Single(EvolutionDdlGenerator.Generate(Customer, plan, allowTableRewrite: true).Statements);

        Assert.Equal(DdlCost.Rewrite, statement.Cost);
        Assert.Equal("ALTER TABLE [dbo].[Customer] ALTER COLUMN [Id] bigint NOT NULL;", statement.Text);
    }

    [Fact]
    public void BlockedPlan_Throws()
    {
        var plan = new EvolutionPlan { CriticalMismatches = [new CriticalMismatch { Column = "K", Reason = "incompatible families" }] };
        Assert.Throws<SqlFlowException>(() => EvolutionDdlGenerator.Generate(Customer, plan, allowTableRewrite: true));
    }

    [Fact]
    public void Identifiers_AreBracketEscaped()
    {
        var target = RelationalObject.Parse("[Db].[dbo].[Odd]]Name]"); // object name contains a ]
        var plan = new EvolutionPlan { ColumnsToAdd = [Col("Weird]Col", "int")] };
        var statement = Assert.Single(EvolutionDdlGenerator.Generate(target, plan, allowTableRewrite: false).Statements);

        Assert.Contains("ALTER TABLE [dbo].[Odd]]Name] ADD [Weird]]Col] int NULL;", statement.Text, StringComparison.Ordinal);
    }
}
