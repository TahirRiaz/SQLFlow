SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO





/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetPreViewCmd]
  -- Date				:   2022.03.20
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This user-defined function returns a table variable containing information about a view that will be created or altered. 
							The information includes the view's name, select statement, and the pre-filter to be applied.
  -- Summary			:	This T-SQL function takes a FlowID as input and returns a table variable containing the view's name, select statement, and pre-filter to be applied.
							It retrieves the necessary information from several tables in the database and constructs a select statement based on the columns specified in the PreIngestionTransform table.
							It then creates or alters the view using the constructed select statement and returns the necessary information about the view in the table variable.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022.03.20		Initial
  ##################################################################################################################################################
*/

CREATE FUNCTION [flw].[GetPreViewCmd]
(
    @FlowId NVARCHAR(1024)
)
RETURNS @cmd TABLE
(
    ViewCMD NVARCHAR(MAX) NULL,
    ViewSelect NVARCHAR(MAX) NULL,
    ViewName NVARCHAR(255) NULL,
    ViewNameFull NVARCHAR(255) NULL,
    preFilter NVARCHAR(1024)
)
BEGIN

    DECLARE @ViewCMD NVARCHAR(MAX) = N'',
            @ViewColumnList NVARCHAR(MAX) = N'',
            @ViewSelect NVARCHAR(MAX) = N'',
            @preFilter NVARCHAR(1024) = N'',
            @trgDatabase [NVARCHAR](255) = N'',
            @trgSchema [NVARCHAR](250) = N'',
            @trgObject [NVARCHAR](250) = N'',
            @ViewName NVARCHAR(255) = N'',
            @ViewNameFull NVARCHAR(255) = N'',
            @trgDBSchTbl [NVARCHAR](250),
            @Schema07Pre NVARCHAR(25) = N'';
    DECLARE @LogStack NVARCHAR(MAX);

    SELECT @Schema07Pre = [flw].[RemBrackets](ParamValue)
    FROM flw.SysCFG
    WHERE (ParamName = N'Schema07Pre');

    SELECT @trgDBSchTbl = flw.GetValidSrcTrgName(trgDBSchTbl),
           @preFilter = REPLACE([preFilter], CHAR(39), CHAR(39) + CHAR(39))
    FROM [flw].[PreFiles]
    WHERE [FlowID] = @FlowId;

    IF (@trgDBSchTbl IS NULL)
    BEGIN
        SELECT @trgDBSchTbl = flw.GetValidSrcTrgName(trgDBSchTbl),
               @preFilter = REPLACE(ISNULL([preFilter], ''), CHAR(39), CHAR(39) + CHAR(39))
        FROM [flw].[PreIngestionADO]
        WHERE [FlowID] = @FlowId;
    END;

    SELECT @trgDatabase = [flw].[RemBrackets]([1]),
           @trgSchema = [flw].[RemBrackets]([2]),
           @trgObject = [flw].[RemBrackets]([3])
    FROM
    (
        SELECT recId,
               Item
        FROM flw.SplitStr2Tbl(flw.GetValidSrcTrgName(@trgDBSchTbl), '.')
    ) AS SourceTable
    PIVOT
    (
        MAX(Item)
        FOR recId IN ([1], [2], [3])
    )
