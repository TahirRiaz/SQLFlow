SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetPreIngTransStatus]
  -- Date				:   2021.04.26
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The purpose of the provided user-defined function [flw].[GetPreIngTransStatus] is to check the pre-ingestion transformation status for a given FlowID.
  -- Summary			:	The function takes an input parameter @FlowID, which is used to fetch the pre-ingestion transformation details from 
							the [flw].[PreIngestionTransfrom] table. If no pre-ingestion transformation details are found for the specified FlowID, 
							the function returns 1 indicating that the pre-ingestion transformation has not been configured yet.

							If pre-ingestion transformation details are found for the specified FlowID, the function checks the number of rows where the SelectExp column has a length of 0. 
							If there are more than one such rows, the function returns 1 indicating that the pre-ingestion transformation has not been configured properly. 
							Otherwise, the function returns 0 indicating that the pre-ingestion transformation has been configured properly.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2021.04.26		Initial
  ##################################################################################################################################################
*/
CREATE FUNCTION [flw].[GetPreIngTransStatus]
(
    @FlowID INT
)
RETURNS BIT
BEGIN

    DECLARE @rValue BIT = 0;
    DECLARE @dupe INT;

    SELECT @dupe = ISNULL(COUNT(*), 0)
    FROM flw.PreIngestionTransfrom
    WHERE (FlowID = @FlowID);
    --AND Len(IsNull(SelectExp,''))> 0

    IF (@dupe = 0)
    BEGIN
        SET @rValue = 1;
    END;
    ELSE
    BEGIN
        SELECT @dupe = ISNULL(COUNT(*), 0)
        FROM flw.PreIngestionTransfrom
        WHERE (FlowID = @FlowID)
              AND LEN(ISNULL(SelectExp, '')) = 0;

        IF (@dupe > 1)
        BEGIN
            SET @rValue = 1;
        END;
    END;

    RETURN @rValue;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The purpose of the provided user-defined function [flw].[GetPreIngTransStatus] is to check the pre-ingestion transformation status for a given FlowID.
  -- Summary			:	The function takes an input parameter @FlowID, which is used to fetch the pre-ingestion transformation details from 
							the [flw].[PreIngestionTransfrom] table. If no pre-ingestion transformation details are found for the specified FlowID, 
							the function returns 1 indicating that the pre-ingestion transformation has not been configured yet.

							If pre-ingestion transformation details are found for the specified FlowID, the function checks the number of rows where the SelectExp column has a length of 0. 
							If there are more than one such rows, the function returns 1 indicating that the pre-ingestion transformation has not been configured properly. 
							Otherwise, the function returns 0 indicating that the pre-ingestion transformation has been configured properly.', 'SCHEMA', N'flw', 'FUNCTION', N'GetPreIngTransStatus', NULL, NULL
GO
