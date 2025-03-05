SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name             :   CompSchema
  -- Date             :   2020.11.06
  -- Author           :   Tahir Riaz
  -- Company          :   Business IQ
  -- Purpose          :   Compare schema of From and ToObject. Diff is loged as warnings in sql.flowlog
  -- Usage			  :	  EXEC [flw].[CompSchema]   @BK  = '',
													@UseAbsObjNames = 1,
													@FromDatabase  = N'AzureDB',
													@FromObject = N'[tmp].[Employee]',
													@ToDatabase = N'AzureDB',
													@ToObject = N'[vlt].[Employee]',
													@dbg = 0;
  -- Required grants  :   dbo
  -- Called by        :   SQL Server Job Agent
  -- Notes			  :	  See TODO:
  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		11.06.2020		Initial
  ##################################################################################################################################################
  */

CREATE PROCEDURE [flw].[!CompSchema]
    @BK NVARCHAR(500) = '',
    @UseAbsObjNames BIT = 1,
    @FromDatabase NVARCHAR(255) = N'AzureDB',
    @FromObject NVARCHAR(255) = N'[tmp].[Employee]',
    @ToDatabase NVARCHAR(255) = N'AzureDB',
    @ToObject NVARCHAR(255) = N'[vlt].[Employee]',
    @dbg BIT = 0,
    @DataTypeWarning NVARCHAR(MAX) = N'' OUTPUT,
    @ColumnWarning NVARCHAR(MAX) = N'' OUTPUT,
    @ADDCmd NVARCHAR(MAX) = N'' OUTPUT,
    @AlterCMD NVARCHAR(MAX) = N'' OUTPUT,
    @ColumnList NVARCHAR(MAX) = N'' OUTPUT,
    @HashColList NVARCHAR(MAX) = N'' OUTPUT
AS
BEGIN

    SET NOCOUNT ON;

    DECLARE @curObjName NVARCHAR(255),
            @curExecCmd NVARCHAR(4000),
            @curSection NVARCHAR(4000),
            @curCode    NVARCHAR(MAX);

    IF (@dbg >= 2)
    BEGIN
        SET @curObjName = ISNULL(OBJECT_SCHEMA_NAME(@@PROCID) + '.' + OBJECT_NAME(@@PROCID), 'Defualt SP Name');
        SET @curExecCmd
            = N'exec ' + @curObjName + N' @BK=''' + @BK + N''' @UseAbsObjNames='
              + CAST(@UseAbsObjNames AS NVARCHAR(255)) + N' @FromDatabase=''' + @FromDatabase + N''' @FromObject='''
              + @FromObject + N''' @ToDatabase=''' + @ToDatabase + N''' @ToObject=' + @ToObject + N''' @dbg='
              + CAST(@dbg AS NVARCHAR(255));
        PRINT [flw].[GetLogHeader](@curObjName, @curExecCmd);
    END;

    --Ensure that debug info is only printed when called directly.
    IF (@@NESTLEVEL > 1 AND @dbg = 1)
        SET @dbg = 0;

    SET @BK = flw.RemBrackets(@BK);

    DECLARE @SchemaCompare TABLE ([FromObject] [NVARCHAR](257) NULL,
                                  [ToObject] [NVARCHAR](257) NULL,
                                  [cName] [sysname] NULL,
                                  [FoDT] [sysname] NULL,
                                  [FoLength] [VARCHAR](25) NULL,
                                  [FoPrecision] [VARCHAR](25) NULL,
                                  [ToDT] [sysname] NULL,
                                  [ToLength] [VARCHAR](25) NULL,
                                  [ToPrecision] [VARCHAR](25) NULL,
                                  [Column] [VARCHAR](150) NOT NULL,
                                  [DataType] [VARCHAR](150) NULL,
                                  [Length] [VARCHAR](25) NULL,
                                  [Precision] [VARCHAR](25) NULL,
                                  [Collation] [VARCHAR](25) NULL,
                                  ADDCmd [VARCHAR](2048) NULL,
                                  AlterCMD [VARCHAR](2048) NULL);

    DECLARE @FromObjectID NVARCHAR(255),
            @ToObjectID   NVARCHAR(255),
            @sObject      NVARCHAR(255),
            @tObject      NVARCHAR(255),
            @SrcMetaPath  NVARCHAR(255),
            @TrgMetaPath  NVARCHAR(255),
            @SrcStr       VARCHAR(50) = 'Fo: ',
            @TrgStr       VARCHAR(50) = 'To: ';

    --Set source and target object name 
    SET @sObject = IIF(@UseAbsObjNames = 1, @FromDatabase + '.' + @FromObject, @FromObject);
    SET @tObject = IIF(@UseAbsObjNames = 1, @ToDatabase + '.' + @ToObject, @ToObject);
    SET @SrcMetaPath = IIF(@UseAbsObjNames = 1, @FromDatabase + '.sys.', 'sys.');
    SET @TrgMetaPath = IIF(@UseAbsObjNames = 1, @ToDatabase + '.sys.', 'sys.');

    SET @SrcStr = IIF(LEN(@FromDatabase) > 0, 'Src: ', @SrcStr);
    SET @TrgStr = IIF(LEN(@FromDatabase) > 0, 'Trg: ', @TrgStr);

    SELECT @FromObjectID = OBJECT_ID(@sObject),
           @ToObjectID = OBJECT_ID(@tObject);

    DECLARE @SQL VARCHAR(MAX);

    SELECT @SQL
        = 'SELECT FromObject=s.tSchema + ''.'' + s.tName 
