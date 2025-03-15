SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE PROCEDURE [flw].[RebuildIndexesStats]
AS
BEGIN
    DECLARE @TableName NVARCHAR(256);
    DECLARE @SchemaName NVARCHAR(256);
    DECLARE @IndexName NVARCHAR(256);
    DECLARE @DynamicSQL NVARCHAR(MAX);

    DECLARE IndexRebuildCursor CURSOR FOR
    SELECT t.name AS TableName,
           s.name AS SchemaName,
           i.name AS IndexName
    FROM sys.indexes i
        JOIN sys.tables t
            ON i.object_id = t.object_id
        JOIN sys.schemas s
            ON t.schema_id = s.schema_id
    WHERE i.type > 0
          AND -- 0 = HEAP (no index), we don't want to process heaps
        t.is_ms_shipped = 0 -- Ignore system tables
    ORDER BY t.name,
             i.name;

    OPEN IndexRebuildCursor;

    FETCH NEXT FROM IndexRebuildCursor
    INTO @TableName,
         @SchemaName,
         @IndexName;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @DynamicSQL
            = N'ALTER INDEX [' + @IndexName + N'] ON [' + @SchemaName + N'].[' + @TableName + N'] REBUILD;';
        EXEC sp_executesql @DynamicSQL;
        FETCH NEXT FROM IndexRebuildCursor
        INTO @TableName,
             @SchemaName,
             @IndexName;
    END;

    CLOSE IndexRebuildCursor;
    DEALLOCATE IndexRebuildCursor;


    DECLARE UpdateStatsCursor CURSOR FOR
    SELECT t.name AS TableName,
           s.name AS SchemaName
    FROM sys.tables t
        JOIN sys.schemas s
            ON t.schema_id = s.schema_id
    WHERE t.is_ms_shipped = 0 -- Ignore system tables
    ORDER BY t.name;

    OPEN UpdateStatsCursor;

    FETCH NEXT FROM UpdateStatsCursor
    INTO @TableName,
         @SchemaName;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @DynamicSQL = N'UPDATE STATISTICS [' + @SchemaName + N'].[' + @TableName + N'] WITH FULLSCAN;';
        EXEC sp_executesql @DynamicSQL;
        FETCH NEXT FROM UpdateStatsCursor
        INTO @TableName,
             @SchemaName;
    END;

    CLOSE UpdateStatsCursor;
    DEALLOCATE UpdateStatsCursor;

END;
GO