AS  PivotTable;

    SET @ViewName = N'[' + @Schema07Pre + N'].[v_' + @trgObject + N']';
    SET @ViewNameFull = N'[' + @trgDatabase + N'].[' + @Schema07Pre + N'].[v_' + @trgObject + N']';


    DECLARE @colTbl TABLE
    (
        RecId INT IDENTITY(1, 1),
        ColName NVARCHAR(4000)
    );
    INSERT INTO @colTbl
    (
        ColName
    )
    SELECT CASE
               WHEN LEN(ISNULL(SelectExp, '')) > 0 THEN
                   REPLACE(REPLACE(SelectExp, CHAR(39), CHAR(39) + CHAR(39)), '@ColName', ColName)
               ELSE
                   CASE
                       WHEN [flw].[RemBrackets](ColName) = 'FileRowDate_DW' THEN
                           'convert(datetime,[' + [flw].[RemBrackets](ColName) + '], 20)'
                       WHEN [flw].[RemBrackets](ColName) = 'FileSize_DW' THEN
                           'CAST ([' + [flw].[RemBrackets](ColName) + '] AS  numeric(18,0)) '
                       WHEN [flw].[RemBrackets](ColName) IN ( 'FileDate_DW', 'DataSet_DW' ) THEN
                           'CAST ([' + [flw].[RemBrackets](ColName) + '] AS  numeric(14,0)) '
                       WHEN [flw].[RemBrackets](ColName) IN ( 'FileLineNumber_DW' ) THEN
                           'CAST ([' + [flw].[RemBrackets](ColName) + '] AS  int) '
                       ELSE
                           '[' + [flw].[RemBrackets](ColName) + ']'
                   END
           END + ' AS [' + CASE
                               WHEN LEN(ISNULL(ColAlias, '')) > 0 THEN
                                   [flw].[RemBrackets](LTRIM(RTRIM(ColAlias)))
                               ELSE
                                   [flw].[RemBrackets](ColName)
                           END + ']'
    FROM flw.PreIngestionTransfrom
    WHERE [FlowID] = @FlowId
          AND ISNULL(ExcludeColFromView, 0) = 0
    ORDER BY [SortOrder] ASC,
             [TransfromID] ASC;

    SELECT @ViewColumnList = @ViewColumnList + N',' + CHAR(13) + CHAR(10) + ColName
    FROM @colTbl
    ORDER BY RecId;

    SET @ViewColumnList = SUBSTRING(@ViewColumnList, 2, LEN(@ViewColumnList));


    IF (LEN(@ViewColumnList) > 2)
    BEGIN
        SET @ViewSelect
            = N'SELECT ' + @ViewColumnList
              + N' FROM ' --+ N', CAST(''''@FileDate_DW''''  as varchar(15)) AS FileDate_DW' 
              + flw.GetValidSrcTrgName(@trgDBSchTbl) + N' AS r ';


        SET @ViewCMD
            = N'IF EXISTS (SELECT * FROM [' + @trgDatabase + N'].sys.objects WHERE object_id = OBJECT_ID('''
              + flw.GetValidSrcTrgName(@trgDBSchTbl) + N'''))' + N' BEGIN ' + +N'IF EXISTS (SELECT * FROM ['
              + @trgDatabase + N'].sys.objects WHERE object_id = OBJECT_ID(''' + @ViewNameFull + N'''))'
              + N' BEGIN; EXEC(''ALTER VIEW ' + @ViewName + N' AS SELECT ' + @ViewColumnList + N' FROM '
              + flw.GetValidSrcTrgName(@trgDBSchTbl) + N' AS r WHERE 1=1 ' + @preFilter
              + N' '') END ELSE BEGIN
EXEC(''CREATE VIEW '   + @ViewName + N' AS SELECT ' + @ViewColumnList + N' FROM '
              + flw.GetValidSrcTrgName(@trgDBSchTbl) + N' AS r WHERE 1=1 ' + @preFilter + N' '') END; END;';

    END;
    ELSE
    BEGIN
        SET @ViewCMD = N'';
        SET @ViewSelect = N'';
    END;


    INSERT INTO @cmd
    (
        ViewCMD,
        ViewSelect,
        ViewName,
        ViewNameFull,
        preFilter
    )
    SELECT @ViewCMD,
           @ViewSelect,
           @ViewName,
           @ViewNameFull,
           @preFilter;

    RETURN;
-- @cmd;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This user-defined function returns a table variable containing information about a view that will be created or altered. 
							The information includes the view''s name, select statement, and the pre-filter to be applied.
  -- Summary			:	This T-SQL function takes a FlowID as input and returns a table variable containing the view''s name, select statement, and pre-filter to be applied.
							It retrieves the necessary information from several tables in the database and constructs a select statement based on the columns specified in the PreIngestionTransform table.
							It then creates or alters the view using the constructed select statement and returns the necessary information about the view in the table variable.
', 'SCHEMA', N'flw', 'FUNCTION', N'GetPreViewCmd', NULL, NULL
GO
