SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[AddIngestionVirtual]
  -- Date				:   2022-12-03
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure is responsible for updating or inserting virtual ingestion columns in the [flw].[IngestionVirtual] table.
  -- Summary			:	It accepts the following input parameters:

							@FlowID
							@ColumnName
							@DataType
							@DataTypeExp
							@SelectExp
							It starts by updating the record in the [flw].[IngestionVirtual] table with the specified FlowID and ColumnName, 
							setting the DataType, DataTypeExp, and SelectExp fields with the provided values from input parameters.

							It then uses a CTE (Common Table Expression) named 'base' to store the input parameter values in a single row.

							It inserts a new record into the [flw].[IngestionVirtual] table if a record with the specified FlowID and ColumnName does not exist.
							This is done by performing a LEFT OUTER JOIN between the 'base' CTE and the [flw].[IngestionVirtual] table on FlowID and ColumnName, 
							and then filtering for rows where trg.ColumnName is NULL.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-12-03		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[AddIngestionVirtual]
    @FlowID [INT],
    @ColumnName [NVARCHAR](250),
    @DataType [NVARCHAR](250),
    @DataTypeExp [NVARCHAR](250),
    @SelectExp [NVARCHAR](MAX)
AS
BEGIN

    UPDATE TOP (1)
        [flw].[IngestionVirtual]
    SET DataType = @DataType,
        DataTypeExp = @DataTypeExp,
        SelectExp = @SelectExp
    WHERE [FlowID] = @FlowID
          AND ColumnName = @ColumnName;
    ;WITH base
    AS (SELECT @FlowID FlowID,
               @ColumnName ColumnName,
               @DataType DataType,
               @DataTypeExp DataTypeExp,
               @SelectExp SelectExp)
    INSERT INTO [flw].[IngestionVirtual]
    (
        [FlowID],
        [ColumnName],
        [DataType],
        [DataTypeExp],
        [SelectExp]
    )
    SELECT src.[FlowID],
           src.[ColumnName],
           src.[DataType],
           src.[DataTypeExp],
           src.[SelectExp]
    FROM base src
        LEFT OUTER JOIN [flw].[IngestionVirtual] trg
            ON src.[FlowID] = trg.[FlowID]
               AND src.ColumnName = trg.ColumnName
    WHERE trg.ColumnName IS NULL;



END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedure is responsible for updating or inserting virtual ingestion columns in the [flw].[IngestionVirtual] table.
  -- Summary			:	It accepts the following input parameters:

							@FlowID
							@ColumnName
							@DataType
							@DataTypeExp
							@SelectExp
							It starts by updating the record in the [flw].[IngestionVirtual] table with the specified FlowID and ColumnName, 
							setting the DataType, DataTypeExp, and SelectExp fields with the provided values from input parameters.

							It then uses a CTE (Common Table Expression) named ''base'' to store the input parameter values in a single row.

							It inserts a new record into the [flw].[IngestionVirtual] table if a record with the specified FlowID and ColumnName does not exist.
							This is done by performing a LEFT OUTER JOIN between the ''base'' CTE and the [flw].[IngestionVirtual] table on FlowID and ColumnName, 
							and then filtering for rows where trg.ColumnName is NULL.', 'SCHEMA', N'flw', 'PROCEDURE', N'AddIngestionVirtual', NULL, NULL
GO
