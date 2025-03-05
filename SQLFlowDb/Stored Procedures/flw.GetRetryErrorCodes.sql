SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetRetryErrorCodes]
  -- Date				:   2020.11.08
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure is obsolete and will be removed in future. Retry codes are moved to SQLFlow Engine.
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2020.11.08		Initial
  ##################################################################################################################################################
*/

CREATE PROCEDURE [flw].[GetRetryErrorCodes] @dbg INT = 0 -- Debug Level

AS
BEGIN

    DECLARE @curObjName NVARCHAR(255),
            @curExecCmd NVARCHAR(4000),
            @curSection NVARCHAR(4000),
            @curCode NVARCHAR(MAX);


    IF (@dbg >= 1)
    BEGIN
        SET @curObjName = ISNULL(OBJECT_SCHEMA_NAME(@@PROCID) + '.' + OBJECT_NAME(@@PROCID), 'Defualt SP Name');
        SET @curExecCmd = N'exec ' + @curObjName;
        PRINT [flw].[GetLogHeader](@curObjName, @curExecCmd);
    END;


    SELECT '{"' + CAST(error AS VARCHAR(255)) + '","' + REPLACE(description, '"', '') + '"},'
    FROM sys.sysmessages WITH (READPAST)
    WHERE msglangid = 1033
          AND
          (
              error IN ( 40197, 40501, 10928, 10929, 40553, 40615, 40544, 40549, 40550, 40551, 40552, 40613, 40532,
                         40611, 4060, 1205, 3989, 3965, 3919, 3903
                       )
              OR description LIKE '%try%later.'
              OR description LIKE '%. rerun the%'
              OR description LIKE '%connection%terminated%'
              OR description LIKE '%time out%'
              OR description LIKE '%throttle%'
          )
          AND description NOT LIKE '%resolve%'
          AND description NOT LIKE '%and try%'
          AND description NOT LIKE '%and retry%';

END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedure is obsolete and will be removed in future. Retry codes are moved to SQLFlow Engine.', 'SCHEMA', N'flw', 'PROCEDURE', N'GetRetryErrorCodes', NULL, NULL
GO
