SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO



/*
  ##################################################################################################################################################
  -- Name				:   [flw].[TokenizeRaw]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure tokenizes data. Works only OnPrem solutions and Azure support require a rewrite due to cross-database scripting. 

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[TokenizeRaw]
    @rawTable NVARCHAR(255) = '', -- Staging table
    @BK NVARCHAR(255) = '', -- Business Key/ Primary Key
    @SyncSchema BIT = 1, -- SyncSchema from [flw].[Ingestion]
    @FlowID NVARCHAR(255) = 7, -- FlowID from [flw].[Ingestion]
    @dbg INT = 0 -- Debug level
AS
BEGIN

    --DECLARE @rawTable NVARCHAR(255) = N'[SEODS].raw.[Employee]',
    --        @BK NVARCHAR(255) = N'[BusinessEntityID]',
    --        @SyncSchema BIT = 1,
    --        @FlowID INT = 7,
    --        @dbg INT = 1;
    SET NOCOUNT ON;

	set @BK = flw.FormListWithBrackets(@BK,',') 
	
    DECLARE @srcObjectID             NVARCHAR(255),
            @srcDatabase             NVARCHAR(255)  = N'',
            @srcSchema               NVARCHAR(255)  = N'',
            @srcObject               NVARCHAR(255)  = N'',
            @vltDatabase             NVARCHAR(255)  = N'',
            @ColumnsList             NVARCHAR(MAX)  = N'',
            @vltColumnList           NVARCHAR(MAX)  = N'',
            @vltViewColumnList       NVARCHAR(MAX)  = N'',
            @dtkViewColumnList       NVARCHAR(MAX)  = N'',
            @SQLCMD                  NVARCHAR(MAX)  = N'',
            @TempTableNameFull       NVARCHAR(255)  = N'',
            @VaultTableNameFull      NVARCHAR(255)  = N'',
            @VaultViewNameFull       NVARCHAR(255)  = N'',
            @TrustedTableNameFull    NVARCHAR(255)  = N'',
            @TempTableName           NVARCHAR(255)  = N'',
            @VaultTableName          NVARCHAR(255)  = N'',
            @TrustedTableName        NVARCHAR(255)  = N'',
            @VaultViewName           NVARCHAR(255)  = N'',
            @Schema02Temp            NVARCHAR(255)  = N'',
            @Schema03Vault           NVARCHAR(255)  = N'',
            @Schema04Trusted         NVARCHAR(255)  = N'',
            @Schema05Detokenize      NVARCHAR(255)  = N'',
            @Schema06Version         NVARCHAR(255)  = N'',
            @DetokenizeViewNameFull  NVARCHAR(255)  = N'',
            @DetokenizeViewName      NVARCHAR(255)  = N'',
            @VersionViewNameFull     NVARCHAR(255)  = N'',
            @VersionViewName         NVARCHAR(255)  = N'',
            @VaultDelSPNameFull      NVARCHAR(255)  = N'',
            @VaultDelSPName          NVARCHAR(255)  = N'',
            @upsExcludeUpdateColumns NVARCHAR(4000) = N'';
    --LastUpdated


    DECLARE @curObjName NVARCHAR(255),
            @curExecCmd NVARCHAR(4000),
            @curSection NVARCHAR(4000),
            @curCode    NVARCHAR(MAX);

    IF (@dbg > 1)
    BEGIN
        SET @curObjName = ISNULL(OBJECT_SCHEMA_NAME(@@PROCID) + '.' + OBJECT_NAME(@@PROCID), 'Defualt SP Name');
        SET @curExecCmd
            = N'exec ' + @curObjName + N' @rawTable=''' + @rawTable + N''',@BK=''' + @BK + N''',@SyncSchema='
              + CAST(@SyncSchema AS NVARCHAR(255)) + N',@FlowID=' + @FlowID + N', @dbg=' + CAST(@dbg AS NVARCHAR(20));
        PRINT [flw].[GetLogHeader](@curObjName, @curExecCmd);
    END;

    IF (@@NESTLEVEL > 1 AND @dbg = 1)
        SET @dbg = 0;

    SET @srcObjectID = OBJECT_ID(@rawTable);

    SELECT @srcDatabase = [flw].[RemBrackets]([1]),
           @srcSchema = [flw].[RemBrackets]([2]),
           @srcObject = [flw].[RemBrackets]([3])
      FROM (   SELECT Ordinal,
                      Item
                 FROM flw.StringSplit(@rawTable, '.') ) AS SourceTable
      PIVOT (   MAX(Item)
                FOR Ordinal IN ([1], [2], [3])) AS PivotTable;


    --SELECT OBJECT_ID('[AzureDB].raw.[Employee]')

    SELECT      @upsExcludeUpdateColumns = @upsExcludeUpdateColumns + N',' + t.ColumnName
      FROM      flw.IngestionTokenExp AS te
     INNER JOIN flw.IngestionTokenize AS t
        ON te.TokenExpAlias = t.TokenExpAlias
     WHERE      1    = 1
       AND      te.[TokenExpAlias] LIKE '%Rand%'
       AND      (t.FlowID = @FlowID);

    IF @dbg > 1
    BEGIN
        SET @curSection = N'10 :: ' + @curObjName + N' :: Variable @upsExcludeUpdateColumns value';
        SET @curCode = @upsExcludeUpdateColumns;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;

    --Fetch Temp Schema Name
    SELECT @Schema02Temp = [flw].[RemBrackets](ParamValue)
      FROM flw.SysCFG
     WHERE ParamName = 'Schema02Temp';

    IF @dbg > 1
    BEGIN
        SET @curSection = N'20 :: ' + @curObjName + N' :: Fetch Temp Schema Name';
        SET @curCode = @Schema02Temp;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;

    --Fetch Temp Schema Name
    SELECT @Schema03Vault = [flw].[RemBrackets](ParamValue)
      FROM flw.SysCFG
     WHERE ParamName = 'Schema03Vault';

    IF @dbg > 1
    BEGIN
        SET @curSection = N'30 :: ' + @curObjName + N' :: Fetch Temp Schema Name';
        SET @curCode = @Schema03Vault;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;

    --Fetch Vault Database Name
    SELECT @vltDatabase = [flw].[RemBrackets](ParamValue)
      FROM flw.SysCFG
     WHERE ParamName = 'VaultDB';

    IF @dbg > 1
    BEGIN
        SET @curSection = N'40 :: ' + @curObjName + N' :: Fetch Vault Database Name';
        SET @curCode = @vltDatabase;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;

    --Fetch Trusted Schema Name
    SELECT @Schema04Trusted = [flw].[RemBrackets](ParamValue)
      FROM flw.SysCFG
     WHERE ParamName = 'Schema04Trusted';

    IF @dbg > 1
    BEGIN
        SET @curSection = N'50 :: ' + @curObjName + N' :: Fetch Trusted Schema Name';
        SET @curCode = @Schema04Trusted;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;

    --
    SELECT @Schema05Detokenize = [flw].[RemBrackets](ParamValue)
      FROM flw.SysCFG
     WHERE ParamName = 'Schema05Detokenize';

    IF @dbg > 1
    BEGIN
        SET @curSection = N'60 :: ' + @curObjName + N' :: Variable @Schema05Detokenize value';
        SET @curCode = @Schema05Detokenize;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;

    SELECT @Schema06Version = ParamValue
      FROM flw.SysCFG
     WHERE (ParamName = N'Schema06Version');

    IF @dbg > 1
    BEGIN
        SET @curSection = N'61 :: ' + @curObjName + N' :: Variable @Schema06Version value';
        SET @curCode = @Schema06Version;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;

    --Create Token Object Names
    SET @TempTableNameFull = N'[' + @vltDatabase + N'].[' + @Schema02Temp + N'].[' + @srcObject + N']';
    SET @VaultTableNameFull = N'[' + @vltDatabase + N'].[' + @Schema03Vault + N'].[' + @srcObject + N']';
    SET @VaultViewNameFull = N'[' + @vltDatabase + N'].[' + @Schema03Vault + N'].[v_' + @srcObject + N']';
    SET @TrustedTableNameFull = N'[' + @srcDatabase + N'].[' + @Schema04Trusted + N'].[' + @srcObject + N']';
    SET @DetokenizeViewNameFull = N'[' + @srcDatabase + N'].[' + @Schema05Detokenize + N'].[' + @srcObject + N']';
    SET @VersionViewNameFull = N'[' + @vltDatabase + N'].[' + @Schema06Version + N'].[' + @srcObject + N']';
    SET @VaultDelSPNameFull = N'[' + @vltDatabase + N'].[' + @Schema03Vault + N'].[delTokens_' + @srcObject + N']';

    SET @TempTableName = N'[' + @Schema02Temp + N'].[' + @srcObject + N']';
    SET @VaultTableName = N'[' + @Schema03Vault + N'].[' + @srcObject + N']';
    SET @VaultViewName = N'[' + @Schema03Vault + N'].[v_' + @srcObject + N']';
    SET @TrustedTableName = N'[' + @Schema04Trusted + N'].[' + @srcObject + N']';
    SET @DetokenizeViewName = N'[' + @Schema05Detokenize + N'].[' + @srcObject + N']';
    SET @VersionViewName = N'[' + @Schema06Version + N'].[' + @srcObject + N']';
    SET @VaultDelSPName = N'[' + @Schema03Vault + N'].[delTokens_' + @srcObject + N']';


    --Fetch column list with token expresions
    EXEC flw.GetTokenSchema @rawTable = @rawTable,
                            @mode = 0,
                            @dbg = @dbg,
                            @ColumnsList = @ColumnsList OUTPUT,
                            @vltColumnList = @vltColumnList OUTPUT;


    --
    DECLARE @TokenVersioning     BIT,
            @TokenRetentionDays INT,
            @VersionCMD         NVARCHAR(4000),
            @VersionOnCMD       NVARCHAR(2000),
            @VersionOffCMD      NVARCHAR(2000);
    SELECT @TokenVersioning = ISNULL(TokenVersioning, 0),
           @TokenRetentionDays = ISNULL(TokenRetentionDays, 0)
      FROM [flw].[Ingestion]
     WHERE FlowID = @FlowID;

    EXEC [flw].[GetVersioningScript] @srcTable = @VaultTableNameFull,
                                    @dbg = @dbg,
                                    @VersionCMD = @VersionCMD OUTPUT,
                                    @VersionOnCMD = @VersionOnCMD OUTPUT,
                                    @VersionOffCMD = @VersionOffCMD OUTPUT;



    --Final Column List
    SET @ColumnsList
        = @BK + N',GETDATE() AS UpdatedDate_DW, Getdate() as InsertedDate_DW, CAST(NULL AS BIT) DeletedDate_DW'
          + REPLACE(@ColumnsList, ',,', ',');

    --Check Values
    --SELECT @TempTableNameFull,
    --       @VaultTableNameFull,
    --       @ColumnsList,
    --       @vltColumnList;

    --PRINT '*************************test' + CAST(OBJECT_ID(@VaultTableNameFull) AS VARCHAR(255));
    --Check if Vault Table Exists
    IF (OBJECT_ID(@VaultTableNameFull) IS NULL)
    BEGIN

        --Create vault Schema if it doesnt exsist
        SET @SQLCMD
            = N'IF NOT EXISTS (SELECT * FROM [' + @vltDatabase + N'].sys.schemas WHERE name = N''' + @Schema03Vault
              + N''') exec [' + @vltDatabase + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA ['
              + REPLACE(REPLACE(@Schema03Vault, '[', ''), ']', '') + N'] AUTHORIZATION [DBO]''';

        IF @dbg > 1
        BEGIN
            SET @curSection = N'70 :: ' + @curObjName + N' :: Create vault Schema if it doesnt exsist';
            SET @curCode = @SQLCMD;
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;

        EXEC (@SQLCMD);

        --Create vault Schema if it doesnt exsist
        SET @SQLCMD
            = N'IF NOT EXISTS (SELECT * FROM [' + @vltDatabase + N'].sys.schemas WHERE name = N''' + @Schema03Vault
              + N''') exec [' + @vltDatabase + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA ['
              + REPLACE(REPLACE(@Schema03Vault, '[', ''), ']', '') + N'] AUTHORIZATION [DBO]''';

        IF @dbg > 1
        BEGIN
            SET @curSection = N'80 :: ' + @curObjName + N' :: Create vault Schema if it doesnt exsist';
            SET @curCode = @SQLCMD;
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;

        EXEC (@SQLCMD);

        --Create Init Vault Entity From Raw
        SET @SQLCMD
            = N' SELECT ' + @ColumnsList + N' INTO ' + @VaultTableNameFull + N' FROM ' + @rawTable + N' WHERE 1=2 ';

        IF @dbg > 1
        BEGIN
            SET @curSection = N'90 :: ' + @curObjName + N' :: Create Init Vault Entity From Raw';
            SET @curCode = @SQLCMD;
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;

        EXEC (@SQLCMD);

        --Create Clustered PK based on BK
        SET @SQLCMD
            = N' ALTER TABLE ' + @VaultTableNameFull + N' ADD CONSTRAINT ' + N'PK_'
              + [flw].[StrRemRegex] (@VaultTableNameFull, '%[^a-z]%') + CAST(CONVERT(INT,ABS(CHECKSUM(NewId())) % 1000) as varchar(5))  + N' PRIMARY KEY CLUSTERED ('
              + @BK + N') '
              + N'WITH( STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY] ';

        IF @dbg > 1
        BEGIN
            SET @curSection = N'100 :: ' + @curObjName + N' :: Create Clustered PK based on BK';
            SET @curCode = @SQLCMD;
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;

        EXEC (@SQLCMD);

        ---Add Versioning for token table.
        IF (@TokenVersioning = 1)
        BEGIN
            IF @dbg > 1
            BEGIN
                SET @curSection = N'110 :: ' + @curObjName + N' :: Add Versioning for token table';
                SET @curCode = @VersionCMD;
                PRINT [flw].[GetLogSection](@curSection, @curCode);
            END;
            EXEC (@VersionCMD);
        END;

        --Transfer data from Raw
        SET @SQLCMD
            = N' INSERT INTO  ' + @VaultTableNameFull + N' SELECT ' + @ColumnsList + N' FROM ' + @rawTable + N'';

        IF @dbg > 1
        BEGIN
            SET @curSection = N'120 :: ' + @curObjName + N' :: Transfer data from Raw';
            SET @curCode = @TokenVersioning;
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;
        EXEC (@SQLCMD);
    END;
    ELSE
    BEGIN
        --Create detokenize Schema if it doesnt exsist
        SET @SQLCMD
            = N'IF NOT EXISTS (SELECT * FROM [' + @vltDatabase + N'].sys.schemas WHERE name = N'''
              + @Schema05Detokenize + N''') exec [' + @vltDatabase + N'].sys.sp_executesql @stmt = N''CREATE SCHEMA ['
              + REPLACE(REPLACE(@Schema05Detokenize, '[', ''), ']', '') + N'] AUTHORIZATION [DBO]''';
        IF @dbg > 1
        BEGIN
            SET @curSection = N'130 :: ' + @curObjName + N' :: Create detokenize Schema if it doesnt exsist';
            SET @curCode = @SQLCMD;
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;

        EXEC (@SQLCMD);

        --Create Temp Token Data  From Raw
        SET @SQLCMD
            = N'IF EXISTS (SELECT * FROM [' + @vltDatabase + N'].sys.objects WHERE object_id = OBJECT_ID('''
              + @TempTableNameFull + N'''))' + N' BEGIN DROP TABLE ' + @TempTableNameFull + N' END' + N' SELECT '
              + @ColumnsList + N' INTO ' + @TempTableNameFull + N' FROM ' + @rawTable + N' WHERE 1=2 ';

        IF @dbg > 1
        BEGIN
            SET @curSection = N'140 :: ' + @curObjName + N' :: Create Temp Token Data  From Raw';
            SET @curCode = @SQLCMD;
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;

        EXEC (@SQLCMD);

        --Transfer data from Raw
        SET @SQLCMD
            = N' INSERT INTO  ' + @TempTableNameFull + N' SELECT ' + @ColumnsList + N' FROM ' + @rawTable + N'';

        IF @dbg > 1
        BEGIN
            SET @curSection = N'150 :: ' + @curObjName + N' :: Transfer data from Raw';
            SET @curCode = @SQLCMD;
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;

        EXEC (@SQLCMD);

        DECLARE @ADDCmd          NVARCHAR(MAX) = N'',
                @AlterCMD        NVARCHAR(MAX) = N'',
                @DataTypeWarning NVARCHAR(MAX) = N'',
                @ColumnWarning   NVARCHAR(MAX) = N'',
                @ColumnList      NVARCHAR(MAX) = N'',
                @HashColList     NVARCHAR(MAX) = N'';

        EXEC [flw].[CompSchema] @dbg = @dbg,
                                             @BK = @BK,
                                             @FromDatabase = @vltDatabase,
                                             @FromObject = @TempTableName,
                                             @ToDatabase = @vltDatabase,
                                             @ToObject = @VaultTableName,
                                             @DataTypeWarning = @DataTypeWarning OUTPUT,
                                             @ColumnWarning = @ColumnWarning OUTPUT,
                                             @ADDCmd = @ADDCmd OUTPUT,
                                             @AlterCMD = @AlterCMD OUTPUT,
                                             @ColumnList = @ColumnList OUTPUT,
                                             @HashColList = @HashColList OUTPUT;


        PRINT ' [flw].[CompSchema] @dbg = 0, @BK = ''' + @BK + ''',' + ' @FromDatabase = '''
              + @vltDatabase + ''',' + ' @FromObject = ''' + @TempTableName + ''',' + ' @ToDatabase =  '''
              + @vltDatabase + ''',' + ' @ToObject = ''' + @VaultTableName + ''',' + ' @DataTypeWarning = '''
              + @DataTypeWarning + ''',' + ' @ColumnWarning = ''' + @ColumnWarning + ''',' + ' @ADDCmd = ''' + @ADDCmd
              + ''',' + ' @AlterCMD = ''' + @AlterCMD + ''',' + ' @ColumnList = ''' + @ColumnList + ''','
              + ' @HashColList = '''',';

        --select @ADDCmd, @AlterCMD
        --Compare Schmea between Schema
        IF (@SyncSchema = 1)
        BEGIN
            --Add missing columns
            IF (LEN(@ADDCmd) > 0)
            BEGIN
                --PRINT @ADDCmd
                EXEC (@ADDCmd);
            END;

            --Adjust minor datatype changes
            IF (LEN(@AlterCMD) > 0)
            BEGIN
                --PRINT @AlterCMD
                EXEC (@AlterCMD);
            END;
        END;

        --Merg tmp with Vault
        EXEC [flw].[Upsert] @FlowID = @FlowID,
                            @ToDatabase = @vltDatabase,
                            @FromDatabase = @vltDatabase,
                            @FromObject = @TempTableName,
                            @ToObject = @VaultTableName,
                            @upsJoinColumns = @BK,
                            @HashKeyColumns = '',
                            @upsExcludeUpdateColumns = @upsExcludeUpdateColumns,
                            @dbg = @dbg;

    --IF (@dbg) = 0
    --BEGIN
    --    SET @SQLCMD = N'Drop table If Exists ' + @TempTableNameFull;
    --    EXEC (@SQLCMD);
    --END;
    END;

    --SELECT     v.BusinessEntityID, v.InsertDate_DW, v.UpdatedDate_DW, v.JobTitle, v.JobTitle_FT, v.JobTitle_SRC, v.BirthDate, v.BirthDate_SRC, v.Gender, v.Gender_FT, v.Gender_SRC, v.VacationHours, 
    --                         v.VacationHours_SRC
    --FROM            VaultDB.vlt.Employee AS v INNER JOIN
    --                         AzureDB.raw.Employee AS s ON v.BusinessEntityID = s.BusinessEntityID

    --Build Vault View Select Columns
    SET @SQLCMD
        = N'
    SELECT @vltViewColumnList = @vltViewColumnList + '','' + ISNULL(''v.['' + vltcol.COLUMN_NAME +'']'', ''r.['' + ra.COLUMN_NAME +'']''),
		   @dtkViewColumnList = @dtkViewColumnList + '','' + ISNULL(''v.['' + vltcol.COLUMN_NAME +''_SRC] AS ['' + vltcol.COLUMN_NAME  +'']'', ''r.['' + ra.COLUMN_NAME +'']'')
	FROM  [' + @srcDatabase
          + N'].INFORMATION_SCHEMA.COLUMNS ra
	LEFT OUTER JOIN (SELECT item AS COLUMN_NAME FROM [flw].[StringSplit](''' + @vltColumnList
          + N''','','')) vltcol
	ON ra.COLUMN_NAME = vltcol.COLUMN_NAME
	WHERE OBJECT_ID(''['' + ra.TABLE_CATALOG + ''].['' + ra.TABLE_SCHEMA + ''].['' + ra.TABLE_NAME + '']'') = '
          + @srcObjectID + N' ORDER BY ORDINAL_POSITION';
    EXEC sp_executesql @SQLCMD,
                       N'@vltViewColumnList NVARCHAR(MAX) OUTPUT, @dtkViewColumnList NVARCHAR(MAX) OUTPUT ',
                       @vltViewColumnList = @vltViewColumnList OUTPUT,
                       @dtkViewColumnList = @dtkViewColumnList OUTPUT;

    IF @dbg > 1
    BEGIN
        SET @curSection = N'160 :: ' + @curObjName + N' :: Build Vault View Select Columns';
        SET @curCode = @SQLCMD;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;

    --print @vltViewColumnList 
    --print @dtkViewColumnList
    SET @vltViewColumnList = SUBSTRING(@vltViewColumnList, 2, LEN(@vltViewColumnList));
    SET @dtkViewColumnList = SUBSTRING(@dtkViewColumnList, 2, LEN(@dtkViewColumnList));

    --Build Joing Expresion 
    DECLARE @Join      NVARCHAR(4000) = N'',
            @ViewBatch NVARCHAR(4000) = N'',
            @SPBatch   NVARCHAR(4000) = N'';

    SET @Join = N'';
    SELECT @Join = @Join + N' AND v.[' + Item + N']=' + N'r.[' + Item + ']'
      FROM flw.StringSplit([flw].[RemBrackets](@BK), ',')
     WHERE LEN(Item) > 0;
    SET @Join = SUBSTRING(@Join, 5, LEN(@Join));

    IF @dbg > 1
    BEGIN
        SET @curSection = N'170 :: ' + @curObjName + N' :: Build Joing Expresion';
        SET @curCode = @Join;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;


    ----Work around to create view on a remote db
    ----Build Vault view between vlt and raw table
    SET @SQLCMD
        = N'IF EXISTS (SELECT * FROM [' + @vltDatabase + N'].sys.objects WHERE object_id = OBJECT_ID('''
          + @VaultViewNameFull + N'''))' + N' BEGIN DROP VIEW ' + @VaultViewName + N' END';
    SET @ViewBatch
        = N'EXECUTE [' + @vltDatabase + N'].sys.sp_executesql N''' + REPLACE(@SQLCMD, CHAR(39), CHAR(39) + CHAR(39))
          + N'''';
    IF @dbg > 1
    BEGIN
        SET @curSection = N'180 :: ' + @curObjName + N' :: Build Vault view between vlt and raw table';
        SET @curCode = @ViewBatch;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;

    EXEC (@ViewBatch);

    SET @SQLCMD
        = N'CREATE VIEW ' + @VaultViewName + N' AS SELECT ' + @vltViewColumnList + N' FROM ' + @rawTable + N' AS r '
          + N'INNER JOIN ' + @VaultTableName + N' AS v  ON ' + @Join;

    SET @ViewBatch
        = N'EXECUTE [' + @vltDatabase + N'].sys.sp_executesql N''' + REPLACE(@SQLCMD, CHAR(39), CHAR(39) + CHAR(39))
          + N'''';
    PRINT @ViewBatch;

    IF @dbg > 1
    BEGIN
        SET @curSection = N'190 :: ' + @curObjName + N' :: Build Vault view between vlt and raw table';
        SET @curCode = @ViewBatch;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;

    EXEC (@ViewBatch);


    --Merg tmp with trusted table
    EXEC [flw].[Upsert] @FlowID = @FlowID,
                        @ToDatabase = @srcDatabase,
                        @ToObject = @TrustedTableName,
                        @FromDatabase = @vltDatabase,
                        @FromObject = @VaultViewName,
                        @upsJoinColumns = @BK,
                        @HashKeyColumns = '',
                        @upsExcludeUpdateColumns = @upsExcludeUpdateColumns,
                        @dbg = @dbg;


    ----Build Vault view between vlt and trusted table
    --+ N' BEGIN DROP VIEW ' + @DetokenizeViewName +
    SET @SQLCMD
        = N'IF EXISTS (SELECT * FROM [' + @vltDatabase + N'].sys.objects WHERE object_id = OBJECT_ID('''
          + @DetokenizeViewName + N'''))' + N' BEGIN; EXEC(''ALTER VIEW ' + @DetokenizeViewName + N' AS SELECT '
          + @dtkViewColumnList + N' FROM ' + @TrustedTableNameFull + N' AS r ' + N'INNER JOIN ' + @VaultTableName
          + N' AS v  ON ' + @Join + N'; '') END 
	  ELSE BEGIN
		EXEC(''CREATE VIEW ' + @DetokenizeViewName + N' AS SELECT ' + @dtkViewColumnList + N' FROM '
          + @TrustedTableNameFull + N' AS r ' + N'INNER JOIN ' + @VaultTableName + N' AS v  ON ' + @Join
          + N'; '') END;';

    IF @dbg > 1
    BEGIN
        SET @curSection = N'200 :: ' + @curObjName + N' :: Build Vault view between vlt and trusted table';
        SET @curCode = @SQLCMD;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;
    --remvoe temp View
    --if(@dbg=0)
    --BEGIN
    --  set @SQLCMD  = @SQLCMD +
    --  + N'IF EXISTS (SELECT * FROM [' + @vltDatabase + N'].sys.objects WHERE object_id = OBJECT_ID('''
    --  + @VaultViewNameFull + N'''))' + N' BEGIN DROP VIEW ' + @VaultViewName + N' END';
    --END

    --PRINT @SQLCMD
    SET @ViewBatch
        = N'EXECUTE [' + @vltDatabase + N'].sys.sp_executesql N''' + REPLACE(@SQLCMD, CHAR(39), CHAR(39) + CHAR(39))
          + N'''';

    IF @dbg > 1
    BEGIN
        SET @curSection = N'210 :: ' + @curObjName + N' :: Build Vault view between vlt and trusted table';
        SET @curCode = @ViewBatch;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;
    EXEC (@ViewBatch);

    IF (@TokenRetentionDays > 0)
    BEGIN

        --Drop Exsisting Token Deletion SP
        SET @SQLCMD
            = N'IF EXISTS (SELECT * FROM [' + @vltDatabase + N'].sys.objects WHERE object_id = OBJECT_ID('''
              + @VaultDelSPNameFull + N'''))' + N' BEGIN DROP PROCEDURE ' + @VaultDelSPName + N' END';
        SET @SPBatch
            = N'EXECUTE [' + @vltDatabase + N'].sys.sp_executesql N'''
              + REPLACE(@SQLCMD, CHAR(39), CHAR(39) + CHAR(39)) + N'''';
        IF @dbg > 1
        BEGIN
            SET @curSection = N'220 :: ' + @curObjName + N' :: Drop Exsisting Token Deletion SP';
            SET @curCode = @SPBatch;
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;
        EXEC (@SPBatch);


        --Tag deleted rows
        SET @SQLCMD
            = N';WITH DelTag
AS (SELECT ' + @BK + N'
         FROM ' + @VaultTableNameFull + N'
        WHERE [InsertedDate_DW] < DATEADD(dd, -' + CAST(@TokenRetentionDays AS VARCHAR(50))
              + N', GETDATE()))
UPDATE      v
   SET      v.[DeletedDate_DW] = 1,
            v.[UpdatedDate_DW] = GETDATE()
  FROM      ' + @VaultTableNameFull + N' v
 INNER JOIN DelTag r
    ON ' + @Join + N'
WHERE      v.[DeletedDate_DW] IS NULL;
'       ;
        IF @dbg > 1
        BEGIN
            SET @curSection = N'230 :: ' + @curObjName + N' :: Tag deleted rows';
            SET @curCode = @SQLCMD;
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;
        EXEC (@SQLCMD);

        -- Create Token Deletion SP
        IF (@TokenVersioning = 1)
        BEGIN
            SET @SQLCMD = @VersionOffCMD + N';';
        END;
        --Delete version rows
        SET @SQLCMD = @SQLCMD + N'exec(' + CHAR(39) + N'DELETE v
							FROM ' + @VersionViewNameFull + N' v
							INNER JOIN ' + @VaultTableNameFull + N' r
							ON ' + @Join + N' WHERE r.[DeletedDate_DW] = 1; ' + CHAR(39) + N');' + CHAR(13) + CHAR(10);


        SET @SQLCMD
            = @SQLCMD + N'exec(' + CHAR(39) + N'DELETE Trg
							FROM  ' + @VaultTableNameFull + N' trg WHERE  trg.[DeletedDate_DW] = 1; ' + CHAR(39)
              + N');' + CHAR(13) + CHAR(10);

        IF (@TokenVersioning = 1)
        BEGIN
            SET @SQLCMD = @SQLCMD + @VersionOnCMD;
        END;

        --IF (@dbg > 1)
        --      BEGIN
        --          SET @curSection = N'221 :: ' + @curObjName + N' :: Create Token Deletion SP';
        --          SET @curCode = @SQLCMD;
        --          PRINT [flw].[GetLogSection](@curSection, @curCode);
        --      END;
        --      EXEC (@SQLCMD);

        SET @SQLCMD = N'ALTER PROCEDURE ' + @VaultDelSPName + N' AS  BEGIN ' + @SQLCMD + N'  END ';

        SET @SPBatch
            = N'EXECUTE [' + @vltDatabase + N'].sys.sp_executesql N'''
              + REPLACE(@SQLCMD, CHAR(39), CHAR(39) + CHAR(39)) + N'''';

        IF @dbg > 1
        BEGIN
            SET @curSection = N'240 :: ' + @curObjName + N' :: Build Vault view between vlt and raw table';
            SET @curCode = @SPBatch;
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;
        EXEC (@SPBatch);

 

    END;

END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedure tokenizes data. Works only OnPrem solutions and Azure support require a rewrite due to cross-database scripting. ', 'SCHEMA', N'flw', 'PROCEDURE', N'TokenizeRaw', NULL, NULL
GO
