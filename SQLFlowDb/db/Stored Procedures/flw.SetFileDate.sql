SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
/*
  ##################################################################################################################################################
  -- Name				:   [flw].[Reset]
  -- Date				:   2023-03-29
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure resets logged ingestion date for imported files. Enables reprocessing of the source folder.
  -- Summary			:	
  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2023-08-19		Initial
  ##################################################################################################################################################
*/

CREATE PROCEDURE [flw].[SetFileDate]
    --Reset file date to number of overlaping days. defualt is 0
    @FlowID INT = 0,
    @Batch VARCHAR(2000) = '',
    @FileDate VARCHAR(15) = '-1'
AS
BEGIN

    CREATE TABLE #FlowIDs
    (
        FlowID INT
    );
    IF (LEN(@Batch) > 1)
    BEGIN
        INSERT INTO #FlowIDs
		SELECT FlowID
        FROM [flw].[FlowDS]
        WHERE Batch IN
              (
                  SELECT Item FROM [flw].[StringSplit](@Batch, ',')
              )
              AND FlowType IN ( 'csv', 'xls', 'xml', 'jsn', 'prq' );
    END;
    ELSE
    BEGIN
        INSERT INTO #FlowIDs
        (
            FlowID
        )
        SELECT @FlowID;
    END;


    IF (@FileDate = '-1')
    BEGIN
        UPDATE Trg
        SET Trg.[FileDate] = CASE WHEN Trg.FileDateHist > Trg.[FileDate] THEN Trg.FileDateHist ELSE Trg.[FileDate] END,
            Trg.FileDateHist = NULL
        FROM [flw].[SysLog] Trg
        WHERE FlowID IN
              (
                  SELECT FlowID FROM #FlowIDs
              );

        --Reset [FetchMinValuesFromSrc] to 0
        UPDATE trg
        SET trg.[FetchMinValuesFromSrc] = 0
        FROM [flw].[Ingestion] trg
        WHERE FlowID IN
              (
                  SELECT Item AS FlowID
                  FROM [flw].[LineageMap]
                      CROSS APPLY
                  (SELECT Item FROM [flw].[StringSplit]([NextStepFlows], ',') ) a
                  WHERE LEN([NextStepFlows]) > 0
                        AND FlowID IN
                            (
                                SELECT FlowID FROM #FlowIDs
                            )
              );

    END;
    ELSE
    BEGIN
        UPDATE Trg
        SET Trg.[FileDate] = @FileDate,
            Trg.FileDateHist = CASE
                                   WHEN Trg.FileDateHist IS NULL THEN
                                       Trg.[FileDate]
                                   WHEN CAST(@FileDate AS BIGINT) < CAST(Trg.FileDateHist AS BIGINT) THEN
                                       Trg.FileDateHist
                                   ELSE
                                       Trg.[FileDate]
                               END
        FROM [flw].[SysLog] Trg
        WHERE FlowID IN
              (
                  SELECT FlowID FROM #FlowIDs
              );

        --Changing the file date means re-processing the files. Its natural to set the [FetchMinValuesFromSrc] so the whole staging table is fetched for evalulation.
        UPDATE trg
        SET trg.[FetchMinValuesFromSrc] = 1
        FROM [flw].[Ingestion] trg
        WHERE FlowID IN
              (
                  SELECT Item AS FlowID
                  FROM [flw].[LineageMap]
                      CROSS APPLY
                  (SELECT Item FROM [flw].[StringSplit]([NextStepFlows], ',') ) a
                  WHERE LEN([NextStepFlows]) > 0
                        AND FlowID IN
                            (
                                SELECT FlowID FROM #FlowIDs
                            )
              );

    END;

END;
GO
