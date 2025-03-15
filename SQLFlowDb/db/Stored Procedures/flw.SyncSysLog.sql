SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO


/*
  ##################################################################################################################################################
  -- Name				:   [flw].[SyncSysLog]
  -- Date				:   2020.11.08
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The purpose of this stored procedure is to synchronize the "SysLog" table within the "[flw]" schema with the "FlowDS" table, also within the "[flw]" schema. 
							The "SysLog" table stores logs of various processes and flows in the system, while the "FlowDS" table stores information about the data flows in the system.
  -- Summary			:	The stored procedure achieves this by performing various update, insert, and delete statements on the "SysLog" table. Firstly, 
							an update statement is performed to update the columns "FlowType", "Process", "batch", and "SysAlias" in the "SysLog" table based on the matching "FlowID" in the "FlowDS" table. 
							Then, an insert statement is performed to insert new rows into the "SysLog" table based on the "FlowID" in the "FlowDS" table where a matching "FlowID" doesn't exist in the "SysLog" table. 
							Finally, two delete statements are performed to delete rows from the "SysLog" and "SysStats" tables where the "FlowID" doesn't exist in the "FlowDS" table.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2020.11.08		Initial
  ##################################################################################################################################################
*/


CREATE PROCEDURE [flw].[SyncSysLog] @dbg INT = 0 -- Debug level
AS
BEGIN

    DECLARE @curObjName NVARCHAR(255),
            @curExecCmd NVARCHAR(4000),
            @curSection NVARCHAR(4000),
            @curCode NVARCHAR(MAX);

    IF (@dbg > 1)
    BEGIN
        SET @curObjName = ISNULL(OBJECT_SCHEMA_NAME(@@PROCID) + '.' + OBJECT_NAME(@@PROCID), 'Defualt SP Name');
        SET @curExecCmd = N'exec ' + @curObjName + N', @dbg=' + CAST(@dbg AS NVARCHAR(20));
        PRINT [flw].[GetLogHeader](@curObjName, @curExecCmd);
    END;

    IF (@@NESTLEVEL > 1 AND @dbg = 1)
        SET @dbg = 0;

    -- SET NOCOUNT ON added to prevent extra result sets from
    -- interfering with SELECT statements.
    SET NOCOUNT ON;


    UPDATE trg
    SET trg.[FlowType] = src.[FlowType],
        trg.[Process] = src.[Process],
        trg.Batch = src.Batch,
        trg.SysAlias = src.SysAlias,
        trg.[ProcessShort] = src.[ProcessShort],
        trg.[srcAlias] = src.[srcAlias],
        trg.[trgAlias] = src.[trgAlias]
    FROM [flw].[SysLog] trg
        INNER JOIN [flw].[FlowDS] src
            ON trg.[FlowID] = src.FlowID;


  --  UPDATE trg
  --  SET trg.[FlowType] = src.[FlowType],
  --      trg.[Process] = '-->' + [SubscriberName],
  --      trg.[ProcessShort] = '-->' + [SubscriberName],
		--trg.Batch = src.Batch,
  --      trg.SysAlias = src.Batch
  --  FROM [flw].[SysLog] trg
  --      INNER JOIN [flw].[DataSubscriber] src
  --          ON trg.[FlowID] = src.FlowID;


    -- Insert statements for procedure here
    INSERT INTO [flw].[SysLog]
    (
        [FlowID],
        [FlowType],
        Batch,
        SysAlias,
        [Process],
        [ProcessShort]
    )
    SELECT src.FlowID,
           src.FlowType,
           src.Batch,
           src.SysAlias,
           src.[Process],
           src.[ProcessShort]
    FROM [flw].[FlowDS] src
        LEFT OUTER JOIN [flw].[SysLog] trg
            ON trg.[FlowID] = src.FlowID
               AND trg.[FlowType] = src.FlowType
    WHERE trg.[FlowID] IS NULL;


    DELETE trg
    FROM [flw].[SysLog] trg
    WHERE trg.[FlowID] NOT IN
          (
              SELECT FlowID FROM [flw].[FlowDS]
          );


    DELETE trg
    FROM [flw].[SysStats] trg
    WHERE trg.[FlowID] NOT IN
          (
              SELECT FlowID FROM [flw].[FlowDS]
          );

    --Delete Orphan Records
    DELETE FROM [flw].[PreIngestionTransfrom]
    WHERE FlowID NOT IN
          (
              SELECT FlowID FROM flw.FlowDS
          );

    DELETE FROM [flw].[IngestionVirtual]
    WHERE FlowID NOT IN
          (
              SELECT FlowID FROM flw.FlowDS
          );

    DELETE FROM [flw].[PreIngestionADOVirtual]
    WHERE FlowID NOT IN
          (
              SELECT FlowID FROM flw.FlowDS
          );





END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The purpose of this stored procedure is to synchronize the "SysLog" table within the "[flw]" schema with the "FlowDS" table, also within the "[flw]" schema. 
							The "SysLog" table stores logs of various processes and flows in the system, while the "FlowDS" table stores information about the data flows in the system.
  -- Summary			:	The stored procedure achieves this by performing various update, insert, and delete statements on the "SysLog" table. Firstly, 
							an update statement is performed to update the columns "FlowType", "Process", "batch", and "SysAlias" in the "SysLog" table based on the matching "FlowID" in the "FlowDS" table. 
							Then, an insert statement is performed to insert new rows into the "SysLog" table based on the "FlowID" in the "FlowDS" table where a matching "FlowID" doesn''t exist in the "SysLog" table. 
							Finally, two delete statements are performed to delete rows from the "SysLog" and "SysStats" tables where the "FlowID" doesn''t exist in the "FlowDS" table.', 'SCHEMA', N'flw', 'PROCEDURE', N'SyncSysLog', NULL, NULL
GO
