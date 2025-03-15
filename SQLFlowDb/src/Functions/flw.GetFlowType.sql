SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetFlowType]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The purpose of this user-defined function is to return the flow type of a given flow ID.
  -- Summary			:	The flow type can be any of the following:

							'csv' (for pre-ingestion CSV)
							'xls' (for pre-ingestion XLS)
							'xml' (for pre-ingestion XML)
							'sp' (for pre/post stored procedures)
							'inv' (for invoke)
							'ado' (for pre-ingestion ADO)
							'exp' (for export)

							If the flow type is not found in any of the above tables, the function returns NULL.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/

CREATE FUNCTION [flw].[GetFlowType] (@FlowID INT)
RETURNS VARCHAR(25)
BEGIN

    DECLARE @rValue VARCHAR(25);

    SELECT @rValue = [FlowType]
      FROM flw.LineageEdge
     WHERE FlowID = @FlowID
       

    IF (@rValue IS NULL)
    BEGIN
        SELECT @rValue = [FlowType]
          FROM [flw].[Ingestion]
         WHERE FlowID = @FlowID;
    END;

    IF (@rValue IS NULL)
    BEGIN
        SELECT @rValue = [FlowType]
          FROM [flw].[PreIngestionCSV]
         WHERE FlowID = @FlowID;
    END;

    IF (@rValue IS NULL)
    BEGIN
        SELECT @rValue = [FlowType]
          FROM [flw].[PreIngestionXLS]
         WHERE FlowID = @FlowID;
    END;

    IF (@rValue IS NULL)
    BEGIN
        SELECT @rValue = [FlowType]
          FROM [flw].[PreIngestionXML]
         WHERE FlowID = @FlowID;
    END;

    IF (@rValue IS NULL)
    BEGIN
        SELECT @rValue = [FlowType]
          FROM flw.StoredProcedure
         WHERE FlowID = @FlowID;
    END;

	IF (@rValue IS NULL)
    BEGIN
        SELECT @rValue = 'inv'
          FROM flw.Invoke sc
         WHERE FlowID = @FlowID;
    END;

	IF (@rValue IS NULL)
    BEGIN
        SELECT @rValue = 'ado'
          FROM [flw].[PreIngestionADO] sc
         WHERE FlowID = @FlowID;
    END;

	IF (@rValue IS NULL)
    BEGIN
        SELECT @rValue = 'exp'
          FROM [flw].[Export] sc
         WHERE FlowID = @FlowID;
    END;


	IF (@rValue IS NULL)
    BEGIN
        SELECT @rValue = 'jsn'
          FROM [flw].[Export] sc
         WHERE FlowID = @FlowID;
    END;


	IF (@rValue IS NULL)
    BEGIN
        SELECT @rValue = 'sub'
          FROM [flw].[DataSubscriber] sc
         WHERE FlowID = @FlowID;
    END;


    RETURN LOWER(@rValue);
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The purpose of this user-defined function is to return the flow type of a given flow ID.
  -- Summary			:	The flow type can be any of the following:

							''csv'' (for pre-ingestion CSV)
							''xls'' (for pre-ingestion XLS)
							''xml'' (for pre-ingestion XML)
							''sp'' (for pre/post stored procedures)
							''inv'' (for invoke)
							''ado'' (for pre-ingestion ADO)
							''exp'' (for export)

							If the flow type is not found in any of the above tables, the function returns NULL.', 'SCHEMA', N'flw', 'FUNCTION', N'GetFlowType', NULL, NULL
GO
