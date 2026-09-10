-- Two downstream stored procedures that chain off the fact table, so lineage shows sp -> sp -> sp:
--   demo.Fact_OrderSummary --(usp_BuildCountryRollup)--> demo.Fact_CountryRollup
--                          --(usp_BuildExecKpi)--------> demo.Kpi_Executive
-- Deferred name resolution means the target tables need not exist when the procedures are created; each
-- SELECT ... INTO creates its table at run time. The connected lineage tier derives each proc's reads/writes
-- from its body, which is what binds these flows to Fact_OrderSummary and to each other.
IF SCHEMA_ID('demo') IS NULL EXEC('CREATE SCHEMA demo');
GO

-- Rolls the per-customer fact up to one row per country.
CREATE OR ALTER PROCEDURE demo.usp_BuildCountryRollup
AS
BEGIN
    SET NOCOUNT ON;

    IF OBJECT_ID('demo.Fact_CountryRollup') IS NOT NULL
        DROP TABLE demo.Fact_CountryRollup;

    SELECT
        f.country,
        COUNT_BIG(*)        AS customer_count,
        SUM(f.order_count)  AS order_count,
        SUM(f.total_amount) AS total_amount
    INTO demo.Fact_CountryRollup
    FROM demo.Fact_OrderSummary AS f
    GROUP BY f.country;
END
GO

-- Reduces the country rollup to a single executive KPI row.
CREATE OR ALTER PROCEDURE demo.usp_BuildExecKpi
AS
BEGIN
    SET NOCOUNT ON;

    IF OBJECT_ID('demo.Kpi_Executive') IS NOT NULL
        DROP TABLE demo.Kpi_Executive;

    SELECT
        COUNT_BIG(*)          AS country_count,
        SUM(r.customer_count) AS customer_count,
        SUM(r.order_count)    AS order_count,
        SUM(r.total_amount)   AS total_amount
    INTO demo.Kpi_Executive
    FROM demo.Fact_CountryRollup AS r;
END
GO
