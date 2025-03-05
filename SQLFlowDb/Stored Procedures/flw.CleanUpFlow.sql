SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO



/*
  ##################################################################################################################################################
  -- Name				:   [flw].[CleanUpFlow]
  -- Date				:   2020.11.06
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedures responsible for cleaning up data and metadata associated with a specific FlowID.
  -- Summary			:	The procedure starts by determining the flow type ('ing', 'XLS', 'CSV', or 'XML') and then proceeds to clean up the corresponding objects in the target and vault databases.
							This includes dropping tables, turning off versioning, and deleting log stats. If the flow type is a pre-ingestion type (such as 'XLS', 'CSV', or 'XML'), 
							the procedure will also drop pre-ingestion tables and views, and delete the associated log entries and stats.

							The stored procedure supports an optional @Exec parameter, which, when set to 1, will automatically execute the generated cleanup statements.
							If @Exec is set to 0, the procedure will only print the cleanup statements without executing them.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2020.11.06		Initial
  ##################################################################################################################################################
*/

CREATE procedure [flw].[CleanUpFlow]
    @FlowID int,
    @Exec bit = 0
as
begin
    DECLARE @FlowType NVARCHAR(25) = N'ing';
    SET @FlowType =
    (
        SELECT [flw].[GetFlowType](@FlowID)
    );

    --Ingestion Cleanup
    IF (@FlowType = 'ing')
    BEGIN
        --FETCH Target DATABASE Name
        DECLARE @trgDbName NVARCHAR(255) = [flw].[GetDefaultTrgDB] ();

        --FETCH Vault DATABASE Name
        DECLARE @vltDbName NVARCHAR(255) = [flw].[GetCFGParamVal]('VaultDB');

        --FETCH Schema Name Schema01Raw	
        DECLARE @raw NVARCHAR(255) = [flw].[GetCFGParamVal]('Schema01Raw');

        --FETCH Schema Name Schema04Trusted	
        DECLARE @tru NVARCHAR(255) = [flw].[GetCFGParamVal]('Schema04Trusted');

        --FETCH Schema Name Schema06Version	
        DECLARE @ver NVARCHAR(255) = [flw].[GetCFGParamVal]('Schema06Version');

        --FETCH Schema Name Schema02Temp	
        DECLARE @tmp NVARCHAR(255) = [flw].[GetCFGParamVal]('Schema02Temp');

        --FETCH Schema Name Schema03Vault	
        DECLARE @vlt NVARCHAR(255) = [flw].[GetCFGParamVal]('Schema03Vault');

        --FETCH Object Name
        DECLARE @ObjectName NVARCHAR(255);
        SET @ObjectName =
        (
            SELECT PARSENAME(trgDBSchTbl, 1)
            FROM flw.Ingestion
            WHERE FlowID = @FlowID
        );

        -- Check Target Versioning Status
        DECLARE @trgVer BIT;
        SET @trgVer =
        (
            SELECT [trgVersioning] FROM flw.Ingestion WHERE FlowID = @FlowID
        );

        -- Check Token Versioning Status
        DECLARE @tknVer BIT;
        SET @tknVer =
        (
            SELECT [TokenVersioning] FROM flw.Ingestion WHERE FlowID = @FlowID
        );

        DECLARE @SQL NVARCHAR(MAX);

        -- Set target table Versioning off if status is on
        IF (@trgVer = 1)
        BEGIN
            SET @SQL
                = N' ALTER TABLE [' + @trgDbName + N'].[' + @tru + N'].[' + @ObjectName
                  + N'] SET (SYSTEM_Versioning = OFF);' + CHAR(13) + CHAR(10) + N'IF OBJECT_ID(''[' + @trgDbName
                  + N'].[' + @ver + N'].[' + @ObjectName + N']'', ''U'') IS NOT NULL' + CHAR(13) + CHAR(10)
                  + N'DROP TABLE ''[' + @trgDbName + N'].[' + @ver + N'].[' + @ObjectName + N'];';
            --EXECUTE sys.sp_executesql @SQL;
            PRINT @SQL;
            if (@Exec = 1)
            begin
                exec (@SQL);
            end;

        end;

        -- Set vault table Versioning off if status is on
        IF (@tknVer = 1)
        BEGIN
            SET @SQL
                = N' ALTER TABLE [' + @vltDbName + N'].[' + @vlt + N'].[' + @ObjectName
                  + N'] SET (SYSTEM_Versioning = OFF);' + CHAR(13) + CHAR(10) + N'IF OBJECT_ID(''[' + @vltDbName
                  + N'].[' + @ver + N'].[' + @ObjectName + N']'', ''U'') IS NOT NULL' + CHAR(13) + CHAR(10)
                  + N'DROP TABLE [' + @vltDbName + N'].[' + @ver + N'].[' + @ObjectName + N'];';
            --EXECUTE sys.sp_executesql @SQL;
            PRINT @SQL;
            if (@Exec = 1)
            begin
                exec (@SQL);
            end;
        end;

        -- DROP TABLES

        SET @SQL
            = N'IF OBJECT_ID(''[' + @trgDbName + N'].[' + @raw + N'].[' + @ObjectName + N']'', ''U'') IS NOT NULL'
              + CHAR(13) + CHAR(10) + N'DROP TABLE [' + @trgDbName + N'].[' + @raw + N'].[' + @ObjectName + N'];'
              + CHAR(13) + CHAR(10) + N'IF OBJECT_ID(''[' + @trgDbName + N'].[' + @tru + N'].[' + @ObjectName
              + N']'', ''U'') IS NOT NULL' + CHAR(13) + CHAR(10) + N'DROP TABLE [' + @trgDbName + N'].[' + @tru
              + N'].[' + @ObjectName + N'];' + CHAR(13) + CHAR(10) + N'IF OBJECT_ID(''[' + @vltDbName + N'].[' + @tmp
              + N'].[' + @ObjectName + N']'', ''U'') IS NOT NULL' + CHAR(13) + CHAR(10) + N'DROP TABLE [' + @vltDbName
              + N'].[' + @tmp + N'].[' + @ObjectName + N'];' + CHAR(13) + CHAR(10) + N'IF OBJECT_ID(''[' + @vltDbName
              + N'].[' + @vlt + N'].[' + @ObjectName + N']'', ''U'') IS NOT NULL' + CHAR(13) + CHAR(10)
              + N'DROP TABLE [' + @vltDbName + N'].[' + @vlt + N'].[' + @ObjectName + N'];';

        --EXECUTE sys.sp_executesql @SQL;
        PRINT @SQL;
        IF (@Exec = 1)
        BEGIN
            EXEC (@SQL);
        END;

        -- DELETE From [flw].[SysStats]

        SET @SQL = N'DELETE FROM [flw].[SysStats] WHERE FlowID =' + CAST(@FlowID AS NVARCHAR(255)) + N';';

        PRINT @SQL;
        if (@Exec = 1)
        begin
            exec (@SQL);
        end;
    end;

    --Preingestion Cleanup

    DECLARE @PreDbName NVARCHAR(255) = [flw].[GetDefaultPreDB] ();

    --FETCH Schema Name Schema07Pre	
    DECLARE @Schema07Pre NVARCHAR(255) = [flw].[GetCFGParamVal]('Schema07Pre');

    --XLS
    --Fetch PreIngestion Database
    IF @FlowType = 'XLS'
    BEGIN

        --FETCH Object Name
        DECLARE @ObjectNameXLS NVARCHAR(255);
        SET @ObjectNameXLS =
        (
            SELECT PARSENAME(trgDBSchTbl, 1)
            FROM [flw].[PreIngestionXLS]
            WHERE FlowID = @FlowID
        );

        --CleanUp

        --Drop Pre Table
        DECLARE @SQLPre NVARCHAR(MAX);
        SET @SQLPre
            = N'IF OBJECT_ID(''[' + @PreDbName + N'].[' + @Schema07Pre + N'].[' + @ObjectNameXLS
              + N']'', ''U'') IS NOT NULL' + CHAR(13) + CHAR(10) + N'DROP TABLE [' + @PreDbName + N'].[' + @Schema07Pre
              + N'].[' + @ObjectNameXLS + N'];';
        PRINT @SQLPre;

        IF @Exec = 1
        BEGIN
            EXEC (@SQLPre);
        END;

        --Drop View
        SET @SQL
            = N'USE [' + @PreDbName + N'] IF OBJECT_ID(''[' + @Schema07Pre + N'].[v_' + @ObjectNameXLS
              + N']'', ''U'') IS NOT NULL' + CHAR(13) + CHAR(10) + N'DROP VIEW [' + @Schema07Pre + N'].[v_'
              + @ObjectNameXLS + N'];';
        PRINT @SQL;

        IF @Exec = 1
        BEGIN
            EXEC (@SQL);
        END;

        --Delete from [flw].[SysLog]
        SET @SQL
            = N'DELETE FROM [flw].[SysLog]' + CHAR(13) + CHAR(10) + N'WHERE [FlowID] = '
              + CAST(@FlowID AS NVARCHAR(255)) + N' AND [FlowType] = ''' + @FlowType + N''';';

        PRINT @SQL;

        IF @Exec = 1
        BEGIN
            EXEC (@SQL);
        END;

        -- DELETE From [flw].[SysStats]

        SET @SQL
            = N'DELETE FROM [flw].[SysStats]' + CHAR(13) + CHAR(10) + N'WHERE [FlowID] = '
              + CAST(@FlowID AS NVARCHAR(255)) + N' AND [FlowType] = ''' + @FlowType + N''';';

        PRINT @SQL;
        if (@Exec = 1)
        begin
            exec (@SQL);
        end;

    --Delete From PreIngestionTransfrom
    --SET @SQL
    --    = N'DELETE FROM [flw].[PreIngestionTransfrom]' + CHAR(13) + CHAR(10) + N'WHERE [FlowID] = '
    --      + CAST(@FlowID AS NVARCHAR(255)) + N' AND [FlowType] = ''' + @FlowType + N''';';

    --PRINT @SQL;

    --IF @Exec = 1
    --BEGIN
    --    EXEC (@SQL);
    --END;
    end;
    --CSV
    --Fetch PreIngestion Database
    IF @FlowType = 'CSV'
    BEGIN
        --FETCH Object Name
        DECLARE @ObjectNameCSV NVARCHAR(255);
        SET @ObjectNameCSV =
        (
            SELECT PARSENAME(trgDBSchTbl, 1)
            FROM [flw].[PreIngestionCSV]
            WHERE FlowID = @FlowID
        );

        --CleanUp

        --Drop Pre Table
        SET @SQLPre
            = N'IF OBJECT_ID(''[' + @PreDbName + N'].[' + @Schema07Pre + N'].[' + @ObjectNameCSV
              + N']'', ''U'') IS NOT NULL' + CHAR(13) + CHAR(10) + N'DROP TABLE [' + @PreDbName + N'].[' + @Schema07Pre
              + N'].[' + @ObjectNameCSV + N'];';
        PRINT @SQLPre;

        IF @Exec = 1
        BEGIN
            EXEC (@SQLPre);
        END;

        --Drop View
        SET @SQL
            = N'USE [' + @PreDbName + N'];' + CHAR(13) + CHAR(10) + N'IF OBJECT_ID(''[' + @Schema07Pre + N'].[v_'
              + @ObjectNameCSV + N']'', ''U'') IS NOT NULL' + CHAR(13) + CHAR(10) + N'DROP VIEW [' + @Schema07Pre
              + N'].[v_' + @ObjectNameCSV + N'];';
        PRINT @SQL;

        IF @Exec = 1
        BEGIN
            EXEC (@SQL);
        END;

        --Delete from [flw].[SysLog]
        SET @SQL
            = N'DELETE FROM [flw].[SysLog]' + CHAR(13) + CHAR(10) + N'WHERE [FlowID] = '
              + CAST(@FlowID AS NVARCHAR(255)) + N' AND [FlowType] = ''' + @FlowType + N''';';

        PRINT @SQL;

        IF @Exec = 1
        BEGIN
            EXEC (@SQL);
        END;

        -- DELETE From [flw].[SysStats]

        SET @SQL
            = N'DELETE FROM [flw].[SysStats]' + CHAR(13) + CHAR(10) + N'WHERE [FlowID] = '
              + CAST(@FlowID AS NVARCHAR(255)) + N' AND [FlowType] = ''' + @FlowType + N''';';

        PRINT @SQL;
        if (@Exec = 1)
        begin
            exec (@SQL);
        end;

    --Delete From PreIngestionTransfrom
    --SET @SQL
    --    = N'DELETE FROM [flw].[PreIngestionTransfrom]' + CHAR(13) + CHAR(10) + N'WHERE [FlowID] = '
    --      + CAST(@FlowID AS NVARCHAR(255)) + N' AND [FlowType] = ''' + @FlowType + N''';';

    --PRINT @SQL;

    --IF @Exec = 1
    --BEGIN
    --    EXEC (@SQL);
    --END;
    end;

    IF @FlowType = 'XML'
    BEGIN
        --FETCH Object Name
        DECLARE @ObjectNameXML NVARCHAR(255);
        SET @ObjectNameXML =
        (
            SELECT PARSENAME(trgDBSchTbl, 1)
            FROM [flw].[PreIngestionXML]
            WHERE FlowID = @FlowID
        );

        --CleanUp

        --Drop Pre Table
        SET @SQLPre
            = N'IF OBJECT_ID(''[' + @PreDbName + N'].[' + @Schema07Pre + N'].[' + @ObjectNameXML
              + N']'', ''U'') IS NOT NULL' + CHAR(13) + CHAR(10) + N'DROP TABLE [' + @PreDbName + N'].[' + @Schema07Pre
              + N'].[' + @ObjectNameXML + N'];';
        PRINT @SQLPre;

        IF @Exec = 1
        BEGIN
            EXEC (@SQLPre);
        END;

        --Drop View
        SET @SQL
            = N'USE [' + @PreDbName + N'] IF OBJECT_ID(''[' + @Schema07Pre + N'].[v_' + @ObjectNameXML
              + N']'', ''U'') IS NOT NULL' + CHAR(13) + CHAR(10) + N'DROP VIEW [' + @Schema07Pre + N'].[v_'
              + @ObjectNameXML + N'];';
        PRINT @SQL;

        IF @Exec = 1
        BEGIN
            EXEC (@SQL);
        END;

        --Delete from [flw].[SysLog]
        SET @SQL
            = N'DELETE FROM [flw].[SysLog]' + CHAR(13) + CHAR(10) + N'WHERE [FlowID] = '
              + CAST(@FlowID AS NVARCHAR(255)) + N' AND [FlowType] = ''' + @FlowType + N''';';

        PRINT @SQL;

        IF @Exec = 1
        BEGIN
            EXEC (@SQL);
        END;

        -- DELETE From [flw].[SysStats]

        SET @SQL
            = N'DELETE FROM [flw].[SysStats]' + CHAR(13) + CHAR(10) + N'WHERE [FlowID] = '
              + CAST(@FlowID AS NVARCHAR(255)) + N' AND [FlowType] = ''' + @FlowType + N''';';

        PRINT @SQL;
        if (@Exec = 1)
        begin
            exec (@SQL);
        end;

    --Delete From PreIngestionTransfrom
    --SET @SQL
    --    = N'DELETE FROM [flw].[PreIngestionTransfrom]' + CHAR(13) + CHAR(10) + N'WHERE [FlowID] = '
    --      + CAST(@FlowID AS NVARCHAR(255)) + N' AND [FlowType] = ''' + @FlowType + N''';';

    --PRINT @SQL;

    --IF @Exec = 1
    --BEGIN
    --    EXEC (@SQL);
    --END;
    end;

    IF @FlowType = 'jsn'
    BEGIN
        --FETCH Object Name
        DECLARE @ObjectNamejsn NVARCHAR(255);
        SET @ObjectNamejsn =
        (
            SELECT PARSENAME(trgDBSchTbl, 1)
            FROM [flw].[PreIngestionjsn]
            WHERE FlowID = @FlowID
        );

        --CleanUp

        --Drop Pre Table
        SET @SQLPre
            = N'IF OBJECT_ID(''[' + @PreDbName + N'].[' + @Schema07Pre + N'].[' + @ObjectNamejsn
              + N']'', ''U'') IS NOT NULL' + CHAR(13) + CHAR(10) + N'DROP TABLE [' + @PreDbName + N'].[' + @Schema07Pre
              + N'].[' + @ObjectNamejsn + N'];';
        PRINT @SQLPre;

        IF @Exec = 1
        BEGIN
            EXEC (@SQLPre);
        END;

        --Drop View
        SET @SQL
            = N'USE [' + @PreDbName + N'] IF OBJECT_ID(''[' + @Schema07Pre + N'].[v_' + @ObjectNamejsn
              + N']'', ''U'') IS NOT NULL' + CHAR(13) + CHAR(10) + N'DROP VIEW [' + @Schema07Pre + N'].[v_'
              + @ObjectNamejsn + N'];';
        PRINT @SQL;

        IF @Exec = 1
        BEGIN
            EXEC (@SQL);
        END;

        --Delete from [flw].[SysLog]
        SET @SQL
            = N'DELETE FROM [flw].[SysLog]' + CHAR(13) + CHAR(10) + N'WHERE [FlowID] = '
              + CAST(@FlowID AS NVARCHAR(255)) + N' AND [FlowType] = ''' + @FlowType + N''';';

        PRINT @SQL;

        IF @Exec = 1
        BEGIN
            EXEC (@SQL);
        END;

        -- DELETE From [flw].[SysStats]

        SET @SQL
            = N'DELETE FROM [flw].[SysStats]' + CHAR(13) + CHAR(10) + N'WHERE [FlowID] = '
              + CAST(@FlowID AS NVARCHAR(255)) + N' AND [FlowType] = ''' + @FlowType + N''';';

        PRINT @SQL;
        if (@Exec = 1)
        begin
            exec (@SQL);
        end;

    --Delete From PreIngestionTransfrom
    --SET @SQL
    --    = N'DELETE FROM [flw].[PreIngestionTransfrom]' + CHAR(13) + CHAR(10) + N'WHERE [FlowID] = '
    --      + CAST(@FlowID AS NVARCHAR(255)) + N' AND [FlowType] = ''' + @FlowType + N''';';

    --PRINT @SQL;

    --IF @Exec = 1
    --BEGIN
    --    EXEC (@SQL);
    --END;
    end;

    if @FlowType = 'PRC'
    begin
        --FETCH Object Name
        DECLARE @ObjectNamePRC NVARCHAR(255);
        SET @ObjectNamePRC =
        (
            SELECT PARSENAME(trgDBSchTbl, 1)
            FROM [flw].[PreIngestionPRC]
            WHERE FlowID = @FlowID
        );

        --CleanUp

        --Drop Pre Table
        SET @SQLPre
            = N'IF OBJECT_ID(''[' + @PreDbName + N'].[' + @Schema07Pre + N'].[' + @ObjectNamePRC
              + N']'', ''U'') IS NOT NULL' + CHAR(13) + CHAR(10) + N'DROP TABLE [' + @PreDbName + N'].[' + @Schema07Pre
              + N'].[' + @ObjectNamePRC + N'];';
        PRINT @SQLPre;

        IF @Exec = 1
        BEGIN
            EXEC (@SQLPre);
        END;

        --Drop View
        SET @SQL
            = N'USE [' + @PreDbName + N'] IF OBJECT_ID(''[' + @Schema07Pre + N'].[v_' + @ObjectNamePRC
              + N']'', ''U'') IS NOT NULL' + CHAR(13) + CHAR(10) + N'DROP VIEW [' + @Schema07Pre + N'].[v_'
              + @ObjectNamePRC + N'];';
        PRINT @SQL;

        if @Exec = 1
        begin
            exec (@SQL);
        end;

        --Delete from [flw].[SysLog]
        set @SQL
            = N'DELETE FROM [flw].[SysLog]' + char(13) + char(10) + N'WHERE [FlowID] = '
              + cast(@FlowID as nvarchar(255)) + N' AND [FlowType] = ''' + @FlowType + N''';';

        print @SQL;

        if @Exec = 1
        begin
            exec (@SQL);
        end;

        -- DELETE From [flw].[SysStats]

        set @SQL
            = N'DELETE FROM [flw].[SysStats]' + char(13) + char(10) + N'WHERE [FlowID] = '
              + cast(@FlowID as nvarchar(255)) + N' AND [FlowType] = ''' + @FlowType + N''';';

        print @SQL;
        if (@Exec = 1)
        begin
            exec (@SQL);
        end;

    --Delete From PreIngestionTransfrom
    --SET @SQL
    --    = N'DELETE FROM [flw].[PreIngestionTransfrom]' + CHAR(13) + CHAR(10) + N'WHERE [FlowID] = '
    --      + CAST(@FlowID AS NVARCHAR(255)) + N' AND [FlowType] = ''' + @FlowType + N''';';

    --PRINT @SQL;

    --IF @Exec = 1
    --BEGIN
    --    EXEC (@SQL);
    --END;
    end;

    --SyncSysLog
    if @FlowType in ( 'XLS', 'CSV', 'XML', 'PRQ', 'JSN', 'PRC')
    begin
        exec [flw].[SyncSysLog];
        print char(13) + char(10) + '****SysLog Synced***' + char(13) + char(10) + 'EXEC [flw].[SyncSysLog]';
    end;

end;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedures responsible for cleaning up data and metadata associated with a specific FlowID.
  -- Summary			:	The procedure starts by determining the flow type (''ing'', ''XLS'', ''CSV'', or ''XML'') and then proceeds to clean up the corresponding objects in the target and vault databases.
							This includes dropping tables, turning off versioning, and deleting log stats. If the flow type is a pre-ingestion type (such as ''XLS'', ''CSV'', or ''XML''), 
							the procedure will also drop pre-ingestion tables and views, and delete the associated log entries and stats.

							The stored procedure supports an optional @Exec parameter, which, when set to 1, will automatically execute the generated cleanup statements.
							If @Exec is set to 0, the procedure will only print the cleanup statements without executing them.', 'SCHEMA', N'flw', 'PROCEDURE', N'CleanUpFlow', NULL, NULL
GO
