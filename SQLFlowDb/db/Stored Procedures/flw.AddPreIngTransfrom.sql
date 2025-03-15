SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[AddPreIngTransfrom]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure is responsible for adding pre-ingestion transformations, creating target tables, and updating the associated views in the pre-ingestion process.
  -- Summary			:	It accepts the following input parameters:

							@FlowID
							@FlowType
							@ColList
							It retrieves the DefaultColDataType and trgDBSchTbl from the flw.PreFiles table for the given FlowID.

							It checks if there's already a record with a column name containing 'FileDate_DW' for the given FlowID in the [flw].[PreIngestionTransfrom] table.

							If there's at least one record, it sets the @indexCMD variable to create non-clustered indexes on the target table for FileDate_DW and FileName_DW columns.

							It builds an ALTER TABLE command (@alterCmd) to add new columns to the target table for the given FlowID and FlowType, if they don't already exist.

							It inserts new rows into the [flw].[PreIngestionTransfrom] table for the given FlowID and FlowType, if they don't already exist.

							It creates a target table script (@cmdCreate) with the columns specified in the @ColList parameter and the non-clustered indexes specified in the @indexCMD variable.

							It retrieves the ViewCMD, ViewSelect, and ViewName from the [flw].[GetPreViewCmd] function for the given FlowID.

							It returns the generated commands and lists as a result set, including alterCmd, cmdCreate, tfColList, viewCMD, and viewSelect.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/

CREATE PROCEDURE [flw].[AddPreIngTransfrom]
    @FlowID INT,
    @FlowType NVARCHAR(25),
    @ColList NVARCHAR(MAX),
    @DataTypeList NVARCHAR(MAX) = '',
    @ExpList NVARCHAR(MAX) = ''
