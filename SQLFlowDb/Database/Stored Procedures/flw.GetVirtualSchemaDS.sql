SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO



/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetVirtualSchemaDS]
  -- Date				:   2020.11.08
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   
  -- Summary			:	
  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2020.11.08		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[GetVirtualSchemaDS]
    @FlowID INT,   -- FlowID from [flw].[Ingestion]
    @mode INT = 1, -- 
    @dbg INT = 0,  -- Debug Level
    @VirtualSchema XML = '' OUTPUT,
    @VirtualSchemaForCTE VARCHAR(MAX) = '' OUTPUT
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
            = N'exec ' + @curObjName + N' @FlowID=''' + CAST(@FlowID AS NVARCHAR(255)) + N''', @mode='
              + CAST(@mode AS NVARCHAR(255)) + N', @dbg=' + CAST(@dbg AS NVARCHAR(255));
        PRINT [flw].[GetLogHeader](@curObjName, @curExecCmd);
    END;

    WITH ValidSyscols
    AS (SELECT FlowID,
               [ColumnName]
        FROM flw.Ingestion i
            CROSS APPLY
        (
            SELECT Ordinal,
                   [flw].[RemBrackets](Item) AS [ColumnName]
            FROM [flw].[StringSplit](i.[SysColumns], ',')
        ) s
        WHERE i.FlowID = @FlowID),
         IngestionSysVirtual
    AS (SELECT FlowID,
               isc.[SysColumnID],
               isc.ColumnName,
               isc.[DataType],
               isc.[DataTypeExp],
               isc.[SelectExp]
        FROM flw.SysColumn AS isc
            INNER JOIN ValidSyscols vs
                ON isc.[ColumnName] = vs.[ColumnName]),
         VirtualCols
    AS (SELECT v.FlowID,
               PARSENAME(flw.GetValidSrcTrgName(i.srcDBSchTbl), 3) SrcDB,
               PARSENAME(flw.GetValidSrcTrgName(i.srcDBSchTbl), 2) SrcSch,
               PARSENAME(flw.GetValidSrcTrgName(i.srcDBSchTbl), 1) SrcTbl,
               PARSENAME(flw.GetValidSrcTrgName(i.trgDBSchTbl), 3) TrgDB,
               PARSENAME(flw.GetValidSrcTrgName(i.trgDBSchTbl), 2) TrgSch,
               PARSENAME(flw.GetValidSrcTrgName(i.trgDBSchTbl), 1) TrgTbl,
               v.ColumnName,
               [VirtualID] * 2000 OrdPos,
               v.DataType,
               '' AS Coll,
               v.ColumnName AS ColClean,
               [flw].[IsKeyColumn](1021, v.ColumnName) IsKey,
               0 [IsDate],
               v.DataTypeExp,
               v.SelectExp AS SelectExp,
               'ALTER TABLE [' + PARSENAME(flw.GetValidSrcTrgName(i.trgDBSchTbl), 3) + '].['
               + PARSENAME(flw.GetValidSrcTrgName(i.trgDBSchTbl), 2) + '].['
               + PARSENAME(flw.GetValidSrcTrgName(i.trgDBSchTbl), 1) + '] ADD [' + +[flw].[RemBrackets](v.ColumnName)
               + +'] ' + v.DataTypeExp + ' NULL;' AS SrcAddColumnCMD
        FROM flw.IngestionVirtual AS v
            INNER JOIN flw.Ingestion AS i
                ON v.FlowID = i.FlowID
        WHERE i.FlowID = @FlowID
              AND v.ColumnName NOT IN
                  (
                      SELECT [ColumnName] FROM flw.SysColumn
                  ) -- Exclude any custom mapping for SysColumns
        UNION ALL
        SELECT v.FlowID,
               PARSENAME(flw.GetValidSrcTrgName(i.srcDBSchTbl), 3) SrcDB,
               PARSENAME(flw.GetValidSrcTrgName(i.srcDBSchTbl), 2) SrcSch,
               PARSENAME(flw.GetValidSrcTrgName(i.srcDBSchTbl), 1) SrcTbl,
               PARSENAME(flw.GetValidSrcTrgName(i.trgDBSchTbl), 3) TrgDB,
               PARSENAME(flw.GetValidSrcTrgName(i.trgDBSchTbl), 2) TrgSch,
               PARSENAME(flw.GetValidSrcTrgName(i.trgDBSchTbl), 1) TrgTbl,
               v.ColumnName,
               [SysColumnID] * 90000 OrdPos,
               v.DataType,
               '' AS Coll,
               v.ColumnName AS ColClean,
               [flw].[IsKeyColumn](1021, v.ColumnName) IsKey,
               0 [IsDate],
               v.DataTypeExp,
               v.SelectExp AS SelectExp,
               'ALTER TABLE [' + PARSENAME(flw.GetValidSrcTrgName(i.trgDBSchTbl), 3) + '].['
               + PARSENAME(flw.GetValidSrcTrgName(i.trgDBSchTbl), 2) + '].['
               + PARSENAME(flw.GetValidSrcTrgName(i.trgDBSchTbl), 1) + '] ADD [' + [flw].[RemBrackets](v.ColumnName)
               + '] ' + v.DataTypeExp + ' NULL;' AS SrcAddColumnCMD
        FROM IngestionSysVirtual AS v
            INNER JOIN flw.Ingestion AS i
                ON v.FlowID = i.FlowID)
    SELECT FlowID,
           SrcDB,
           SrcSch,
           SrcTbl,
           TrgDB,
           TrgSch,
           TrgTbl,
           [flw].[RemBrackets](ColumnName) ColumnName,
           OrdPos,
           DataType,
           Coll,
           [flw].[RemBrackets](ColumnName) ColClean,
           IsKey,
           [IsDate],
           DataTypeExp,
           REPLACE(SelectExp,'@ColName',ColumnName) AS SelectExp,
           SrcAddColumnCMD
    FROM VirtualCols;

END;
GO
