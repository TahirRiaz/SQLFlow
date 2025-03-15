SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO



/*
  ##################################################################################################################################################
  -- Name				:   [flw].[BuildIngestionDS]
  -- Date				:   2020.11.06
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedures generates and prints an SQL command to fetch metadata for a specific source table in a database, in order to build a dataset for ingestion. 
							The generated SQL command retrieves information such as key columns, date columns, and batch size, which can be useful in designing an ETL or data processing pipeline.
  -- Summary			:	The stored procedure accepts the following input parameters:

							@SysAlias: The alias name for the system (default value: 'AW2019').
							@srcServer: The source server name (default value: 'src').
							@srcTable: The source table name (default value: '').
							@trgServer: The target server name (default value: 'dwh_prod').
							@dbg: Debug flag (default value: 0).
							The stored procedure performs the following actions:

							It initializes variables and sets default values based on the input parameters and other configuration settings.
							It splits the source object name and fetches the object ID.
							It fetches target database and schema names, and the default target server.
							It builds target values and sets source and target table names.
							It generates an SQL command to fetch the metadata for the specified source table, including key columns, date columns, and batch size.
							The generated SQL command can then be executed to retrieve the necessary metadata for the source table, which can be used in further processing or analysis.

							If the @dbg flag is set to a value greater than 0, the stored procedure also prints debug information, such as the values of variables and the generated SQL command.
  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2020.11.06		Initial
  ##################################################################################################################################################
*/

CREATE PROCEDURE [flw].[BuildIngestionDS]
    @SysAlias NVARCHAR(255) = N'AW2019',
    @srcServer NVARCHAR(255) = N'src',
    @srcTable NVARCHAR(255) = N'',
	@trgServer NVARCHAR(255) = N'dwh_prod',
    --@Exec BIT = 0,
    @dbg INT = 0
