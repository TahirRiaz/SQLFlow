using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <summary>
    /// Gives every finished run recorded without a duration the one its own start and end instants say. Until now a run
    /// had a duration only when its flow kind reported one in a form the projection read: an ingestion run reports its
    /// duration as a fraction of a second, which was read as a whole number and dropped, and a kind a host module
    /// registers reports none at all, so their runs showed when they ran and never how long. Runs that never started
    /// (cancelled or skipped while queued) have no start, and keep no duration; a run whose end is recorded before its
    /// start keeps none either. <c>DATEDIFF_BIG</c> counts the milliseconds, so a run of any length is timed exactly.
    /// </summary>
    public partial class RunDurationFromStartAndEnd : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE [catalog].[Run]
                SET [DurationSeconds] = DATEDIFF_BIG(millisecond, [StartUtc], [EndUtc]) / 1000.0
                WHERE [DurationSeconds] IS NULL
                  AND [StartUtc] IS NOT NULL
                  AND [EndUtc] IS NOT NULL
                  AND [EndUtc] >= [StartUtc];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The durations filled in are what each run's own instants say, and a duration the run recorded itself cannot
            // be told apart from one filled in here, so going back leaves every run timed rather than guessing which to clear.
        }
    }
}
