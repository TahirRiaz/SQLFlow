SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetVersioningScript]
  -- Date				:   2020.11.08
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure creates script for the creation of Versioning table.
  -- Summary			:	This stored procedure generates dynamic SQL statements to enable system versioning on a specified table in SQL Server. 
							It creates columns ValidFrom_DW, ValidTo_DW and applies period to the SYSTEM_TIME, with HISTORY_TABLE defined in the Schema06Version.

							The stored procedure takes in the base table name and generates SQL statements to enable system versioning. 
							It uses the @Mode parameter to determine if it should return the generated SQL statements or execute them immediately. 
							It also outputs the generated SQL statements for enabling and disabling system versioning.
  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2020.11.08		Initial
  ##################################################################################################################################################
*/

CREATE PROCEDURE [flw].[GetVersioningScript]
    @baseTable NVARCHAR(255),                  -- Source table
    @Mode NVARCHAR(25) = '',                   -- 
    @VersionCMD NVARCHAR(4000) = '' OUTPUT,    -- Creates SQL statement
    @VersionOnCMD NVARCHAR(4000) = '' OUTPUT,  -- Creates SQL statement
    @VersionOffCMD NVARCHAR(4000) = '' OUTPUT, -- Creates SQL statement
    @dbg INT = 0                               -- Debug level
AS
BEGIN

    DECLARE @curObjName NVARCHAR(255),
            @curExecCmd NVARCHAR(4000),
            @curSection NVARCHAR(4000),
            @curCode NVARCHAR(MAX);

    IF (@@NESTLEVEL > 1 AND @dbg = 1)
        SET @dbg = 0;


    --Deactivate 
    --ALTER TABLE SEODS.[tru].[Employee] SET (SYSTEM_Versioning = OFF)
    --DECLARE @VersionCMD NVARCHAR(MAX) = N'',
    DECLARE @Schema06Version NVARCHAR(255),
            @VersionEndCol NVARCHAR(255),
            @VersionStartCol NVARCHAR(255),
            @srcDatabase NVARCHAR(255),
            @srcSchema NVARCHAR(255),
            @srcObject NVARCHAR(255);

    --SET @baseTable = 'SEODS.[tru].[Employee]';

    SELECT @Schema06Version = ParamValue
    FROM flw.SysCFG
    WHERE (ParamName = N'Schema06Version');

    SELECT @VersionEndCol = ParamValue
    FROM flw.SysCFG
    WHERE (ParamName = N'VersionEndCol')
          OR (ParamName = N'VersionStartCol');

    SELECT @VersionStartCol = ParamValue
    FROM flw.SysCFG
    WHERE (ParamName = N'VersionStartCol');

    SELECT @srcDatabase = PARSENAME(@baseTable, 3),
           @srcSchema = PARSENAME(@baseTable, 2),
           @srcObject = PARSENAME(@baseTable, 1);

    SET @VersionCMD
        = N' ALTER TABLE ' + @baseTable
          + N'
ADD
     ValidFrom_DW datetime2 (0) GENERATED ALWAYS AS ROW START HIDDEN  
        constraint DF_' + @srcSchema + @srcObject
          + 'ValidFrom_DW DEFAULT DATEADD(SECOND, -1, SYSUTCDATETIME())
    , ValidTo_DW datetime2 (0)  GENERATED ALWAYS AS ROW END HIDDEN
        constraint DF_' + @srcSchema + @srcObject
          + 'ValidTo_DW DEFAULT ''9999.12.31 23:59:59.99''
    , PERIOD FOR SYSTEM_TIME (ValidFrom_DW, ValidTo_DW);

ALTER TABLE '         + @baseTable + N' SET (SYSTEM_Versioning = ON (HISTORY_TABLE = [' + +@Schema06Version + N'].['
          + @srcObject + N']));';

    SET @VersionOnCMD
        = ' ALTER TABLE ' + @baseTable + N' SET (SYSTEM_Versioning = ON (HISTORY_TABLE = [' + +@Schema06Version
          + N'].[' + @srcObject + N']));';

    SET @VersionOffCMD = ' ALTER TABLE ' + @baseTable + N' SET (SYSTEM_Versioning = OFF);';

    IF (@Mode = 'DS')
    BEGIN
        SELECT @VersionCMD AS VersionCMD,
               @VersionOnCMD AS VersionOnCMD,
               @VersionOffCMD AS VersionOffCMD;
    END;

END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedure creates script for the creation of Versioning table.
  -- Summary			:	This stored procedure generates dynamic SQL statements to enable system versioning on a specified table in SQL Server. 
							It creates columns ValidFrom_DW, ValidTo_DW and applies period to the SYSTEM_TIME, with HISTORY_TABLE defined in the Schema06Version.

							The stored procedure takes in the base table name and generates SQL statements to enable system versioning. 
							It uses the @Mode parameter to determine if it should return the generated SQL statements or execute them immediately. 
							It also outputs the generated SQL statements for enabling and disabling system versioning.', 'SCHEMA', N'flw', 'PROCEDURE', N'GetVersioningScript', NULL, NULL
GO
