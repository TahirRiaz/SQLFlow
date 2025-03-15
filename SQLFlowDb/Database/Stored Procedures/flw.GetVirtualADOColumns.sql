SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO



/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetVirtualADOColumns]
  -- Date				:   2022.11.12
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure provides a dataset for virtual columns
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022.11.12		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[GetVirtualADOColumns] @FlowID INT -- FlowID from [flw].[Ingestion]

AS
BEGIN

    SELECT [VirtualID],
           [FlowID],
           [ColumnName],
           [DataType],
           CAST(ISNULL([Length], '') AS VARCHAR(25)) AS [Length],
           CAST(ISNULL([Precision], '') AS VARCHAR(25)) AS [Precision],
           CAST(ISNULL([Scale], '') AS VARCHAR(25)) AS [Scale],
           [SelectExp]
    FROM [flw].[PreIngestionADOVirtual]
    WHERE FlowID = @FlowID;

END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedure provides a dataset for virtual columns', 'SCHEMA', N'flw', 'PROCEDURE', N'GetVirtualADOColumns', NULL, NULL
GO
