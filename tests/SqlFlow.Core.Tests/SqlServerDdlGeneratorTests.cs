using SqlFlow.Core.Model;
using SqlFlow.SqlServer;
using Xunit;

namespace SqlFlow.Tests;

public sealed class SqlServerDdlGeneratorTests
{
    private static readonly SqlServerDdlGenerator Generator = new();
    private static readonly TargetSpec Target = new() { Connection = "x", Schema = "dbo", Table = "Orders" };

    [Fact]
    public void Generate_CreateTable_EmitsCreateWithNullability()
    {
        var delta = new SchemaDelta
        {
            CreateTable = true,
            ColumnsToAdd =
            [
                new ColumnDefinition { Name = "OrderId", SqlType = "BIGINT", IsNullable = false },
                new ColumnDefinition { Name = "Amount", SqlType = "DECIMAL(38, 6)", IsNullable = true },
            ],
        };

        var sql = Generator.Generate(Target, delta);

        Assert.Equal(2, sql.Count);
        Assert.Equal("IF SCHEMA_ID(N'dbo') IS NULL EXEC(N'CREATE SCHEMA [dbo]');", sql[0]);
        Assert.Contains("CREATE TABLE [dbo].[Orders]", sql[1], StringComparison.Ordinal);
        Assert.Contains("[OrderId] BIGINT NOT NULL", sql[1], StringComparison.Ordinal);
        Assert.Contains("[Amount] DECIMAL(38, 6) NULL", sql[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_CreateTable_EnsuresNonDefaultSchemaFirst()
    {
        var target = new TargetSpec { Connection = "x", Schema = "pre", Table = "Baatbooking_detail" };
        var delta = new SchemaDelta
        {
            CreateTable = true,
            ColumnsToAdd = [new ColumnDefinition { Name = "MEDIA_TYPE", SqlType = "varchar(255)", IsNullable = true }],
        };

        var sql = Generator.Generate(target, delta);

        Assert.Equal(2, sql.Count);
        Assert.Equal("IF SCHEMA_ID(N'pre') IS NULL EXEC(N'CREATE SCHEMA [pre]');", sql[0]);
        Assert.Contains("CREATE TABLE [pre].[Baatbooking_detail]", sql[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_AddColumns_ForcesNullable()
    {
        var delta = new SchemaDelta
        {
            ColumnsToAdd = [new ColumnDefinition { Name = "NewCol", SqlType = "INT", IsNullable = false }],
        };

        var sql = Generator.Generate(Target, delta);

        Assert.Single(sql);
        Assert.Equal("ALTER TABLE [dbo].[Orders] ADD [NewCol] INT NULL;", sql[0]);
    }

    [Fact]
    public void Generate_WidenColumn_EmitsAlterColumnKeepingNullability()
    {
        var delta = new SchemaDelta
        {
            ColumnsToAlter = [new ColumnDefinition { Name = "Remarks", SqlType = "varchar(4000)", IsNullable = true }],
        };

        var sql = Generator.Generate(Target, delta);

        Assert.Equal("ALTER TABLE [dbo].[Orders] ALTER COLUMN [Remarks] varchar(4000) NULL;", Assert.Single(sql));
    }

    [Fact]
    public void Generate_AddAndWiden_EmitsAddsThenAlters()
    {
        var delta = new SchemaDelta
        {
            ColumnsToAdd = [new ColumnDefinition { Name = "Notes", SqlType = "varchar(4000)", IsNullable = true }],
            ColumnsToAlter = [new ColumnDefinition { Name = "Remarks", SqlType = "varchar(4000)", IsNullable = false }],
        };

        var sql = Generator.Generate(Target, delta);

        Assert.Equal(2, sql.Count);
        Assert.Equal("ALTER TABLE [dbo].[Orders] ADD [Notes] varchar(4000) NULL;", sql[0]);
        Assert.Equal("ALTER TABLE [dbo].[Orders] ALTER COLUMN [Remarks] varchar(4000) NOT NULL;", sql[1]);
    }

    [Fact]
    public void Generate_NoChanges_EmitsNothing()
        => Assert.Empty(Generator.Generate(Target, new SchemaDelta()));
}
