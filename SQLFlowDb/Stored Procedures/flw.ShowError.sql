SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS OFF
GO




/*
  ##################################################################################################################################################
  -- Name				:   [flw].[Search]
  -- Date				:   2023.04.20
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure returns all flows with an error
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2023.04.20		Initial
  ##################################################################################################################################################
*/

CREATE PROCEDURE [flw].[ShowError]
  
AS
BEGIN
    SET NOCOUNT ON;
    SET ANSI_WARNINGS OFF;
    --DECLARE @RelevantObjectNames NVARCHAR(4000) = N'',
    --        @FlowID              INT            = 0;

    --DECLARE @ObjName NVARCHAR(255) = 437;
    --Find Relevant FlowIDS
    SELECT 'search ' + CAST(a.[FlowID] AS VARCHAR(255)) AS Search,
           CASE
               WHEN a.FlowType = 'ing' THEN
                   'SetMinFromSrc ' + CAST(a.[FlowID] AS VARCHAR(255)) + ',1'
               ELSE
                   ''
           END AS SetMinFromSrc,
           CASE
               WHEN a.FlowType IN ( 'csv', 'xml', 'xls', 'exp', 'prq','jsn','prc' ) THEN
                   'reset ' + CAST(a.[FlowID] AS VARCHAR(255))
               ELSE
                   ''
           END AS Reset,
           CASE
               WHEN a.FlowType IN ( 'csv', 'xml', 'xls', 'prq' ,'jsn','prc') THEN
                   'flowfiles ' + CAST(a.[FlowID] AS VARCHAR(255))
               ELSE
                   ''
           END AS Files,
           *
    FROM [flw].[SysLog] a
    WHERE Success = 0
	ORDER BY a.StartTime desc

END;
GO
