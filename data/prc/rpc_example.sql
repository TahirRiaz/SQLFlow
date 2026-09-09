DECLARE @Inputvalue NVARCHAR(255);
DECLARE @NumericVal INT;
DECLARE @Fetched BIGINT;
DECLARE @Deleted BIGINT;
DECLARE @Inserted BIGINT;
DECLARE @Updated BIGINT;

SET NOCOUNT ON;

-- Generate sample runtime values without querying any tables
-- You can modify these calculations to match your actual logic requirements
SET @Fetched = CAST(RAND() * 10000 AS BIGINT);
SET @Deleted = CAST(RAND() * 1000 AS BIGINT);
SET @Inserted = CAST(RAND() * 5000 AS BIGINT);
SET @Updated = CAST(RAND() * 3000 AS BIGINT);

-- Alternatively, you could use fixed values for testing:
-- SET @Fetched = 8754;
-- SET @Deleted = 921;
-- SET @Inserted = 4231;
-- SET @Updated = 2657;

-- Optional: Return the values as a result set for immediate viewing
SELECT @Fetched AS Fetched,
       @Deleted AS Deleted,
       @Inserted AS Inserted,
       @Updated AS Updated;
