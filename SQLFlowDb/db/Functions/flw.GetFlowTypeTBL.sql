SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetFlowTypeTBL]
  -- Date				:   2020.11.06
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The purpose of this user-defined function named "flw.GetFlowTypeTBL" is to determine the type of the given FlowID 
							by checking various pre-ingestion and post-ingestion configurations.
  -- Summary			:	The function returns a table that includes the FlowID, the FlowType determined based on the configuration parameters, and whether the source is Azure Container or not.
							The function checks various pre-ingestion configurations such as CSV, XLS, XML, and ADO, as well as post-ingestion configurations such as Invoke and Export. 
							Based on the configuration parameters, it determines the FlowType and sets the SourceIsAzCont flag accordingly.
  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2020.11.06		Initial
  ##################################################################################################################################################
*/
CREATE FUNCTION [flw].[GetFlowTypeTBL]
(
    @FlowID INT
)
RETURNS @List TABLE
(
    FlowID INT,
    FlowType VARCHAR(50),
    SourceIsAzCont BIT
        DEFAULT 0
)
AS
BEGIN

    DECLARE @FlowType VARCHAR(25);
    DECLARE @SourceIsAzCont BIT = 0;

    IF (@FlowType IS NULL)
    BEGIN
        SELECT @FlowType = [FlowType],
               @SourceIsAzCont = 0
        FROM [flw].[Ingestion]
        WHERE FlowID = @FlowID;
    END;

    IF (@FlowType IS NULL)
    BEGIN
        SELECT @FlowType = [FlowType],
               @SourceIsAzCont = CASE
                                     WHEN LEN(ISNULL(csv.[ServicePrincipalAlias], '')) = 0 THEN
                                         0
                                     ELSE
                                         1
                                 END
        FROM [flw].[PreIngestionCSV] AS csv
            LEFT OUTER JOIN [flw].[SysServicePrincipal] ss
                ON csv.[ServicePrincipalAlias] = ss.[ServicePrincipalAlias]
        WHERE FlowID = @FlowID;
    END;

    IF (@FlowType IS NULL)
    BEGIN
        SELECT @FlowType = [FlowType],
               @SourceIsAzCont = CASE
                                     WHEN LEN(ISNULL(xls.[ServicePrincipalAlias], '')) = 0 THEN
                                         0
                                     ELSE
                                         1
                                 END
        FROM [flw].[PreIngestionXLS] xls
            LEFT OUTER JOIN [flw].[SysServicePrincipal] ss
                ON xls.[ServicePrincipalAlias] = ss.[ServicePrincipalAlias]
        WHERE FlowID = @FlowID;
    END;

    IF (@FlowType IS NULL)
    BEGIN
        SELECT @FlowType = [FlowType],
               @SourceIsAzCont = CASE
                                     WHEN LEN(ISNULL(xml.[ServicePrincipalAlias], '')) = 0 THEN
                                         0
                                     ELSE
                                         1
                                 END
        FROM [flw].[PreIngestionXML] xml
            LEFT OUTER JOIN [flw].[SysServicePrincipal] ss
                ON xml.[ServicePrincipalAlias] = ss.[ServicePrincipalAlias]
        WHERE FlowID = @FlowID;
    END;

    IF (@FlowType IS NULL)
    BEGIN
        SELECT @FlowType = [FlowType]
        FROM flw.StoredProcedure
        WHERE FlowID = @FlowID;
    END;

    IF (@FlowType IS NULL)
    BEGIN
        SELECT @FlowType = 'inv',
               @SourceIsAzCont = CASE
                                     WHEN LEN(ISNULL(sc.trgServicePrincipalAlias, '')) > 0
                                          OR LEN(ISNULL(sc.trgServicePrincipalAlias, '')) > 0 THEN
                                         1
                                     ELSE
                                         0
                                 END
        FROM flw.Invoke sc
            LEFT OUTER JOIN [flw].[SysServicePrincipal] ss
                ON sc.trgServicePrincipalAlias = ss.[ServicePrincipalAlias]
        WHERE FlowID = @FlowID;
    END;

    IF (@FlowType IS NULL)
    BEGIN
        SELECT @FlowType = [FlowType],
               @SourceIsAzCont = 0
        FROM [flw].[PreIngestionADO]
        WHERE FlowID = @FlowID;
    END;

    IF (@FlowType IS NULL)
    BEGIN
        SELECT @FlowType = [FlowType],
               @SourceIsAzCont = CASE
                                     WHEN LEN(ISNULL(e.[ServicePrincipalAlias], '')) > 0 THEN
                                         1
                                     ELSE
                                         0
                                 END
        FROM [flw].[Export] e
            LEFT OUTER JOIN [flw].[SysServicePrincipal] ss
                ON e.[ServicePrincipalAlias] = ss.[ServicePrincipalAlias]
        WHERE FlowID = @FlowID;
    END;

    IF (@FlowType IS NULL)
    BEGIN
        SELECT @FlowType = [FlowType],
               @SourceIsAzCont = CASE
                                     WHEN LEN(ISNULL(prq.[ServicePrincipalAlias], '')) = 0 THEN
                                         0
                                     ELSE
                                         1
                                 END
        FROM [flw].[PreIngestionPRQ] prq
            LEFT OUTER JOIN [flw].[SysServicePrincipal] ss
                ON prq.[ServicePrincipalAlias] = ss.[ServicePrincipalAlias]
        WHERE FlowID = @FlowID;
    END;

    IF (@FlowType IS NULL)
    BEGIN
        SELECT @FlowType = [FlowType],
               @SourceIsAzCont = CASE
                                     WHEN LEN(ISNULL(PRC.[ServicePrincipalAlias], '')) = 0 THEN
                                         0
                                     ELSE
                                         1
                                 END
        FROM [flw].[PreIngestionPRC] PRC
            LEFT OUTER JOIN [flw].[SysServicePrincipal] ss
                ON PRC.[ServicePrincipalAlias] = ss.[ServicePrincipalAlias]
        WHERE FlowID = @FlowID;
    END;


    IF (@FlowType IS NULL)
    BEGIN
        SELECT @FlowType = [FlowType],
               @SourceIsAzCont = CASE
                                     WHEN LEN(ISNULL(jsn.[ServicePrincipalAlias], '')) = 0 THEN
                                         0
                                     ELSE
                                         1
                                 END
        FROM [flw].[PreIngestionJSN] jsn
            LEFT OUTER JOIN [flw].[SysServicePrincipal] ss
                ON jsn.[ServicePrincipalAlias] = ss.[ServicePrincipalAlias]
        WHERE FlowID = @FlowID;
    END;


    INSERT INTO @List
    (
        FlowID,
        FlowType,
        SourceIsAzCont
    )
    SELECT @FlowID,
           LOWER(@FlowType),
           @SourceIsAzCont;

    RETURN;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The purpose of this user-defined function named "flw.GetFlowTypeTBL" is to determine the type of the given FlowID 
							by checking various pre-ingestion and post-ingestion configurations.
  -- Summary			:	The function returns a table that includes the FlowID, the FlowType determined based on the configuration parameters, and whether the source is Azure Container or not.
							The function checks various pre-ingestion configurations such as CSV, XLS, XML, and ADO, as well as post-ingestion configurations such as Invoke and Export. 
							Based on the configuration parameters, it determines the FlowType and sets the SourceIsAzCont flag accordingly.', 'SCHEMA', N'flw', 'FUNCTION', N'GetFlowTypeTBL', NULL, NULL
GO