AS
BEGIN

    --DELETE FROM [flw].[PreIngestionTransfrom] WHERE FlowID = 538;

    --DECLARE @ColList            NVARCHAR(4000) = N'',
    --           @FlowID             INT            = 538,
    --           @FlowType           NVARCHAR(255)  = N'csv'
    --SET @ColList = N'[BikeId],[VisualId],[Serial],[DockingPointId],[DockingStationId],[Address_Street],[Address_District],[Address_ZipCode],[Address_City],[Address_Country],[ChargeLevel],[BikeState],[LockState],[PowerState],[Position_Latitude],[Position_Longitude],[Position_Altitude],[Position_Quality],[SessionId],[EndUserId],[DockingStationName],[Acknowledged],[LatestPulse],[TripCount],[AccumulateTotalDistance],[Disabled],[DisabledReason],[BikeStatusId],[NearDockingStation],[BikeModeId],[dl_date_cettime],[FileDate_DW],[FileName_DW],[FileRowDate_DW],[FileSize_DW]';

    DECLARE @DefaultColDataType NVARCHAR(255) = N'',
            @trgDBSchTbl NVARCHAR(255) = N'',
            @alterCmd NVARCHAR(MAX) = N'';

    SELECT @DefaultColDataType = DefaultColDataType,
           @trgDBSchTbl = trgDBSchTbl
    FROM flw.PreFiles
    WHERE FlowID = @FlowID;


    DECLARE @dupeFileDate INT = 0;
    DECLARE @indexCMD NVARCHAR(4000) = N'';


    SELECT @dupeFileDate = COUNT(*)
    FROM [flw].[PreIngestionTransfrom] trg
    WHERE trg.FlowID = @FlowID
          AND [ColName] LIKE '%FileDate_DW%';

    IF (@dupeFileDate > 0)
    BEGIN
        SET @indexCMD
            = N'
		;CREATE INDEX NCI_FileDate_DW ON ' + @trgDBSchTbl + N'([FileDate_DW]) INCLUDE([FileSize_DW]) ' + CHAR(10)
              + CHAR(13) + N';CREATE INDEX NCI_FileName_DW ON ' + @trgDBSchTbl
              + N'([FileName_DW]) INCLUDE([FileSize_DW]) ';
    END

    --PRINT @indexCMD
    --build alter cmd

    ;
    WITH base
    AS (SELECT @FlowID AS FlowID,
               @FlowType AS FlowType,
               RTRIM(LTRIM(Item)) AS ColName,
               (
                   SELECT TOP 1
                          Item
                   FROM [flw].[StringSplit](@DataTypeList, ';') sub
                   WHERE sub.Ordinal = base.Ordinal
               ) AS DataType,
               (
                   SELECT TOP 1
                          Item
                   FROM [flw].[StringSplit](@ExpList, ';') sub
                   WHERE sub.Ordinal = base.Ordinal
               ) AS SelectExp
        FROM [flw].[StringSplit](@ColList, ',') base )
    SELECT @alterCmd
        = @alterCmd + N' ALTER TABLE ' + @trgDBSchTbl + N' ADD [' + [flw].[RemBrackets](src.ColName) + N'] '
          + CASE
                WHEN LEN(src.DataType) > 0 THEN
                    src.DataType
                ELSE
                    @DefaultColDataType
            END + N' NULL;' + CHAR(10) + CHAR(13)
    FROM base src
        LEFT OUTER JOIN [flw].[PreIngestionTransfrom] trg
            ON src.FlowID = trg.FlowID
               AND src.FlowType = trg.FlowType
               AND [flw].[RemBrackets](src.ColName) = [flw].[RemBrackets](trg.ColName)
    WHERE trg.FlowID IS NULL;

    -- Add new rows
    ;
    WITH base
    AS (SELECT @FlowID AS FlowID,
               @FlowType AS FlowType,
               RTRIM(LTRIM(Item)) AS ColName,
               (
                   SELECT TOP 1
                          Item
                   FROM [flw].[StringSplit](@DataTypeList, ';') sub
                   WHERE sub.Ordinal = base.Ordinal
               ) AS DataType,
               (
                   SELECT TOP 1
                          Item
                   FROM [flw].[StringSplit](@ExpList, ';') sub
                   WHERE sub.Ordinal = base.Ordinal
               ) AS SelectExp
        FROM [flw].[StringSplit](@ColList, ',') base )
    INSERT INTO [flw].[PreIngestionTransfrom]
    (
        FlowID,
        FlowType,
        ColName,
        [SelectExp],
        DataType
    )
    SELECT src.FlowID,
           src.FlowType,
           src.ColName,
           src.SelectExp,
           src.DataType
    FROM base src
        LEFT OUTER JOIN [flw].[PreIngestionTransfrom] trg
            ON src.FlowID = trg.FlowID
               AND src.FlowType = trg.FlowType
               AND [flw].[RemBrackets](src.ColName) = [flw].[RemBrackets](trg.ColName)
    WHERE trg.FlowID IS NULL;


    --Create Target Table Script
    DECLARE @List NVARCHAR(MAX) = N'',
            @tfColList NVARCHAR(MAX) = N'',
            @cmdCreate NVARCHAR(MAX) = N'';


    SELECT @List
        = @List + N',[' + RTRIM(LTRIM([flw].[RemBrackets]([ColName]))) + N'] '
          + CASE
                WHEN LEN(ISNULL(pit.DataType, '')) > 1 --AND pit.DataType NOT IN ( 'NVARCHAR(MAX)' )
                      THEN
                    pit.DataType
                WHEN sc.[ColumnName] IS NOT NULL THEN
                    'varchar(255)'
                ELSE
                    @DefaultColDataType
            END + N' NULL',
           @tfColList = @tfColList + +N',[' + RTRIM(LTRIM([flw].[RemBrackets]([ColName]))) + N']'
    FROM [flw].[PreIngestionTransfrom] pit
        LEFT OUTER JOIN [flw].[SysColumn] sc
            ON [flw].[RemBrackets](pit.ColName) = [flw].[RemBrackets](sc.[ColumnName])
    WHERE [FlowID] = @FlowID
	AND ISNULL(Virtual,0) = 0
    ORDER BY [SortOrder],
             [TransfromID];

    SET @List = SUBSTRING(@List, 2, LEN(@List));
    SET @tfColList = SUBSTRING(@tfColList, 2, LEN(@tfColList));

    SET @cmdCreate
        = IIF(LEN(@List) = 0,
              '',
              'IF OBJECT_ID(''' + @trgDBSchTbl + ''', ''U'') IS NOT NULL BEGIN DROP TABLE ' + @trgDBSchTbl + ' END; '
              + 'CREATE TABLE ' + @trgDBSchTbl + '(' + @List + ') ' + @indexCMD + CHAR(10) + CHAR(13));


    DECLARE @ViewName NVARCHAR(255) = N'',
            @ViewNameFull NVARCHAR(255) = N'',
            @ViewCMD NVARCHAR(MAX) = N'',
            @ViewColumnList NVARCHAR(MAX) = N'',
            @ViewSelect NVARCHAR(MAX) = N'';


    SELECT @ViewCMD = ViewCMD,
           @ViewSelect = ViewSelect,
           @ViewName = ViewName,
           @ViewNameFull = @ViewNameFull
    FROM [flw].[GetPreViewCmd](@FlowID);


    SELECT ISNULL(@alterCmd, '') AS alterCmd,
           ISNULL(@cmdCreate, '') AS cmdCreate,
           @tfColList AS tfColList,
           @ViewCMD AS viewCMD,
           @ViewSelect AS viewSelect;

END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedure is responsible for adding pre-ingestion transformations, creating target tables, and updating the associated views in the pre-ingestion process.
  -- Summary			:	It accepts the following input parameters:

							@FlowID
							@FlowType
							@ColList
							It retrieves the DefaultColDataType and trgDBSchTbl from the flw.PreFiles table for the given FlowID.

							It checks if there''s already a record with a column name containing ''FileDate_DW'' for the given FlowID in the [flw].[PreIngestionTransfrom] table.

							If there''s at least one record, it sets the @indexCMD variable to create non-clustered indexes on the target table for FileDate_DW and FileName_DW columns.

							It builds an ALTER TABLE command (@alterCmd) to add new columns to the target table for the given FlowID and FlowType, if they don''t already exist.

							It inserts new rows into the [flw].[PreIngestionTransfrom] table for the given FlowID and FlowType, if they don''t already exist.

							It creates a target table script (@cmdCreate) with the columns specified in the @ColList parameter and the non-clustered indexes specified in the @indexCMD variable.

							It retrieves the ViewCMD, ViewSelect, and ViewName from the [flw].[GetPreViewCmd] function for the given FlowID.

							It returns the generated commands and lists as a result set, including alterCmd, cmdCreate, tfColList, viewCMD, and viewSelect.', 'SCHEMA', N'flw', 'PROCEDURE', N'AddPreIngTransfrom', NULL, NULL
GO
