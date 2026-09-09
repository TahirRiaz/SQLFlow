-- Demo fact build: joins the two ingested targets into a per-customer order summary.
-- Recreated wholesale each run (a demo-sized fact); the connected lineage tier derives
-- "reads demo.Orders, reads demo.Customers, writes demo.Fact_OrderSummary" from this body.
IF SCHEMA_ID('demo') IS NULL EXEC('CREATE SCHEMA demo');
GO
CREATE OR ALTER PROCEDURE demo.usp_BuildOrderFact
AS
BEGIN
    SET NOCOUNT ON;

    IF OBJECT_ID('demo.Fact_OrderSummary') IS NOT NULL
        DROP TABLE demo.Fact_OrderSummary;

    SELECT
        c.customer_id,
        c.name,
        c.country,
        COUNT_BIG(o.order_id)      AS order_count,
        SUM(o.amount)              AS total_amount,
        MIN(o.order_date)          AS first_order_date,
        MAX(o.order_date)          AS last_order_date
    INTO demo.Fact_OrderSummary
    FROM demo.Orders AS o
    INNER JOIN demo.Customers AS c
        ON c.customer_id = o.customer_id
    GROUP BY c.customer_id, c.name, c.country;
END
GO
