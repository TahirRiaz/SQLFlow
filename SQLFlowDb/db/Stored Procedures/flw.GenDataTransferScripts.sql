SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GenDataTransferScripts]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure is intended to copy all the tables, including their data and identity column settings, from a source database to a destination database.
							It does so by creating a cursor and iterating through the list of base tables in the source database with the schema 'flw'.
							Spesifically designed for transforming data from a an older SQLFlow data base to a newer schema.
							
  -- Summary			:	Here's an overview of how the script works:

							It starts by declaring a set of variables, such as @tableName, @HasIdent, @ColList, and @OldDBName, which will be used to store the table names, identity column information, 
							column names, and source database name, respectively.

							It then retrieves all base tables in the 'flw' schema from the source database (specified by the @OldDBName variable), along with information about whether the table has an identity column. 
							This information is obtained by joining the INFORMATION_SCHEMA.TABLES and SYS.IDENTITY_COLUMNS views.

							The script opens a cursor, which will loop through each table in the 'flw' schema, and fetches the table name and identity column information into the @tableName and @HasIdent variables, respectively.

							Inside the cursor loop, the script constructs a dynamic SQL command to copy the data from the source to the destination database. 
							The script first generates the list of column names for each table and stores it in the @ColList variable.

							It then constructs the dynamic SQL command, which includes:

							A TRUNCATE TABLE statement to clear any existing data from the destination table.
							A conditional SET IDENTITY_INSERT statement to enable identity insert if the table has an identity column.
							An INSERT INTO statement to copy the data from the source to the destination table.
							Another conditional SET IDENTITY_INSERT statement to disable identity insert if the table has an identity column.
							The script prints the dynamic SQL command for each table.

							Finally, it fetches the next table from the cursor and repeats the process until all tables have been processed. The cursor is then closed and deallocated.

							Please note that this script only prints the generated SQL commands; it doesn't execute them. To execute the commands, 
							you can either copy and run them manually or modify the script to use sp_executesql to execute the commands dynamically.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[GenDataTransferScripts]
AS
BEGIN

    DECLARE @ColList NVARCHAR(MAX) = N'';
    DECLARE @tableName NVARCHAR(255) = N'';
    DECLARE @HasIdent NVARCHAR(255) = N'';
    DECLARE @OldDBName NVARCHAR(255) = N'SQLFlow';
    DECLARE @NewDBName NVARCHAR(255) = N'SQLFlowV2';

    DECLARE cRec CURSOR FOR
    SELECT table_name,
           CASE
               WHEN name IS NULL THEN
                   0
               ELSE
                   1
           END AS HasIdent
    FROM INFORMATION_SCHEMA.TABLES tbl
        LEFT OUTER JOIN SYS.IDENTITY_COLUMNS iCol
            ON tbl.TABLE_NAME = OBJECT_NAME(iCol.OBJECT_ID)
               AND tbl.TABLE_SCHEMA = OBJECT_SCHEMA_NAME(iCol.object_id)
    WHERE TABLE_SCHEMA = 'flw'
          AND table_type = 'BASE TABLE';


    OPEN cRec;
    FETCH NEXT FROM cRec
    INTO @tableName,
         @HasIdent;

    WHILE (@@FETCH_STATUS = 0)
    BEGIN
        SET @ColList = N'';
        SELECT @ColList = @ColList + N',' + COLUMN_NAME
        FROM INFORMATION_SCHEMA.COLUMNS
        WHERE TABLE_SCHEMA = 'flw'
              AND TABLE_NAME = @tableName;

        SET @ColList = SUBSTRING(@ColList, 2, LEN(@ColList));

        DECLARE @cmd NVARCHAR(MAX);
        SET @cmd

            = 'TRUNCATE TABLE ' + ' FLW.' + @tableName + CHAR(13) + CHAR(10) +
				CASE
                  WHEN @HasIdent = 0 THEN
                      ''
                  ELSE
                      'SET IDENTITY_INSERT FLW.' + @tableName + ' ON '
              END + CHAR(13) + CHAR(10) + N' INSERT INTO FLW.' + @tableName + N'(' + @ColList + N')' + CHAR(13)
              + CHAR(10) + N' SELECT ' + @ColList + N' FROM '+@OldDBName+'.FLW.' + @tableName + CHAR(13) + CHAR(10)
              + CASE
                    WHEN @HasIdent = 0 THEN
                        ''
                    ELSE
                        'SET IDENTITY_INSERT FLW.' + @tableName + ' OFF '
                END + CHAR(13) + CHAR(10) + CHAR(13) + CHAR(10);
        PRINT @cmd;
        FETCH NEXT FROM cRec
        INTO @tableName,
             @HasIdent;
    END;


    CLOSE cRec;
    DEALLOCATE cRec;


END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Company			:   Business IQ
  -- Purpose			:   This stored procedure is intended to copy all the tables, including their data and identity column settings, from a source database to a destination database.
							It does so by creating a cursor and iterating through the list of base tables in the source database with the schema ''flw''.
							Spesifically designed for transforming data from a an older SQLFlow data base to a newer schema.
							
  -- Summary			:	Here''s an overview of how the script works:

							It starts by declaring a set of variables, such as @tableName, @HasIdent, @ColList, and @OldDBName, which will be used to store the table names, identity column information, 
							column names, and source database name, respectively.

							It then retrieves all base tables in the ''flw'' schema from the source database (specified by the @OldDBName variable), along with information about whether the table has an identity column. 
							This information is obtained by joining the INFORMATION_SCHEMA.TABLES and SYS.IDENTITY_COLUMNS views.

							The script opens a cursor, which will loop through each table in the ''flw'' schema, and fetches the table name and identity column information into the @tableName and @HasIdent variables, respectively.

							Inside the cursor loop, the script constructs a dynamic SQL command to copy the data from the source to the destination database. 
							The script first generates the list of column names for each table and stores it in the @ColList variable.

							It then constructs the dynamic SQL command, which includes:

							A TRUNCATE TABLE statement to clear any existing data from the destination table.
							A conditional SET IDENTITY_INSERT statement to enable identity insert if the table has an identity column.
							An INSERT INTO statement to copy the data from the source to the destination table.
							Another conditional SET IDENTITY_INSERT statement to disable identity insert if the table has an identity column.
							The script prints the dynamic SQL command for each table.

							Finally, it fetches the next table from the cursor and repeats the process until all tables have been processed. The cursor is then closed and deallocated.

							Please note that this script only prints the generated SQL commands; it doesn''t execute them. To execute the commands, 
							you can either copy and run them manually or modify the script to use sp_executesql to execute the commands dynamically.', 'SCHEMA', N'flw', 'PROCEDURE', N'GenDataTransferScripts', NULL, NULL
GO