AS
BEGIN
    --Input
    --DECLARE @srcTable  NVARCHAR(255) = N'AdventureWorks2019.[HumanResources].[EmployeePayHistory]',
    --        @SysAlias  NVARCHAR(255) = N'AW2019',
    --        @srcServer NVARCHAR(255) = N'src';

    



    --Input
    --DECLARE @srcTable  NVARCHAR(255) = N'AdventureWorks2019.[HumanResources].[EmployeePayHistory]',
    --        @SysAlias  NVARCHAR(255) = N'AW2019',
    --        @srcServer NVARCHAR(255) = N'src';

    DECLARE @curObjName NVARCHAR(255),
            @curExecCmd NVARCHAR(4000),
            @curSection NVARCHAR(4000),
            @curCode    NVARCHAR(MAX);
    IF @dbg >= 2
    BEGIN
        SET @curObjName = ISNULL(OBJECT_SCHEMA_NAME(@@PROCID) + '.' + OBJECT_NAME(@@PROCID), 'Defualt SP Name');
        SET @curExecCmd
            = N'exec ' + @curObjName + N' @SysAlias=''' + @SysAlias + N''', @srcServer=''' + @srcServer
              + N''', @srcTable=''' + @srcTable + N''', @dbg=''' + CAST(@dbg AS NVARCHAR(255)) + N''';';
        PRINT [flw].[GetLogHeader](@curObjName, @curExecCmd);
    END;
    --Ensure that debug info is only printed when called directly.
    IF (@@NESTLEVEL > 1 AND @dbg = 1)
        SET @dbg = 0;

    --Drived values
    DECLARE @trgDBName          NVARCHAR(255)  = N'',
            @Schema04Trusted    NVARCHAR(255)  = N'',
            @srcDatabase        NVARCHAR(255),
            @srcSchema          NVARCHAR(255),
            @srcObject          NVARCHAR(255),
            @srcObjectID        NVARCHAR(255),
            @DefaultTrgSrv      NVARCHAR(255),
            @DefualtDateColList NVARCHAR(1024),
            @srcSchTbl          NVARCHAR(255),
            @DateColList        NVARCHAR(1024) = N'';

    --Target values
    DECLARE @srcDBSchTbl NVARCHAR(255)  = @srcTable,
            @trgDBSchTbl NVARCHAR(255)  = N'',
            @KeyColumns  NVARCHAR(1024) = N'',
            @DateColumn  NVARCHAR(255)  = N'';

    --Split the source object name and fetch objectid
    SELECT @srcDatabase = parsename(@srcTable,3),
                             @srcSchema = parsename(@srcTable,2),
                             @srcObject = parsename(@srcTable,1);

    -- Fetch Target Database name        
    SELECT @trgDBName = [flw].[GetDefaultTrgDB] ()
   
    IF @dbg > 1
    BEGIN
        SET @curSection = N'10 :: ' + @curObjName + N' :: Fetch Target Database name';
        SET @curCode = @trgDBName;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;

    -- Fetch Target Schema Name  
    SELECT @Schema04Trusted = [flw].[GetCFGParamVal]('Schema04Trusted')

    IF @dbg > 1
    BEGIN
        SET @curSection = N'20 :: ' + @curObjName + N' :: Fetch Target Schema Name';
        SET @curCode = @Schema04Trusted;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;

    -- Fetch Default Target Server
    SELECT @DefaultTrgSrv = [flw].[GetCFGParamVal]('DefaultTrgSrv')

    IF @dbg > 1
    BEGIN
        SET @curSection = N'30 :: ' + @curObjName + N' :: Fetch Default Target Server';
        SET @curCode = @DefaultTrgSrv;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;

	IF(LEN(@trgServer)>1) 
	BEGIN 
		SET @DefaultTrgSrv = @trgServer
	END

    -- Fetch DefualtDateColList
    SELECT @DefualtDateColList = [flw].[GetCFGParamVal]('DefualtDateColList')

    IF @dbg > 1
    BEGIN
        SET @curSection = N'40 :: ' + @curObjName + N' :: Fetch DefualtDateColList';
        SET @curCode = @DefualtDateColList;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;


    --Build Target values
    SET @srcDBSchTbl = ISNULL(N'[' + @srcDatabase + N'].[' + @srcSchema + N'].[' + @srcObject + N']', '');

    IF @dbg > 1
    BEGIN
        SET @curSection = N'50 :: ' + @curObjName + N' :: Build Target values';
        SET @curCode = flw.GetValidSrcTrgName(@srcDBSchTbl);
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;

    --SET @trgDBSchTbl = N'[' + @trgDBName + N'].[' + @Schema04Trusted + N'].[' + @srcObject + N']';
    SET @srcSchTbl = ISNULL(N'[' + @srcSchema + N'].[' + @srcObject + N']', '');

    IF @dbg > 1
    BEGIN
        SET @curSection = N'60 :: ' + @curObjName + N' :: @srcDBSchTbl Value';
        SET @curCode = flw.GetValidSrcTrgName(@srcDBSchTbl);
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;

    SELECT @DateColList = @DateColList + N',''' + Item + N''''
      FROM [flw].[StringSplit](@DefualtDateColList, ',');

    SET @DateColList = REPLACE(SUBSTRING(@DateColList, 2, LEN(@DateColList)), '''', '''');

    IF @dbg > 1
    BEGIN
        SET @curSection = N'70 :: ' + @curObjName + N' :: @DateColList Value';
        SET @curCode = @DateColList;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;

    DECLARE @sqlCMD NVARCHAR(MAX);
    SET @sqlCMD
        = N';WITH BaseVal AS (SELECT ''' + @SysAlias + N''' AS SysAlias,
             '''  + @srcServer + N''' AS srcServer,
             '''  + @srcTable + N''' AS srcDBSchTbl,
             '''  + @srcSchTbl + N''' AS srcSchTbl,
             '''  + @DefaultTrgSrv + N''' AS DefaultTrgSrv,
             '''  + @trgDBName + N''' AS DefaultTrgDB,
             '''  + @Schema04Trusted
          + N''' AS Schema04Trusted),
rCount AS (SELECT      ''['' + SCHEMA_NAME(sOBJ.schema_id) + ''].['' + (sOBJ.name) + '']'' AS [TableName], SUM(sPTN.rows) AS [TblCount]
         FROM sys.objects AS sOBJ
        INNER JOIN sys.partitions AS sPTN ON sOBJ.object_id = sPTN.object_id
        WHERE sOBJ.type  = ''U'' AND  sOBJ.is_ms_shipped = 0x0 AND  index_id  < 2 -- 0:Heap, 1:Clustered
        GROUP BY sOBJ.schema_id,sOBJ.name)
SELECT       SysAlias,
             srcServer,
             CASE WHEN LEN(srcDBSchTbl) > 0 THEN srcDBSchTbl ELSE ''['' + TABLE_CATALOG + ''].['' + TABLE_SCHEMA + ''].['' + TABLE_NAME + '']'' END srcDBSchTbl,
             DefaultTrgSrv AS trgServer,
             ''['' + DefaultTrgDB + ''].['' + Schema04Trusted + ''].['' + TABLE_NAME + '']'' AS trgDBSchTbl,
             STUFF(
                 ( SELECT N'', ['' + cols.COLUMN_NAME + '']''
                       FROM INFORMATION_SCHEMA.COLUMNS AS cols WITH (NOLOCK)
                      WHERE 1 = 1 AND ''['' + cols.TABLE_SCHEMA + ''].['' + cols.TABLE_NAME + '']'' = CASE WHEN LEN(srcSchTbl) > 0 THEN srcSchTbl ELSE ''['' + tbl.TABLE_SCHEMA + ''].['' + tbl.TABLE_NAME + '']'' END
                        AND cols.COLUMN_NAME IN (   SELECT      kcu.COLUMN_NAME
                                                      FROM      INFORMATION_SCHEMA.TABLE_CONSTRAINTS AS tc WITH (NOLOCK)
INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE AS kcu WITH (NOLOCK)
ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME AND kcu.TABLE_CATALOG   = tc.TABLE_CATALOG AND kcu.TABLE_SCHEMA    = tc.TABLE_SCHEMA AND kcu.TABLE_NAME      = tc.TABLE_NAME AND tc.TABLE_CATALOG    = cols.TABLE_CATALOG AND tc.TABLE_SCHEMA     = cols.TABLE_SCHEMA AND tc.TABLE_NAME       = cols.TABLE_NAME
                                                     WHERE      (tc.CONSTRAINT_TYPE = ''PRIMARY KEY''))
                     FOR XML PATH(''''), TYPE).value(''text()[1]'', ''nvarchar(max)''),
                 1,
                 2,
                 N'''') [KeyColumns],
             (   SELECT TOP 1 COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS c WHERE COLUMN_NAME IN (' + @DateColList
          + N')
                    AND ''['' + c.TABLE_SCHEMA + ''].['' + c.TABLE_NAME + '']'' = CASE WHEN LEN(srcSchTbl) > 0 THEN srcSchTbl ELSE ''['' + tbl.TABLE_SCHEMA + ''].['' + tbl.TABLE_NAME + '']'' END) as DateColumn,
			(   SELECT CASE WHEN [TblCount] < 50000 THEN ''Small'' WHEN [TblCount] BETWEEN 50000 AND 1000000 THEN ''Medium'' ELSE ''Large'' END
FROM rCount WHERE ''['' + TABLE_SCHEMA + ''].['' + TABLE_NAME + '']'' = [TableName]) as [BatchSize]
  FROM       INFORMATION_SCHEMA.TABLES AS tbl
 CROSS APPLY BaseVal
 WHERE (TABLE_TYPE  = ''BASE TABLE'') AND ''['' + TABLE_SCHEMA + ''].['' + TABLE_NAME + '']'' = CASE WHEN LEN(srcSchTbl) > 0 THEN srcSchTbl ELSE ''['' + TABLE_SCHEMA + ''].['' + TABLE_NAME + '']'' END;';
	PRINT @sqlCMD

    IF @dbg >= 1
    BEGIN
        SET @curSection = N'80 :: ' + @curObjName + N' :: @sqlCMD Value';
        SET @curCode = @sqlCMD;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;

    --IF @Exec = 1
    --BEGIN
    --    EXEC @sqlCMD;
    --END;







END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedures generates and prints an SQL command to fetch metadata for a specific source table in a database, in order to build a dataset for ingestion. 
							The generated SQL command retrieves information such as key columns, date columns, and batch size, which can be useful in designing an ETL or data processing pipeline.
  -- Summary			:	The stored procedure accepts the following input parameters:

							@SysAlias: The alias name for the system (default value: ''AW2019'').
							@srcServer: The source server name (default value: ''src'').
							@srcTable: The source table name (default value: '''').
							@trgServer: The target server name (default value: ''dwh_prod'').
							@dbg: Debug flag (default value: 0).
							The stored procedure performs the following actions:

							It initializes variables and sets default values based on the input parameters and other configuration settings.
							It splits the source object name and fetches the object ID.
							It fetches target database and schema names, and the default target server.
							It builds target values and sets source and target table names.
							It generates an SQL command to fetch the metadata for the specified source table, including key columns, date columns, and batch size.
							The generated SQL command can then be executed to retrieve the necessary metadata for the source table, which can be used in further processing or analysis.

							If the @dbg flag is set to a value greater than 0, the stored procedure also prints debug information, such as the values of variables and the generated SQL command.', 'SCHEMA', N'flw', 'PROCEDURE', N'BuildIngestionDS', NULL, NULL
GO
