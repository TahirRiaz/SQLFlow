SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetFlowType]
  -- Date				:   2023-09-21
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   
  -- Summary			:	

							If the flow type is not found in any of the above tables, the function returns NULL.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/

CREATE FUNCTION [flw].[GetFlowTypeUiUrl]
(
    @FlowType VARCHAR(25)
)
RETURNS VARCHAR(250)
BEGIN

    DECLARE @rValue VARCHAR(25);

    SET @rValue = CASE
                      WHEN @FlowType = 'exp' THEN
                          '/export/'
                      WHEN @FlowType = 'ing' THEN
                          '/ingestion/'
                      WHEN @FlowType = 'skey' THEN
                          '/surrogate-key/'
					  WHEN @FlowType = 'mkey' THEN
                          '/match-key/'
                      WHEN @FlowType = 'ado' THEN
                          '/pre-ingestion-ado/'
                      WHEN @FlowType = 'csv' THEN
                          '/pre-ingestion-csv/'
                      WHEN @FlowType = 'jsn' THEN
                          '/pre-ingestion-jsn/'
                      WHEN @FlowType = 'prc' THEN
                          '/pre-ingestion-prc/'
                      WHEN @FlowType = 'prq' THEN
                          '/pre-ingestion-prq/'
                      WHEN @FlowType = 'xls' THEN
                          '/pre-ingestion-xls/'
                      WHEN @FlowType = 'xml' THEN
                          '/pre-ingestion-xml/'
                      WHEN @FlowType = 'sp' THEN
                          '/stored-procedure/'
                      WHEN @FlowType IN ( 'aut', 'inv','adf','cs') THEN
                          '/invoke/'
                      ELSE
                          ''
                  END;

    RETURN @rValue;
END;
GO