,ToObject=t.tSchema + ''.'' + t.tName 
,cName=ISNULL(s.cName,t.cName) ,s.Datatype FoDT,s.Length FoLength,s.precision FoPrecision,t.Datatype ToDT,t.Length ToLength,t.precision ToPrecision
,[Column]=Case When s.cName IS NULL then ''Missing in the Source'' When t.cName IS NULL then ''Missing in the Destination'' ELSE '''' END
,ADDCmd=CASE When t.cName IS NULL then 
''ALTER TABLE [' + @ToDatabase + '].' + @ToObject
          + ' ADD ['' + s.cName  + ''] ''
+ CASE WHEN s.Datatype in (''nvarchar'',''nchar'',''ntext'')  THEN substring(s.Datatype,2,len(s.Datatype)) ELSE s.Datatype END
+ CASE WHEN s.Datatype in (''sql_variant'',''text'',''ntext'',''int'',''float'',''smallint'',''bigint'',''real'',''datetime'',''smalldatetime'',''tinyint'', ''bit'', ''datetime2'',''date'',''xml'',''hierarchyid'',''geography'') THEN '''' WHEN s.Datatype in (''decimal'', ''NUMERIC'') THEN ''('' + Cast(s.precision AS VARCHAR) + '', '' + Cast(s.scale AS VARCHAR) + '')'' ELSE ISNULL( CASE WHEN s.Datatype IN (''XML'')  THEN '''' WHEN s.max_length = -1 THEN ''(MAX)'' ELSE ''('' + CAST(s.max_length AS VARCHAR) + '')'' END , '''') END + '' '' 
+ '' NULL '' 
+ CASE WHEN s.COLUMN_DEFAULT IS NOT NULL THEN ''DEFAULT ''  + s.COLUMN_DEFAULT ELSE '''' END  + '';''
	ELSE '''' END 
,AlterCMD=CASE When s.Length > t.Length OR  s.precision > t.precision THEN 
''ALTER TABLE [' + @ToDatabase + '].' + @ToObject
          + ' ALTER COLUMN ['' + s.cName  + ''] ''
