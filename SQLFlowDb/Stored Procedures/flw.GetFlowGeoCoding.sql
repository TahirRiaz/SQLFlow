SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO


/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetFlowGeoCoding]
  -- Date				:   2023.06.15
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure will fetch geocoding metadata attached to a flow
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2023.06.15		Initial
  ##################################################################################################################################################
*/

CREATE PROCEDURE [flw].[GetFlowGeoCoding]
    @FlowID int -- FlowID from table [flw].[Ingestion]
	
AS
BEGIN
    
	SELECT [GeoCodingID], [FlowID], [GoogleAPIKey], [KeyColumn], [LonColumn], [LatColumn], [AddressColumn], [trgDBSchTbl]
	FROM [flw].[GeoCoding]
	WHERE [FlowID] = @FlowID
	--CASE WHEN LEN(ISNULL(@Alias,'')) = 0 THEN Alias ELSE @Alias END
	
END;
GO
