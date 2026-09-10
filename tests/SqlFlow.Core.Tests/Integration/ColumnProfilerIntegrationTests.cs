using System.Globalization;
using SqlFlow.Core.Model;
using SqlFlow.SqlServer;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The server-side column profiler against the physical sink: all columns are aggregated over one shared
/// sample scan, and a table wide enough to overflow a single SELECT list is profiled in batches over one
/// materialized sample. These pin the per-column counts the inferencer consumes.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ColumnProfilerIntegrationTests
{
    [SkippableFact]
    public async Task WideTable_ProfilesEveryColumn_AcrossBatchesOverOneSample()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_Profile_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        // 60 columns forces several aggregate batches (and the materialized shared sample). Even columns
        // carry integer strings, odd ones carry text, so each profile is distinguishable per column.
        const int columnCount = 60;
        var names = Enumerable.Range(0, columnCount)
            .Select(i => "C" + i.ToString("D2", CultureInfo.InvariantCulture))
            .ToArray();
        try
        {
            var definitions = string.Join(", ", names.Select(n => $"[{n}] varchar(255) NULL"));
            await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{table}] ({definitions});");
            for (var row = 0; row < 3; row++)
            {
                var values = string.Join(", ", Enumerable.Range(0, columnCount)
                    .Select(i => i % 2 == 0 ? $"'{100 + row + i}'" : $"'txt{row}'"));
                await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{table}] VALUES ({values});");
            }

            var profiles = await new SqlServerColumnProfiler().ProfileAsync(
                cs, IntegrationDb.Schema, table, names,
                new TypeInferencePolicy { Enabled = true, SampleSize = 1000 },
                ServerLocale.Invariant);

            Assert.Equal(columnCount, profiles.Count);
            for (var i = 0; i < columnCount; i++)
            {
                var profile = profiles[i];
                Assert.Equal(names[i], profile.ColumnName);
                Assert.Equal(3, profile.Total);
                Assert.Equal(3, profile.NonNull);
                if (i % 2 == 0)
                {
                    Assert.Equal(3, profile.AsBigInt);
                    Assert.Equal(100 + i, profile.MinValue);
                    Assert.Equal(102 + i, profile.MaxValue);
                }
                else
                {
                    Assert.Equal(0, profile.AsBigInt);
                    Assert.Equal(4, profile.MaxLen); // 'txt0'..'txt2'
                }
            }
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task WeirdColumnNames_AreEscaped_AndNullsCounted()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_Profile_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        try
        {
            await IntegrationDb.ExecuteAsync(cs,
                $"CREATE TABLE [dbo].[{table}] ([Wei]]rd Col] varchar(50) NULL, [Plain] varchar(50) NULL);");
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [dbo].[{table}] VALUES ('42', NULL), (NULL, 'x'), ('7', 'y');");

            var profiles = await new SqlServerColumnProfiler().ProfileAsync(
                cs, IntegrationDb.Schema, table, ["Wei]rd Col", "Plain"],
                new TypeInferencePolicy { Enabled = true },
                ServerLocale.Invariant);

            Assert.Equal(2, profiles.Count);
            var weird = profiles.Single(p => p.ColumnName == "Wei]rd Col");
            Assert.Equal(3, weird.Total);
            Assert.Equal(2, weird.NonNull);
            Assert.Equal(2, weird.AsBigInt);
            Assert.Equal(7, weird.MinValue);
            Assert.Equal(42, weird.MaxValue);

            var plain = profiles.Single(p => p.ColumnName == "Plain");
            Assert.Equal(2, plain.NonNull);
            Assert.Equal(0, plain.AsBigInt);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }
}
