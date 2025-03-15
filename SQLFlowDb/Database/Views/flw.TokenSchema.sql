SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO













CREATE VIEW [flw].[TokenSchema]
AS
SELECT t.TokenID,
       t.FlowID,
       REPLACE(   i.trgDBSchTbl,
                             (
                                 SELECT TOP 1
                                        ParamValue
                                 FROM flw.SysCFG
                                 WHERE ParamName = 'Schema04Trusted'
                             ),
                             (
                                 SELECT ParamValue
                                 FROM flw.SysCFG
                                 WHERE ParamName = 'Schema01Raw'
                             )
                                    ) trgDBSchTbl,
       OBJECT_ID(i.trgDBSchTbl) AS OBJECTID,
       s.TABLE_CATALOG,
       s.TABLE_SCHEMA,
       s.TABLE_NAME,
       s.COLUMN_NAME,
       t.TokenExpAlias,
       ISNULL(REPLACE(r.SelectExp,'@ColName', s.COLUMN_NAME),'') AS SelectExp,
	   ISNULL(REPLACE(r.SelectExpFull,'@ColName', s.COLUMN_NAME),'') AS SelectExpFull,
       r.DataType DataTypeTE
FROM flw.IngestionTokenExp AS r
    RIGHT OUTER JOIN flw.IngestionTokenize AS t
        INNER JOIN flw.Ingestion AS i
            ON t.FlowID = i.FlowID
        INNER JOIN INFORMATION_SCHEMA.COLUMNS AS s
            ON t.ColumnName = s.COLUMN_NAME
               AND OBJECT_ID(REPLACE(   i.trgDBSchTbl,
                             (
                                 SELECT TOP 1
                                        ParamValue
                                 FROM flw.SysCFG
                                 WHERE ParamName = 'Schema04Trusted'
                             ),
                             (
                                 SELECT ParamValue
                                 FROM flw.SysCFG
                                 WHERE ParamName = 'Schema01Raw'
                             )
                                    )
                            ) = OBJECT_ID('[' + s.TABLE_CATALOG + '].[' + s.TABLE_SCHEMA + '].[' + s.TABLE_NAME + ']')
        ON r.TokenExpAlias = t.TokenExpAlias
GO