+ CASE WHEN s.Datatype in (''nvarchar'',''nchar'',''ntext'')  THEN substring(s.Datatype,2,len(s.Datatype)) ELSE s.Datatype END
+ CASE WHEN s.Datatype in (''sql_variant'',''text'',''ntext'',''int'',''float'',''smallint'',''bigint'',''real'') THEN '''' WHEN s.Datatype in (''decimal'', ''NUMERIC'') THEN ''('' + Cast(s.precision AS VARCHAR) + '', '' + Cast(s.scale AS VARCHAR) + '')'' ELSE ISNULL( CASE WHEN s.Datatype IN (''XML'')  THEN '''' WHEN s.max_length = -1 THEN ''(MAX)'' ELSE ''('' + CAST(s.max_length AS VARCHAR) + '')'' END , '''') END + '' '' 
+ '' NULL '' 
+ CASE WHEN s.COLUMN_DEFAULT IS NOT NULL THEN ''DEFAULT ''  + s.COLUMN_DEFAULT ELSE '''' END  + '';''
ELSE '''' END 
,DataType=CASE WHEN s.cName IS NOT NULL AND t.cName IS NOT NULL AND s.Datatype <> t.Datatype THEN ''Data Type mismatch'' END 
,Length=CASE WHEN s.cName IS NOT NULL AND t.cName IS NOT NULL AND s.Length <> t.Length THEN ''Length mismatch'' END 
,Precision=CASE WHEN s.cName IS NOT NULL AND t.cName IS NOT NULL AND s.precision <> t.precision THEN ''precision mismatch'' END 
,Collation = CASE WHEN s.cName IS NOT NULL AND t.cName IS NOT NULL AND ISNULL(s.collation_name,'''') <> ISNULL(t.collation_name,'''') THEN ''Collation mismatch'' END 
FROM  
(SELECT tSchema=(SELECT name FROM [' + @FromDatabase
          + N'].sys.schemas sc WHERE sc.schema_id=so.schema_id) 
,tName=so.name,cName=sc.name,DataType=St.name,Length=IIF(left(St.name,1)=''n'', Sc.max_length/2, Sc.max_length)
,precision=Sc.precision,scale=sc.scale,max_length=Sc.max_length,COLUMN_DEFAULT=OBJECT_DEFINITION(sc.default_object_id),collation_name=Sc.collation_name 
  FROM ' + @SrcMetaPath + 'objects So 
  JOIN ' + @SrcMetaPath + 'columns Sc 
    ON So.object_id = Sc.object_id 
  JOIN ' + @SrcMetaPath
          + 'types St 
    ON Sc.system_type_id=St.system_type_id 
   AND Sc.user_type_id=St.user_type_id 
 WHERE SO.TYPE in (''U'',''V'')
   AND SO.object_id=''' + @FromObjectID + ''' 
  ) s  
 FULL OUTER JOIN 
 ( SELECT tSchema=(SELECT name FROM [' + @ToDatabase
          + N'].sys.schemas sc WHERE sc.schema_id=so.schema_id)
,tName=so.name,cName=sc.name,DataType=St.name,Length=IIF(left(St.name,1)=''n'', Sc.max_length/2, Sc.max_length)
,precision=Sc.precision,scale=sc.scale,max_length=Sc.max_length,COLUMN_DEFAULT=OBJECT_DEFINITION(sc.default_object_id),collation_name=Sc.collation_name 
  FROM ' + @TrgMetaPath + 'objects So 
  JOIN ' + @TrgMetaPath + 'columns Sc 
    ON So.object_id = Sc.object_id 
  JOIN ' + @TrgMetaPath
          + 'types St 
    ON Sc.system_type_id=St.system_type_id AND Sc.user_type_id=St.user_type_id 
WHERE SO.TYPE in (''U'',''V'')
  AND SO.object_id=''' + @ToObjectID + ''' 
 ) t ON s.cName=t.cName ';
    --s.tName = t.tName AND

    IF @dbg >= 2
    BEGIN
        SET @curSection = N'01 :: ' + @curObjName + N' :: SQL for comparing schema';
        SET @curCode = @SQL;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;

    INSERT INTO @SchemaCompare ([FromObject],
                                [ToObject],
                                [cName],
                                [FoDT],
                                [FoLength],
                                [FoPrecision],
                                [ToDT],
                                [ToLength],
                                [ToPrecision],
                                [Column],
                                ADDCmd,
                                AlterCMD,
                                [DataType],
                                [Length],
                                [Precision],
                                [Collation])
    EXEC (@SQL);


    DECLARE @DiffDataType  NVARCHAR(MAX),
            @DiffPrecision NVARCHAR(MAX),
            @DiffCol       NVARCHAR(MAX),
            @DiffLength    NVARCHAR(MAX);
    --@ColumnList nvarchar(max) = ''
    --@DataTypeWarning NVARCHAR(MAX),
    --@ColumnWarning NVARCHAR(MAX),
    --@ADDCmd NVARCHAR(MAX) = N'',
    --@AlterCMD NVARCHAR(MAX) = N'';

    --SET @DataTypeWarning = N'';
    --SET @ColumnWarning = N'';


    SELECT @ColumnList = @ColumnList + ',' + ISNULL([cName], '')
      FROM @SchemaCompare
     WHERE [ToObject] IS NOT NULL
       AND [FromObject] IS NOT NULL;

    IF @dbg > 2
    BEGIN
        SET @curSection = N'02 :: ' + @curObjName + N' :: Variable @ColumnList Value';
        SET @curCode = @ColumnList;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;


    SELECT @HashColList = @HashColList + ',' + ISNULL([cName], '')
      FROM @SchemaCompare
     WHERE [ToObject] IS NOT NULL
       AND [FromObject] IS NOT NULL
       AND cName NOT IN ( SELECT Item FROM [flw].[StringSplit](@BK, ',') );

    IF @dbg > 2
    BEGIN
        SET @curSection = N'03 :: ' + @curObjName + N' :: Variable @HashColList Value';
        SET @curCode = @HashColList;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;


    DECLARE @addCMDTemp   NVARCHAR(MAX) = N'',
            @AlterCMDTemp NVARCHAR(MAX) = N'';

    SELECT @addCMDTemp = @addCMDTemp + ISNULL(ADDCmd, ''),
           @AlterCMDTemp = @AlterCMDTemp + ISNULL(AlterCMD, '')
      FROM @SchemaCompare
     WHERE LEN(ADDCmd)   > 1
        OR LEN(AlterCMD) > 1;

    SELECT @ADDCmd = @addCMDTemp,
           @AlterCMD = @AlterCMDTemp;

    SELECT @DataTypeWarning
        = @DataTypeWarning
          + CASE
                 WHEN LEN(DiffDataType) > 0
                   OR LEN(DiffLength) > 0
                   OR LEN(DiffPrecision) > 0 THEN
                     '"' + [cName] + '"' + ':{' + ISNULL('"DataType":' + DiffDataType, '')
                     + ISNULL(',' + '"Length":' + DiffLength, '') + +ISNULL(',' + '"Precision":' + DiffPrecision, '')
                     + '}, ' + CHAR(13) + CHAR(10)
                 ELSE '' END,
           @ColumnWarning
               = @ColumnWarning
                 + CASE
                        WHEN LEN(DiffCol) > 0 THEN
                            '"' + [cName] + '"' + ':{' + ISNULL(',' + '"Columns":' + DiffCol, '') + '}, ' + CHAR(13)
                            + CHAR(10)
                        ELSE '' END
      FROM (   SELECT [cName],
                      DiffDataType = STUFF(
                                         (   SELECT N', {' + @SrcStr + sub.FoDT + '(' + sub.FoLength + '), ' + @TrgStr
                                                    + sub.ToDT + '(' + sub.ToLength + ')}'
                                               FROM @SchemaCompare sub
                                              WHERE DataType IS NOT NULL
                                                AND main.cName = sub.cName
                                             FOR XML PATH(''), TYPE).value('text()[1]', 'nvarchar(max)'),
                                         1,
                                         2,
                                         N''),
                      DiffPrecision = STUFF(
                                          (   SELECT N', {' + @SrcStr + Sub.FoPrecision + ', ' + @TrgStr
                                                     + Sub.ToPrecision + '}'
                                                FROM @SchemaCompare Sub
                                               WHERE [Precision] IS NOT NULL
                                                 AND main.cName = Sub.cName
                                              FOR XML PATH(''), TYPE).value('text()[1]', 'nvarchar(max)'),
                                          1,
                                          2,
                                          N''),
                      DiffLength = STUFF(
                                       (   SELECT N', {' + @SrcStr + Sub.FoLength + ', ' + @TrgStr + Sub.ToLength + '}'
                                             FROM @SchemaCompare Sub
                                            WHERE Length IS NOT NULL
                                              AND main.cName = Sub.cName
                                           FOR XML PATH(''), TYPE).value('text()[1]', 'nvarchar(max)'),
                                       1,
                                       2,
                                       N''),
                      DiffCol = STUFF(
                                    (   SELECT N', {' + @SrcStr + IIF(Sub.FoDT IS NULL, '0', '1') + ', ' + @TrgStr
                                               + IIF(Sub.ToDT IS NULL, '0', '1') + '}'
                                          FROM @SchemaCompare Sub
                                         WHERE (   FoDT IS NULL
                                              OR   ToDT IS NULL)
                                           AND main.cName = Sub.cName
                                           AND cName NOT IN ( 'LastUpdated', 'RowStatus' ) --Exclude Sys Columns
                                        FOR XML PATH(''), TYPE).value('text()[1]', 'nvarchar(max)'),
                                    1,
                                    2,
                                    N'')
                 FROM @SchemaCompare main
                GROUP BY [cName]) a;

    IF @dbg > 2
    BEGIN
        SET @curSection = N'04 :: ' + @curObjName + N' :: Variable @DataTypeWarning Value';
        SET @curCode = @DataTypeWarning;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;

    IF (@dbg = 1)
    BEGIN
        PRINT IIF(LEN(@DataTypeWarning) > 0, @DataTypeWarning, NULL);
        PRINT IIF(LEN(@ColumnWarning) > 0, @ColumnWarning, NULL);
    END;

--SELECT IIF(LEN(@DataTypeWarning) > 0, @DataTypeWarning, NULL) DataTypeWarning,
--       IIF(LEN(@ColumnWarning) > 0, @ColumnWarning, NULL) ColumnWarning,
--       @ADDCmd ADDCmd,
--       @AlterCMD AlterCMD;

END;
GO
