SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO



/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetTransSchemaDS]
  -- Date				:   2024.07.05
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   
  -- Summary			:	
  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2020.11.08		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[GetTransformSchemaDS]
    @FlowID INT,   -- FlowID from [flw].[Ingestion]
    @dbg INT = 0  -- Debug Level
AS
BEGIN

    DECLARE @curObjName NVARCHAR(255),
            @curExecCmd NVARCHAR(4000),
            @curSection NVARCHAR(4000),
            @curCode NVARCHAR(MAX);


    IF (@dbg > 1)
    BEGIN
        SET @curObjName = ISNULL(OBJECT_SCHEMA_NAME(@@PROCID) + '.' + OBJECT_NAME(@@PROCID), 'Defualt SP Name');
        SET @curExecCmd
            = N'exec ' + @curObjName + N' @FlowID=''' + CAST(@FlowID AS NVARCHAR(255)) + N''', @dbg=' + CAST(@dbg AS NVARCHAR(255));
        PRINT [flw].[GetLogHeader](@curObjName, @curExecCmd);
    END;

    WITH TransformCols
    AS (
        SELECT v.FlowID,
               PARSENAME(flw.GetValidSrcTrgName(i.srcDBSchTbl), 3) SrcDB,
               PARSENAME(flw.GetValidSrcTrgName(i.srcDBSchTbl), 2) SrcSch,
               PARSENAME(flw.GetValidSrcTrgName(i.srcDBSchTbl), 1) SrcTbl,
               PARSENAME(flw.GetValidSrcTrgName(i.trgDBSchTbl), 3) TrgDB,
               PARSENAME(flw.GetValidSrcTrgName(i.trgDBSchTbl), 2) TrgSch,
               PARSENAME(flw.GetValidSrcTrgName(i.trgDBSchTbl), 1) TrgTbl,
               v.ColumnName,
               0 OrdPos,
               '' AS Coll,
               v.ColumnName AS ColClean,
               [flw].[IsKeyColumn](@FlowID, v.ColumnName) IsKey,
               0 [IsDate],
               v.DataTypeExp,
               ISNULL(v.SelectExp, '') AS SelectExp, --REPLACE(v.SelectExp, CHAR(39), CHAR(39) + CHAR(39))
               'ALTER TABLE [' + PARSENAME(flw.GetValidSrcTrgName(i.trgDBSchTbl), 3) + '].['
               + PARSENAME(flw.GetValidSrcTrgName(i.trgDBSchTbl), 2) + '].['
               + PARSENAME(flw.GetValidSrcTrgName(i.trgDBSchTbl), 1) + '] ADD [' + +[flw].[RemBrackets](v.ColumnName)
               + +'] ' + v.DataTypeExp + ' NULL;' AS SrcAddColumnCMD
        FROM [flw].[IngestionTransfrom] AS v
            INNER JOIN flw.Ingestion AS i
                ON v.FlowID = i.FlowID
        WHERE i.FlowID = @FlowID
              AND v.ColumnName NOT IN
                  (
                      SELECT [ColumnName] FROM flw.SysColumn
                  ) -- Exclude any custom mapping for SysColumns)
    )
	
	SELECT FlowID,
           SrcDB,
           SrcSch,
           SrcTbl,
           TrgDB,
           TrgSch,
           TrgTbl,
           [flw].[RemBrackets](ColumnName) ColumnName,
           OrdPos,
           Coll,
           [flw].[RemBrackets](ColumnName) ColClean,
           IsKey,
           [IsDate],
           DataTypeExp,
           REPLACE(SelectExp,'@ColName',QUOTENAME([flw].[RemBrackets](ColumnName)))  AS SelectExp, 
           SrcAddColumnCMD
    FROM TransformCols;
	--+ ' AS ' + QUOTENAME([flw].[RemBrackets](ColumnName))
END;
GO
