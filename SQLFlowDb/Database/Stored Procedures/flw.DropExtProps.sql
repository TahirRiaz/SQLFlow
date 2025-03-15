SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE PROCEDURE [flw].[DropExtProps]
AS
BEGIN
    -- T-SQL Script to drop all extended properties in a database
    -- First, let's display all existing extended properties
    PRINT 'Current extended properties in the database:';
    SELECT [name] AS [Property Name],
           [class] AS [Class],
           [class_desc] AS [Class Description],
           [major_id] AS [Major ID],
           OBJECT_NAME([major_id]) AS [Object Name],
           [minor_id] AS [Minor ID],
           CONVERT(NVARCHAR(MAX), [value]) AS [Property Value]
    FROM sys.extended_properties;

    -- Now generate and execute DROP statements with error handling
    DECLARE @name NVARCHAR(128);
    DECLARE @class INT;
    DECLARE @class_desc NVARCHAR(60);
    DECLARE @major_id INT;
    DECLARE @minor_id INT;
    DECLARE @level0type NVARCHAR(128);
    DECLARE @level0name NVARCHAR(128);
    DECLARE @level1type NVARCHAR(128);
    DECLARE @level1name NVARCHAR(128);
    DECLARE @level2type NVARCHAR(128);
    DECLARE @level2name NVARCHAR(128);
    DECLARE @sql NVARCHAR(MAX);
    DECLARE @success_count INT = 0;
    DECLARE @error_count INT = 0;

    -- Cursor to iterate through all properties
    DECLARE property_cursor CURSOR FOR
    SELECT [name],
           [class],
           [class_desc],
           [major_id],
           [minor_id]
    FROM sys.extended_properties
    ORDER BY [class],
             [major_id],
             [minor_id];

    OPEN property_cursor;
    FETCH NEXT FROM property_cursor
    INTO @name,
         @class,
         @class_desc,
         @major_id,
         @minor_id;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        -- Set the level values based on class
        SET @level0type = NULL;
        SET @level0name = NULL;
        SET @level1type = NULL;
        SET @level1name = NULL;
        SET @level2type = NULL;
        SET @level2name = NULL;

        -- Database level
        IF @class = 0
        BEGIN
            SET @level0type = NULL;
            SET @level0name = NULL;
        END;
        -- Schema level
        ELSE IF @class = 1
        BEGIN
            SET @level0type = N'SCHEMA';
            SET @level0name = SCHEMA_NAME(@major_id);
        END;
        -- Object level (Tables, Procedures, Functions, etc.)
        ELSE IF @class IN ( 2, 3, 4, 5, 10, 15, 16, 17, 18, 19, 20 )
        BEGIN
            SET @level0type = N'SCHEMA';
            SET @level0name = OBJECT_SCHEMA_NAME(@major_id);
            SET @level1type = CASE
                                  WHEN @class = 2 THEN
                                      N'TABLE'
                                  WHEN @class = 3 THEN
                                      N'PROCEDURE'
                                  WHEN @class = 4 THEN
                                      N'FUNCTION'
                                  WHEN @class = 5 THEN
                                      N'VIEW'
                                  WHEN @class = 10 THEN
                                      N'XML SCHEMA COLLECTION'
                                  WHEN @class = 15 THEN
                                      N'MESSAGE TYPE'
                                  WHEN @class = 16 THEN
                                      N'SERVICE CONTRACT'
                                  WHEN @class = 17 THEN
                                      N'SERVICE'
                                  WHEN @class = 18 THEN
                                      N'REMOTE SERVICE BINDING'
                                  WHEN @class = 19 THEN
                                      N'ROUTE'
                                  WHEN @class = 20 THEN
                                      N'QUEUE'
                              END;
            SET @level1name = OBJECT_NAME(@major_id);
        END;
        -- Parameter
        ELSE IF @class = 6
        BEGIN
            SET @level0type = N'SCHEMA';
            SET @level0name = OBJECT_SCHEMA_NAME(@major_id);
            SET @level1type = CASE
                                  WHEN OBJECTPROPERTY(@major_id, 'IsProcedure') = 1 THEN
                                      N'PROCEDURE'
                                  WHEN OBJECTPROPERTY(@major_id, 'IsScalarFunction') = 1
                                       OR OBJECTPROPERTY(@major_id, 'IsTableFunction') = 1 THEN
                                      N'FUNCTION'
                                  ELSE
                                      N'PROCEDURE' -- Default to procedure
                              END;
            SET @level1name = OBJECT_NAME(@major_id);
            SET @level2type = N'PARAMETER';

            -- Get parameter name
            SELECT @level2name = [name]
            FROM sys.parameters
            WHERE [object_id] = @major_id
                  AND [parameter_id] = @minor_id;
        END;
        -- Column
        ELSE IF @class = 7
        BEGIN
            SET @level0type = N'SCHEMA';
            SET @level0name = OBJECT_SCHEMA_NAME(@major_id);
            SET @level1type = CASE
                                  WHEN OBJECTPROPERTY(@major_id, 'IsTable') = 1 THEN
                                      N'TABLE'
                                  WHEN OBJECTPROPERTY(@major_id, 'IsView') = 1 THEN
                                      N'VIEW'
                                  ELSE
                                      N'TABLE' -- Default to table
                              END;
            SET @level1name = OBJECT_NAME(@major_id);
            SET @level2type = N'COLUMN';

            -- Get column name
            SELECT @level2name = [name]
            FROM sys.columns
            WHERE [object_id] = @major_id
                  AND [column_id] = @minor_id;
        END;
        -- Index
        ELSE IF @class = 8
        BEGIN
            SET @level0type = N'SCHEMA';
            SET @level0name = OBJECT_SCHEMA_NAME(@major_id);
            SET @level1type = N'TABLE';
            SET @level1name = OBJECT_NAME(@major_id);
            SET @level2type = N'INDEX';

            -- Get index name
            SELECT @level2name = [name]
            FROM sys.indexes
            WHERE [object_id] = @major_id
                  AND [index_id] = @minor_id;
        END;

        -- Build and execute the DROP statement
        BEGIN TRY
            SET @sql = N'EXEC sys.sp_dropextendedproperty @name = N''' + @name + N'''';

            IF @level0type IS NOT NULL
                SET @sql = @sql + N', @level0type = N''' + @level0type + N'''';
            ELSE
                SET @sql = @sql + N', @level0type = NULL';

            IF @level0name IS NOT NULL
                SET @sql = @sql + N', @level0name = N''' + @level0name + N'''';
            ELSE
                SET @sql = @sql + N', @level0name = NULL';

            IF @level1type IS NOT NULL
                SET @sql = @sql + N', @level1type = N''' + @level1type + N'''';
            ELSE
                SET @sql = @sql + N', @level1type = NULL';

            IF @level1name IS NOT NULL
                SET @sql = @sql + N', @level1name = N''' + @level1name + N'''';
            ELSE
                SET @sql = @sql + N', @level1name = NULL';

            IF @level2type IS NOT NULL
                SET @sql = @sql + N', @level2type = N''' + @level2type + N'''';
            ELSE
                SET @sql = @sql + N', @level2type = NULL';

            IF @level2name IS NOT NULL
                SET @sql = @sql + N', @level2name = N''' + @level2name + N'''';
            ELSE
                SET @sql = @sql + N', @level2name = NULL';

            PRINT 'Executing: ' + @sql;
            EXEC sp_executesql @sql;
            PRINT 'Success: Dropped property ' + @name;
            SET @success_count = @success_count + 1;
        END TRY
        BEGIN CATCH
            PRINT 'Error: Failed to drop property ' + @name;
            PRINT 'Error Message: ' + ERROR_MESSAGE();
            SET @error_count = @error_count + 1;
        END CATCH;

        FETCH NEXT FROM property_cursor
        INTO @name,
             @class,
             @class_desc,
             @major_id,
             @minor_id;
    END;

    CLOSE property_cursor;
    DEALLOCATE property_cursor;

    -- Print summary
    PRINT '-------------------------------';
    PRINT 'Summary:';
    PRINT 'Successfully dropped ' + CAST(@success_count AS NVARCHAR) + ' extended properties';
    PRINT 'Failed to drop ' + CAST(@error_count AS NVARCHAR) + ' extended properties';

    -- Show remaining properties if any
    IF EXISTS (SELECT 1 FROM sys.extended_properties)
    BEGIN
        PRINT '';
        PRINT 'Remaining extended properties in the database:';
        SELECT [name] AS [Property Name],
               [class] AS [Class],
               [class_desc] AS [Class Description],
               [major_id] AS [Major ID],
               OBJECT_NAME([major_id]) AS [Object Name],
               [minor_id] AS [Minor ID],
               CONVERT(NVARCHAR(MAX), [value]) AS [Property Value]
        FROM sys.extended_properties;
    END;
    ELSE
    BEGIN
        PRINT 'All extended properties have been successfully dropped.';
    END;

END;
GO
