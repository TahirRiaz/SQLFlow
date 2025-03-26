SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE PROCEDURE [flw].[ReplacePaths]
    @NewPathBase NVARCHAR(70),
    @OldPathPattern NVARCHAR(70) = N'/C/SQLFlow/'
AS
BEGIN

    --DECLARE @NewPathBase NVARCHAR(500) = N'/C/SQLFlow/'; -- Replace with your parameter value
    --DECLARE @OldPathPattern NVARCHAR(100) = N'/D/SQLFlow/';
    DECLARE @SQL NVARCHAR(MAX);
    DECLARE @TableName NVARCHAR(128);
    DECLARE @ColumnName NVARCHAR(128);
    DECLARE @SchemaName NVARCHAR(128);
    DECLARE @UpdateCount INT = 0;
    DECLARE @TotalUpdates INT = 0;

    -- Create a temp table to store tables with 'path' columns
    CREATE TABLE #TablesWithPathColumn
    (
        SchemaName NVARCHAR(128),
        TableName NVARCHAR(128),
        ColumnName NVARCHAR(128)
    );

    -- Find all tables with columns named 'path' or containing 'path' in the name
    INSERT INTO #TablesWithPathColumn
    (
        SchemaName,
        TableName,
        ColumnName
    )
    SELECT s.name AS SchemaName,
           t.name AS TableName,
           c.name AS ColumnName
    FROM sys.columns c
        INNER JOIN sys.tables t
            ON c.object_id = t.object_id
        INNER JOIN sys.schemas s
            ON t.schema_id = s.schema_id
    WHERE c.name = 'path'
          OR c.name LIKE '%path%'
             AND t.is_ms_shipped = 0 -- Exclude system tables
    ORDER BY s.name,
             t.name,
             c.name;

    -- Display which tables will be processed
    SELECT 'Tables to be processed:' AS Message;
    SELECT SchemaName,
           TableName,
           ColumnName
    FROM #TablesWithPathColumn;

    -- Create table to log the updates
    CREATE TABLE #UpdateLog
    (
        SchemaName NVARCHAR(128),
        TableName NVARCHAR(128),
        ColumnName NVARCHAR(128),
        UpdateCount INT
    );

    -- Print some information about what the script will do
    PRINT 'Starting search and replace operation...';
    PRINT 'Old path pattern: ' + @OldPathPattern;
    PRINT 'New path base: ' + @NewPathBase;
    PRINT '';

    -- Create a cursor to iterate through each table with a path column
    DECLARE TableCursor CURSOR FOR
    SELECT SchemaName,
           TableName,
           ColumnName
    FROM #TablesWithPathColumn;

    OPEN TableCursor;
    FETCH NEXT FROM TableCursor
    INTO @SchemaName,
         @TableName,
         @ColumnName;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        -- Build the dynamic SQL to update the table
        SET @SQL
            = N'
    BEGIN TRY
        BEGIN TRANSACTION;
        
        UPDATE [' + @SchemaName + N'].[' + @TableName + N']
        SET [' + @ColumnName + N'] = REPLACE([' + @ColumnName + N'], ''' + @OldPathPattern + N''', ''' + @NewPathBase
              + N''')
        WHERE [' + @ColumnName + N'] LIKE ''%' + @OldPathPattern
              + N'%'';
        
        DECLARE @InnerRowCount INT = @@ROWCOUNT;
        
        INSERT INTO #UpdateLog (SchemaName, TableName, ColumnName, UpdateCount)
        VALUES (''' + @SchemaName + N''', ''' + @TableName + N''', ''' + @ColumnName
              + N''', @InnerRowCount);
        
        COMMIT TRANSACTION;
        
        SELECT @InnerRowCount AS [RowCount];
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;
            
        PRINT ''Error updating table [' + @SchemaName + N'].[' + @TableName
              + N']: '' + ERROR_MESSAGE();
        
        INSERT INTO #UpdateLog (SchemaName, TableName, ColumnName, UpdateCount)
        VALUES (''' + @SchemaName + N''', ''' + @TableName + N''', ''' + @ColumnName
              + N''', -1);
        
        SELECT -1 AS [RowCount];
    END CATCH';

        -- Execute the dynamic SQL
        PRINT 'Processing table [' + @SchemaName + '].[' + @TableName + '].[' + @ColumnName + ']...';

        EXEC sp_executesql @SQL, N'@RowCount INT OUTPUT', @UpdateCount OUTPUT;

        IF @UpdateCount >= 0
        BEGIN
            PRINT 'Updated ' + CAST(@UpdateCount AS NVARCHAR(10)) + ' rows.';
            SET @TotalUpdates = @TotalUpdates + @UpdateCount;
        END;
        ELSE
        BEGIN
            PRINT 'Failed to update table.';
        END;

        PRINT '';

        FETCH NEXT FROM TableCursor
        INTO @SchemaName,
             @TableName,
             @ColumnName;
    END;

    CLOSE TableCursor;
    DEALLOCATE TableCursor;

    -- Display summary of updates
    PRINT 'Update operation completed.';
    PRINT 'Total rows updated: ' + CAST(@TotalUpdates AS NVARCHAR(10));
    PRINT '';

    -- Display detailed log
    SELECT 'Update Log:' AS Message;
    SELECT SchemaName,
           TableName,
           ColumnName,
           CASE
               WHEN UpdateCount = -1 THEN
                   'ERROR'
               ELSE
                   CAST(UpdateCount AS NVARCHAR(10))
           END AS RowsUpdated
    FROM #UpdateLog
    ORDER BY SchemaName,
             TableName,
             ColumnName;

    -- Clean up temp tables
    DROP TABLE #TablesWithPathColumn;
    DROP TABLE #UpdateLog;


END;
GO
