SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetTokenSchema]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure generates schema dynamicaly for tokenization process in [flw].[TokenizeRaw].
  -- Summary			:	This sp retrieves the token schema information from the given raw table using ingestion token expressions and tokenizations. 
							It takes in the raw table name and optional parameters like mode, debug flag, and columns list as inputs. The procedure generates a SQL statement 
							to fetch the required information from the database and stores the output in the @ColumnsList, @vltColumnList, and @TokenSchema variables. 
							
							If the mode parameter is set to 1, the procedure returns the output. The debug flag can be used to print the execution details of the stored procedure.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[GetTokenSchema]
    @rawTable NVARCHAR(255), -- Raw/ staging table
    @mode INT = 1,           --
    @dbg INT = 0,            -- Debug Level
    @ColumnsList NVARCHAR(MAX) = '' OUTPUT,
    @vltColumnList NVARCHAR(MAX) = '' OUTPUT,
    @TokenSchema NVARCHAR(MAX) = '' OUTPUT
AS
BEGIN

    DECLARE @curObjName NVARCHAR(255),
            @curExecCmd NVARCHAR(4000),
            @curSection NVARCHAR(4000),
            @curCode NVARCHAR(MAX);


    IF (@dbg >= 1)
    BEGIN
        SET @curObjName = ISNULL(OBJECT_SCHEMA_NAME(@@PROCID) + '.' + OBJECT_NAME(@@PROCID), 'Defualt SP Name');
        SET @curExecCmd
            = N'exec ' + @curObjName + N' @rawTable=''' + @rawTable + N''', @mode=' + CAST(@mode AS NVARCHAR(255))
              + N', @dbg=' + CAST(@dbg AS NVARCHAR(255));
        PRINT [flw].[GetLogHeader](@curObjName, @curExecCmd);
    END;

    IF (@@NESTLEVEL > 1 AND @dbg = 1)
        SET @dbg = 0;


    DECLARE @ObjectID NVARCHAR(255),
            @srcDatabase NVARCHAR(255);

    SELECT @ObjectID = OBJECT_ID(@rawTable),
           @srcDatabase = PARSENAME(@rawTable, 3);

    --SELECT @ObjectID, @srcDatabase

    DECLARE @SQL NVARCHAR(MAX);
    SET @SQL
        = N'WITH TokenSchemaView
AS
(
SELECT t.TokenID,
       t.FlowID,
       REPLACE(   i.trgDBSchTbl,
                             (
                                 SELECT TOP 1
                                        ParamValue
                                 FROM [flw].[SysCFG]
                                 WHERE ParamName = ''Schema04Trusted''
                             ),
                             (
                                 SELECT ParamValue
                                 FROM [flw].[SysCFG]
                                 WHERE ParamName = ''Schema01Raw''
                             )
                                    ) trgDBSchTbl,
       OBJECT_ID(i.trgDBSchTbl) AS OBJECTID,
       s.TABLE_CATALOG,
       s.TABLE_SCHEMA,
       s.TABLE_NAME,
       s.COLUMN_NAME,
       t.TokenExpAlias,
	   r.[DataType],
       ISNULL(REPLACE(r.SelectExp,''@ColName'', ''['' +s.COLUMN_NAME + '']''),'''') AS SelectExp,
	   ISNULL(REPLACE(r.SelectExpFull,''@ColName'', ''['' +s.COLUMN_NAME + '']''),'''') AS SelectExpFull 
FROM flw.IngestionTokenExp AS r
    RIGHT OUTER JOIN flw.IngestionTokenize AS t
        INNER JOIN flw.Ingestion AS i
            ON t.FlowID = i.FlowID
        INNER JOIN [' + @srcDatabase
          + N'].INFORMATION_SCHEMA.COLUMNS AS s
            ON [flw].[RemBrackets](t.ColumnName) = s.COLUMN_NAME
			   AND OBJECT_ID(''['' + s.[TABLE_CATALOG] +''].['' + s.[TABLE_SCHEMA] + ''].['' + s.[TABLE_NAME] + '']'') = '
          + @ObjectID
          + N' AND OBJECT_ID(REPLACE(   i.trgDBSchTbl,
                             (
                                 SELECT TOP 1
                                        ParamValue
                                 FROM [flw].[SysCFG]
                                 WHERE ParamName = ''Schema04Trusted''
                             ),
                             (
                                 SELECT ParamValue
                                 FROM [flw].[SysCFG]
                                 WHERE ParamName = ''Schema01Raw''
                             )
                                    )
                            ) = OBJECT_ID(''['' + s.TABLE_CATALOG + ''].['' + s.TABLE_SCHEMA + ''].['' + s.TABLE_NAME + '']'')
        ON r.TokenExpAlias = t.TokenExpAlias
)
SELECT @vltColumnList= @vltColumnList + '','' + COLUMN_NAME, @ColumnsList = @ColumnsList + '','' + TokenValue + '','' + FTValue + '','' + SrcValue,
@TokenSchema = ISNULL(REPLACE((SELECT TABLE_NAME,[COLUMN_NAME],ISNULL([DataType], '''') AS [DataType]
				FROM TokenSchemaView FOR XML RAW, ROOT(''TokenSchema'')),''"'',''"''),
				''<TokenSchema><row TABLE_NAME="" COLUMN_NAME="" DataType="" /></TokenSchema>'')
	FROM
	(
		SELECT CASE
				   WHEN LEN(t.[SelectExp]) > 0 THEN
					   t.[SelectExp] + '' AS ''
				   ELSE
					   ''''
			   END + ''['' + cl.[COLUMN_NAME] + '']'' AS TokenValue,
			   CASE
				   WHEN LEN(t.[SelectExpFull]) > 0 THEN
					   t.[SelectExpFull] + '' AS '' + ''['' + cl.[COLUMN_NAME] + ''_FT]''
				   ELSE
					   ''''
			   END FTValue,
			   ''['' + cl.[COLUMN_NAME] + ''] AS '' + ''['' + cl.[COLUMN_NAME] + ''_SRC]'' AS SrcValue,
			   cl.ORDINAL_POSITION,
			   cl.[COLUMN_NAME]
		FROM [' + @srcDatabase
          + N'].INFORMATION_SCHEMA.COLUMNS cl
			INNER JOIN TokenSchemaView t
				ON t.[TABLE_SCHEMA] = cl.[TABLE_SCHEMA]
				   AND t.[TABLE_NAME] = cl.[TABLE_NAME]
				   AND cl.COLUMN_NAME = [flw].[RemBrackets](t.COLUMN_NAME)
				   AND OBJECT_ID(''['' + cl.[TABLE_CATALOG] +''].['' + cl.[TABLE_SCHEMA] + ''].['' + cl.[TABLE_NAME] + '']'') = '
          + @ObjectID + N'
	) a
	ORDER BY a.ORDINAL_POSITION;
'   ;

    IF @dbg >= 2
    BEGIN
        SET @curSection = N'10 :: ' + @curObjName + N' :: @SQL - SQL Statment';
        SET @curCode = @SQL;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;

    EXEC sp_executesql @SQL,
                       N'@ColumnsList NVARCHAR(MAX) OUTPUT, @vltColumnList NVARCHAR(MAX) OUTPUT, @TokenSchema NVARCHAR(MAX) OUTPUT',
                       @ColumnsList = @ColumnsList OUTPUT,
                       @vltColumnList = @vltColumnList OUTPUT,
                       @TokenSchema = @TokenSchema OUTPUT;
    IF (@mode = 1)
    BEGIN
        SELECT @ColumnsList AS ColumnsList,
               @vltColumnList AS vltColumnList,
               @TokenSchema AS TokenSchema;
    END;


END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedure generates schema dynamicaly for tokenization process in [flw].[TokenizeRaw].
  -- Summary			:	This sp retrieves the token schema information from the given raw table using ingestion token expressions and tokenizations. 
							It takes in the raw table name and optional parameters like mode, debug flag, and columns list as inputs. The procedure generates a SQL statement 
							to fetch the required information from the database and stores the output in the @ColumnsList, @vltColumnList, and @TokenSchema variables. 
							
							If the mode parameter is set to 1, the procedure returns the output. The debug flag can be used to print the execution details of the stored procedure.', 'SCHEMA', N'flw', 'PROCEDURE', N'GetTokenSchema', NULL, NULL
GO
