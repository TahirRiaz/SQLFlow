SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GenTrgScripts]
  -- Date				:   2022.05.12
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure will generate a dataset for Data Lineage Calculation
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022.05.12		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[GenTrgScripts]
    @Alias NVARCHAR(70)= null, -- FlowID from table [flw].[Ingestion]
    @dbg INT = 0 -- Debug Level
AS
BEGIN

	SELECT * FROM [flw].[ObjectDS]
	WHERE ObjectMK > 0
	AND Alias = CASE WHEN LEN(ISNULL(@Alias,'')) = 0 THEN Alias ELSE @Alias END
	ORDER BY DatabaseName

END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedure will generate a dataset for Data Lineage Calculationlation', 'SCHEMA', N'flw', 'PROCEDURE', N'GenTrgScripts', NULL, NULL
GO
