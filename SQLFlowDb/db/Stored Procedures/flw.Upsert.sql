SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO




/*
  ##################################################################################################################################################
  -- Name				:   [flw].[Upsert]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   Merges a source dataset with the target. Not in use as the SQLFlow core engine genereates this now
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/


CREATE PROCEDURE [flw].[Upsert]
    -- Add the parameters for the stored procedure here
    @FlowID INT,
    @ToDatabase NVARCHAR(250), -- Defaults to current database
    @FromDatabase NVARCHAR(250), -- Source Database name
    @FromObject NVARCHAR(255), -- Source object name
    @ToObject NVARCHAR(255), -- Target ojbect name
    @upsJoinColumns NVARCHAR(2000),
    @HashKeyColumns NVARCHAR(2000) = '',
    @upsSkipUpdate INT = 0,
    @upsSkipInsert INT = 0,
    @upsSkipDelete INT = 1,
    @upsDeleteNonExisting INT = '',
    @upsExcludeUpdateColumns NVARCHAR(2000) = '',
    @ContinueOnError BIT = 0,
    @useTmpTblForinsert BIT = 0,
    @dbg INT = 0
AS
BEGIN

    SET NOCOUNT ON;

    DECLARE @curObjName NVARCHAR(255),
            @curExecCmd NVARCHAR(4000),
            @curSection NVARCHAR(4000),
            @curCode    NVARCHAR(MAX);

    IF (@dbg >= 1)
    BEGIN
        SET @curObjName = ISNULL(OBJECT_SCHEMA_NAME(@@PROCID) + '.' + OBJECT_NAME(@@PROCID), 'Defualt SP Name');
        SET @curExecCmd
            = N'exec ' + @curObjName + N' @FlowID=' + CAST(@FlowID AS NVARCHAR(255)) + N', @ToDatabase='''
              + @ToDatabase + N''', @FromDatabase=''' + @FromDatabase + N''', @FromObject=''' + @FromObject
              + N''', @ToObject=''' + @ToObject + N''', @upsJoinColumns=''' + @upsJoinColumns
              + N''', @HashKeyColumns=''' + @HashKeyColumns + N''', @upsSkipUpdate='
              + CAST(@upsSkipUpdate AS NVARCHAR(255)) + N', @upsSkipInsert=' + CAST(@upsSkipInsert AS NVARCHAR(255))
              + N', @upsSkipDelete=' + CAST(@upsSkipDelete AS NVARCHAR(255)) + N', @upsDeleteNonExisting='
              + CAST(@upsDeleteNonExisting AS NVARCHAR(255)) + N', @upsExcludeUpdateColumns='''
              + @upsExcludeUpdateColumns + N''', @ContinueOnError=' + CAST(@ContinueOnError AS NVARCHAR(255))
              + N', @dbg=' + CAST(@dbg AS NVARCHAR(255));
        PRINT [flw].[GetLogHeader](@curObjName, @curExecCmd);
    END;

    IF (@@NESTLEVEL > 1 AND @dbg = 1)
        SET @dbg = 0;

    /*	
DECLARE @FlowID INT;
DECLARE @dbg INT;
DECLARE @MappingTypeID INT; --1 Use source for column selection, 2 Use target for column selection
SET @FlowID = 3;
SET @dbg = 1;
SET @MappingTypeID = 1;
*/
    SET NOCOUNT ON;

    IF (OBJECT_ID('tempdb..#LogDynSQL') IS NOT NULL)
    BEGIN
        DROP TABLE #LogDynSQL;
    END;
    CREATE TABLE #LogDynSQL ([FlowID] INT NULL,
                             [Inserted] INT NULL,
                             [Updated] INT NULL,
                             [Deleted] INT NULL,
                             [InsertCmd] NVARCHAR(MAX),
                             [UpdateCmd] NVARCHAR(MAX),
                             [DeleteCmd] NVARCHAR(MAX),
                             ErrorInsert NVARCHAR(MAX),
                             ErrorUpdate NVARCHAR(MAX),
                             ErrorDelete NVARCHAR(MAX));

    INSERT INTO #LogDynSQL ([FlowID])
    VALUES (@FlowID);



    DECLARE @sqlCMDUpdate VARCHAR(MAX),
            @sqlCMDInsert VARCHAR(MAX),
            @sqlCMDDelete VARCHAR(MAX),
            @sqlCMDCur    VARCHAR(MAX),
            @srcFilter    NVARCHAR(2000) = N'';


    DECLARE @InnerJoin      NVARCHAR(2000),
            @OuterJoin      NVARCHAR(2000),
            @OuterJoinDel   NVARCHAR(2000),
            @Join           NVARCHAR(2000),
            @ExTypeCheckSum NVARCHAR(500),
            @tmpIncrTbl     NVARCHAR(200);

    DECLARE @ln           VARCHAR(2),
            @EndTime      DATETIME,
            @StartTime    DATETIME,
            @Duration     VARCHAR(255),
            @DurationTot  INT           = 0,
            @Process      NVARCHAR(2000),
            @rowCount     VARCHAR(255),
            @err          INT,
            @err_msg      NVARCHAR(2000),
            @RowsAffected VARCHAR(1000),
            @StopCode     INT;
    SET @ln = CHAR(13) + CHAR(10);

    --Fetch Xtype values that are invalid for checksum
    SELECT @ExTypeCheckSum = [flw].[GetCFGParamVal]('ExTypeCheckSum');

    IF @dbg > 1
    BEGIN
        SET @curSection = N'10 :: ' + @curObjName + N' :: Fetch Xtype values that are invalid for checksum';
        SET @curCode = @ExTypeCheckSum;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;

    --Set Key Param Values
    SET @ToDatabase = IIF(LEN(@ToDatabase) > 0, @ToDatabase, DB_NAME());
    SET @FromDatabase = IIF(LEN(@FromDatabase) > 0, @FromDatabase, DB_NAME());
    SET @Process = CONCAT(@FromDatabase, '.', @FromObject, '->', @ToDatabase, '.', @ToObject);
    SET @StopCode = IIF(@ContinueOnError = 1, -1, 16);

    DECLARE @NameStripped NVARCHAR(255);
    SET @NameStripped = REPLACE(REPLACE(@ToObject, '[', ''), ']', '');
    SET @tmpIncrTbl = REPLACE(@NameStripped, '.', '.[') + N'_tmp]';

    --Init LogParam
    SET @RowsAffected = '';

    --Build join Expresion
    SET @Join = N'';
    SELECT @Join = @Join + N' AND trg.' + Item + N'=' + N'src.' + Item
      FROM flw.SplitStr2Tbl(REPLACE(@upsJoinColumns, IIF(LEN(@HashKeyColumns) > 0, 'HashKey_DW', ''), ''), ',')
     WHERE LEN(Item) > 0;
    SET @Join = SUBSTRING(@Join, 5, LEN(@Join));

    IF @dbg > 1
    BEGIN
        SET @curSection = N'20 :: ' + @curObjName + N' :: Build join Expresion';
        SET @curCode = @Join;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;

    --Execute preprocess

    IF (@upsSkipUpdate = 0)
    BEGIN
        IF @dbg > 1
        BEGIN
            SET @curSection = N'21 :: ' + @curObjName + N' :: ##### Build UpdateSQL #####';
            PRINT [flw].[GetLogSection](@curSection, '');
        END;

        --Create Key Params for the dynamic SQL
        SET @sqlCMDCur
            = +' DECLARE @err nvarchar(max), @updColSQL varchar(max)='''', @execSQL varchar(max) = '''', @HashKeyCol varchar(max)='''
              + IIF(LEN(@HashKeyColumns) > 0, ',trg.HashKey_DW=', '')
              + ''', @HashKeyColSQL varchar(max)='''', @ChkSumSrcColSQL varchar(max)='''', @RevChkSumSrcColSQL varchar(max)='''', @RevChkSumSrcColSortSQL varchar(max)='''', @NewTblSQL varchar(max)='''' '
              + ' ,@BinChkSumSrcColSQL varchar(max)='''', @BinChkSumTrgColSQL varchar(max) = '''' ' + @ln;

        IF @dbg > 1
        BEGIN
            SET @curSection = N'30 :: ' + @curObjName + N' :: Create Key Params for the dynamic SQ';
            SET @curCode = @sqlCMDCur;
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;

        SET @sqlCMDUpdate = @sqlCMDCur;

        --Check if a Hashkey calculation should be added to the target table
        IF (LEN(@HashKeyColumns) > 0)
        BEGIN
            SET @sqlCMDCur
                = ' Select @ChkSumSrcColSQL = @ChkSumSrcColSQL + '',src.['' + src.name + '']'',  @RevChkSumSrcColSQL = @RevChkSumSrcColSQL + '',Reverse(src.['' + src.name + ''])'' from '
                  + @ln + @FromDatabase + '.sys.syscolumns Src inner join ' + @ToDatabase
                  + '.sys.syscolumns Trg on src.name = trg.name WHERE trg.name IN (select Replace(Replace(item,''['',''''),'']'','''') from flw.SplitStr2Tbl('
                  + CHAR(39) + @HashKeyColumns + CHAR(39) + ','','')) AND ' + @ln + ' src.ID = object_id(' + CHAR(39)
                  + @FromDatabase + '.' + @FromObject + CHAR(39) + ')' + @ln + ' AND trg.ID = object_id(' + CHAR(39)
                  + @ToDatabase + '.' + @ToObject + CHAR(39) + ')' + @ln + ' ORDER BY trg.colorder '
                  + ' SELECT  @RevChkSumSrcColSortSQL = @RevChkSumSrcColSortSQL +'',''+ ltrim(rtrim(Item)) FROM  flw.SplitStr2Tbl(@RevChkSumSrcColSQL, '','') where len(item)>0 Order by RecID desc ' --Resort column order for lesser collision
                  + ' Set @HashKeyColSQL = @HashKeyCol + ''CHECKSUM('' + Substring(@ChkSumSrcColSQL,2,len(@ChkSumSrcColSQL)) + '','' + Substring( @RevChkSumSrcColSortSQL,2,len(@RevChkSumSrcColSortSQL)) + '') '' '
                  + @ln;
            IF @dbg > 1
            BEGIN
                SET @curSection
                    = N'40 :: ' + @curObjName
                      + N' :: Check if a HashKey_DW calculation should be added to the target table';
                SET @curCode = @sqlCMDUpdate;
                PRINT [flw].[GetLogSection](@curSection, @curCode);
            END;
            SET @sqlCMDUpdate = @sqlCMDUpdate + @sqlCMDCur;
        END;

        --Dupe target table, if it does not exsits then create it with insert into. Otherwise sync source and target
        --Note that checksum ensures that only changed rows are updated
        SET @sqlCMDCur
            = ' DECLARE @DupeTbl int ' + @ln + ' SELECT @DupeTbl = count(*) from ' + @ln + @ToDatabase
              + '.sys.objects ' + @ln + ' Where object_id = object_id(' + CHAR(39) + @ToDatabase + '.' + @ToObject
              + CHAR(39) + ')' + ' ' + @ln + 'IF(@DupeTbl = 0 or @DupeTbl is null) ' + @ln + ' BEGIN ' + @ln + @ln
              + ' Set @execSQL = '' SELECT * '' + IIF(len(@HashKeyColSQL)> 0, @HashKeyColSQL + ''as HashKey_DW '','''') +'' INTO '
              + @ToDatabase + '.' + @ToObject + ' FROM ' + @FromDatabase + '.' + @FromObject + ' src' + '''' + @ln
              + IIF(LEN(@srcFilter) > 0, REPLACE(@srcFilter, CHAR(39), CHAR(39) + CHAR(39)) + CHAR(39), '') + @ln
              + IIF(@dbg >= 1, ' Print @execSQL', '') + @ln + 'BEGIN TRY' + @ln + 'exec(@execSQL);' + @ln
              + ' Update #LogDynSQL set InsertCmd=@execSQL, Inserted=@@ROWCOUNT WHERE FlowID='
              + CAST(@FlowID AS NVARCHAR(250)) + @ln + 'END TRY' + @ln + 'BEGIN CATCH' + @ln + @ln
              + 'set @err  = ERROR_MESSAGE();' + @ln
              + ' Update #LogDynSQL set InsertCmd=@execSQL, ErrorInsert=@err WHERE FlowID='
              + CAST(@FlowID AS NVARCHAR(250)) + @ln + 'END CATCH ' + @ln + ' END';
        IF @dbg > 1
        BEGIN
            SET @curSection
                = N'50 :: ' + @curObjName
                  + N' :: Dupe target table, if it does not exsits then create it with insert into. Otherwise sync source and target';
            SET @curCode = @sqlCMDCur;
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;
        SET @sqlCMDUpdate = @sqlCMDUpdate + @sqlCMDCur;

        --Generate a list of columns for the update statement 
        SET @sqlCMDCur
            = ' Select @updColSQL = @updColSQL + '',trg.['' + src.name + '']=src.['' + src.name + '']'' from '
              + @FromDatabase + '.sys.syscolumns Src inner join ' + @ToDatabase + '.sys.syscolumns Trg '
              + ' on src.name = trg.name WHERE src.name NOT IN (select Replace(Replace(item,''['',''''),'']'','''') from flw.SplitStr2Tbl('
              + CHAR(39) + @upsJoinColumns + ',' + @upsExcludeUpdateColumns
              + ',UpdatedDate_DW,DeletedDate_DW,InsertedDate_DW,RowStatus_DW,HashKey_DW' + CHAR(39)
              + ','','')) AND src.ID = object_id(' + CHAR(39) + @FromDatabase + '.' + @FromObject + CHAR(39) + ')'
              + ' AND trg.ID = object_id(' + CHAR(39) + @ToDatabase + '.' + @ToObject + CHAR(39) + ')'
              + ' ORDER BY trg.colorder ' + @ln;

        IF @dbg > 1
        BEGIN
            SET @curSection = N'60 :: ' + @curObjName + N' :: Generate a list of columns for the update statement ';
            SET @curCode = @sqlCMDCur;
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;
        SET @sqlCMDUpdate = @sqlCMDUpdate + @sqlCMDCur;

        --Check for UpdatedDate_DW and RowStatus_DW which are system columns. If found add UpdatedDate_DW and RowStatus_DW to the update logic
        SET @sqlCMDCur
            = ' DECLARE  @LU varchar(255), @RS varchar(255) ' + @ln
              + ' SELECT @LU = Max(IIf(trg.name = ''UpdatedDate_DW'','',trg.['' + trg.name + '']=getdate()'','''')),'
              + @ln
              + '        @RS = Max(IIf(trg.name = ''RowStatus_DW'','',trg.['' + trg.name + '']= '' + char(39) +''U''  + char(39) ,''''))'
              + @ln + ' FROM ' + @ToDatabase + '.sys.syscolumns Trg ' + @ln + ' WHERE trg.ID = object_id(' + CHAR(39)
              + @ToDatabase + '.' + @ToObject + CHAR(39) + ')' + @ln;

        IF @dbg > 1
        BEGIN
            SET @curSection
                = N'70 :: ' + @curObjName
                  + N' :: Check for UpdatedDate_DW and RowStatus_DW which are system columns. If found add UpdatedDate_DW and RowStatus_DW to the update logic ';
            SET @curCode = @sqlCMDCur;
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;
        SET @sqlCMDUpdate = @sqlCMDUpdate + @sqlCMDCur;

        --Calculate binary checksum to ensure that update only affects changed rows. SysCols and upsExcludeUpdateColumns are ommited
        SET @sqlCMDCur
            = ' Select @BinChkSumSrcColSQL = @BinChkSumSrcColSQL + '',src.['' + src.name + '']'',  @BinChkSumTrgColSQL = @BinChkSumTrgColSQL + '',trg.['' + src.name + '']'' from '
              + @ln + @FromDatabase + '.sys.syscolumns Src inner join ' + @ToDatabase
              + '.sys.syscolumns Trg on src.name = trg.name WHERE trg.name NOT IN (select Replace(Replace(item,''['',''''),'']'','''') from flw.SplitStr2Tbl('
              + CHAR(39) + @upsExcludeUpdateColumns
              + ',UpdatedDate_DW,DeletedDate_DW,InsertedDate_DW,RowStatus_DW,FileDate_DW,HashKey_DW' + CHAR(39)
              + ','','')) AND ' + @ln + ' src.ID = object_id(' + CHAR(39) + @FromDatabase + '.' + @FromObject
              + CHAR(39) + ')' + @ln + ' AND trg.ID = object_id(' + CHAR(39) + @ToDatabase + '.' + @ToObject + CHAR(39)
              + ')' + ' AND trg.xType NOT IN (' + @ExTypeCheckSum + ')' + +@ln + ' ORDER BY trg.colorder ' + @ln;

        IF @dbg > 1
        BEGIN
            SET @curSection
                = N'80 :: ' + @curObjName
                  + N' :: Calculate binary checksum to ensure that update only affects changed rows. SysCols and upsExcludeUpdateColumns are ommited ';
            SET @curCode = @sqlCMDCur;
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;
        SET @sqlCMDUpdate = @sqlCMDUpdate + @sqlCMDCur;


        --Build inner join expresion
        SET @InnerJoin
            = N' FROM ' + @FromDatabase + N'.' + @FromObject + N' as Src INNER JOIN ' + @ToDatabase + N'.' + @ToObject
              + N' as Trg ON' + @Join;

        --IF @dbg = 1
        --BEGIN
        --    SET @curSection = N'90 :: ' + @curObjName + N' :: Build inner join expresion ';
        --    SET @curCode = @InnerJoin;
        --    PRINT [flw].[GetLogSection](@curSection, @curCode);
        --END;

        --Construct final dynamic flw. The final statement builds yet another dynamic flw. This ensures the posiblitiy for execution against remote databases
        SET @sqlCMDCur
            = ' set @updColSQL = substring(@updColSQL,2,len(@updColSQL)) + Isnull(@LU,'''') + IsNull(@RS,'''')' + @ln
              + ' set @BinChkSumSrcColSQL =  ''CHECKSUM ('' + substring(@BinChkSumSrcColSQL,2,len(@BinChkSumSrcColSQL)) + '')'' '
              + @ln
              + ' set @BinChkSumTrgColSQL = ''CHECKSUM ('' + substring(@BinChkSumTrgColSQL,2,len(@BinChkSumTrgColSQL)) + '')'' '
              + @ln + ' Set @execSQL = ''Update Trg Set ''+' + @ln + @ln + ''' '' + @updColSQL + @HashKeyColSQL + '
              + CHAR(39) + @InnerJoin + CHAR(39) + @ln
              + --Check if Join columns uses HashKey
        +IIF(CHARINDEX('HashKey_DW', @upsJoinColumns) > 0,
             '+ IIF(len(@HashKeyColSQL)> 0,'' AND  '' + Substring(@HashKeyColSQL,2,len(@HashKeyColSQL)), '''') ',
             '') + '+'' AND  '' + @BinChkSumSrcColSQL + ''<>'' + @BinChkSumTrgColSQL ' + @ln
              + IIF(@dbg >= 1, ' Print @execSQL ', '') + @ln + 'BEGIN TRY' + @ln + 'exec(@execSQL);' + @ln
              + ' Update #LogDynSQL set UpdateCmd=@execSQL, Updated=@@ROWCOUNT WHERE FlowID='
              + CAST(@FlowID AS NVARCHAR(250)) + @ln + 'END TRY' + @ln + 'BEGIN CATCH' + @ln + @ln
              + 'set @err  = ERROR_MESSAGE()' + @ln
              + ' Update #LogDynSQL set UpdateCmd=@execSQL, ErrorUpdate = @err WHERE FlowID='
              + CAST(@FlowID AS NVARCHAR(250)) + @ln + 'END CATCH ' + @ln;

        IF @dbg > 1
        BEGIN
            SET @curSection
                = N'100 :: ' + @curObjName
                  + N' :: Construct final dynamic flw. The final statement builds yet another dynamic flw. This ensures the posiblitiy for execution against remote databases ';
            SET @curCode = @sqlCMDCur;
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;
        SET @sqlCMDUpdate = @sqlCMDUpdate + @sqlCMDCur;

        --Execute UpdateSQL
        SET @StartTime = GETDATE();
        BEGIN TRY
            EXEC (@sqlCMDUpdate);
            SELECT @rowCount = @@ROWCOUNT,
                   @err = @@ERROR,
                   @EndTime = GETDATE();

            UPDATE TOP (1) trg
               SET         trg.[EndTime] = @EndTime,
                           trg.[Updated] = src.Updated,
                           trg.[UpdateCmd] = src.UpdateCmd,
                           trg.[RuntimeCmd] = CASE
                                                   WHEN LEN(src.ErrorUpdate) > 0 THEN @sqlCMDUpdate
                                                   ELSE NULL END,
                           trg.ErrorUpdate = src.ErrorUpdate,
                           trg.[Success] = CASE
                                                WHEN LEN(src.ErrorUpdate) > 0 THEN 0
                                                ELSE 0 END
              FROM         flw.SysLog trg
             INNER JOIN    #LogDynSQL src
                ON trg.FlowID = src.FlowID;
        --**Add logging**
        END TRY
        BEGIN CATCH
            SELECT @rowCount = @@ROWCOUNT,
                   @err = @@ERROR,
                   @err_msg = ERROR_MESSAGE(),
                   @EndTime = GETDATE();
            --**Add logging**
            UPDATE TOP (1) trg
               SET         trg.[EndTime] = @EndTime,
                           trg.[Updated] = src.Updated,
                           trg.[UpdateCmd] = src.UpdateCmd,
                           trg.[RuntimeCmd] = CASE
                                                   WHEN LEN(@err_msg) > 0 THEN @sqlCMDUpdate
                                                   ELSE NULL END,
                           trg.ErrorUpdate = CASE
                                                  WHEN LEN(@err_msg) > 0 THEN @err_msg
                                                  ELSE trg.[ErrorUpdate] END,
                           trg.[Success] = CASE
                                                WHEN LEN(@err_msg) > 0 THEN 0
                                                ELSE 1 END
              FROM         flw.SysLog trg
             INNER JOIN    #LogDynSQL src
                ON trg.FlowID = src.FlowID;

            IF (@StopCode = 16)
            BEGIN
                RAISERROR(@err_msg, @StopCode, -1);
            END;
        END CATCH;
        SET @Duration = DATEDIFF(ss, @StartTime, @EndTime);
        SET @DurationTot = @DurationTot + CAST(@Duration AS INT);
        SET @RowsAffected
            = @RowsAffected + 'Updated: ' + CAST(@rowCount AS VARCHAR(255)) + ' (' + @Duration + ' sek)' + ', ';
    END;

    IF (@upsSkipInsert = 0)
    BEGIN

        IF @dbg > 1
        BEGIN
            SET @curSection = N'125 :: ' + @curObjName + N' :: ###### Build InsertSQL ######';
            SET @curCode = N'';
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;

        --Create Key Params for the dynamic SQL
        SET @sqlCMDCur
            = +' DECLARE @err nvarchar(max), @tmpTbl int=''0'',@colSQL varchar(max)='''',  @execSQL varchar(max) = '''', @HashKeyCol varchar(max)='''
              + IIF(LEN(@HashKeyColumns) > 0, ',HashKey_DW', '')
              + ''', @HashKeyColSQL varchar(max)='''', @ChkSumSrcColSQL varchar(max)='''', @RevChkSumSrcColSQL varchar(max)='''', @RevChkSumSrcColSortSQL varchar(max)='''', @NewTblSQL varchar(max)='''' '
              + ' ,@BinChkSumSrcColSQL varchar(max)='''', @BinChkSumTrgColSQL varchar(max) = '''' ' + @ln;

        IF @dbg > 1
        BEGIN
            SET @curSection = N'130 :: ' + @curObjName + N' :: Create Key Params for the dynamic SQL ';
            SET @curCode = @sqlCMDCur;
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;
        SET @sqlCMDInsert = @sqlCMDCur;

        --Check if a Hashkey calculation should be added to the target table
        IF (LEN(@HashKeyColumns) > 0)
        BEGIN
            SET @sqlCMDCur
                = ' Select @ChkSumSrcColSQL = @ChkSumSrcColSQL + '',src.['' + src.name + '']'',  @RevChkSumSrcColSQL = @RevChkSumSrcColSQL + '',Reverse(src.['' + src.name + ''])'' from '
                  + @ln + @FromDatabase + '.sys.syscolumns Src inner join ' + @ToDatabase
                  + '.sys.syscolumns Trg on src.name = trg.name WHERE trg.name IN (select Replace(Replace(item,''['',''''),'']'','''') from flw.SplitStr2Tbl('
                  + CHAR(39) + @HashKeyColumns + CHAR(39) + ','','')) AND ' + @ln + ' src.ID = object_id(' + CHAR(39)
                  + @FromDatabase + '.' + @FromObject + CHAR(39) + ')' + @ln + ' AND trg.ID = object_id(' + CHAR(39)
                  + @ToDatabase + '.' + @ToObject + CHAR(39) + ')' + @ln + ' ORDER BY trg.colorder '
                  + ' SELECT  @RevChkSumSrcColSortSQL = @RevChkSumSrcColSortSQL +'',''+ ltrim(rtrim(Item)) FROM  flw.SplitStr2Tbl(@RevChkSumSrcColSQL, '','') where len(item)>0 Order by RecID desc ' --Resort column order for lesser collision
                  + ' Set @HashKeyColSQL = '', CHECKSUM('' + Substring(@ChkSumSrcColSQL,2,len(@ChkSumSrcColSQL)) + '','' + Substring(@RevChkSumSrcColSortSQL,2,len(@RevChkSumSrcColSortSQL)) + '')  ''  '
                  + @ln;

            IF @dbg > 1
            BEGIN
                SET @curSection
                    = N'140 :: ' + @curObjName
                      + N' :: Check if a Hashkey calculation should be added to the target table';
                SET @curCode = @sqlCMDCur;
                PRINT [flw].[GetLogSection](@curSection, @curCode);
            END;
            SET @sqlCMDInsert = @sqlCMDInsert + @sqlCMDCur;
        END;

        --Dupe temp table, if yes drop it
        SET @sqlCMDCur
            = ' SELECT @tmpTbl = count(*) ' + @ln + ' From ' + @ToDatabase + '.sys.objects ' + @ln
              + ' Where object_id = object_id(' + CHAR(39) + @ToDatabase + '.' + @tmpIncrTbl + CHAR(39) + ')' + @ln
              + ' IF(@tmpTbl >=1) ' + @ln + 'BEGIN Drop Table If Exists ' + @ToDatabase + '.' + @tmpIncrTbl + @ln
              + ' END' + @ln;

        IF @dbg > 1
        BEGIN
            SET @curSection = N'150 :: ' + @curObjName + N' :: Dupe temp table, if yes drop it';
            SET @curCode = @sqlCMDCur;
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;
        SET @sqlCMDInsert = @sqlCMDInsert + @sqlCMDCur;

        SET @sqlCMDCur
            = ' DECLARE @DupeTbl int ' + @ln + ' SELECT @DupeTbl = count(*) from ' + @ln + @ToDatabase
              + '.sys.objects ' + @ln + ' Where object_id = object_id(' + CHAR(39) + @ToDatabase + '.' + @ToObject
              + CHAR(39) + ')' + ' ' + @ln + ' IF(@DupeTbl = 0 or @DupeTbl is null) ' + @ln + ' BEGIN ' + @ln + @ln
              + ' Set @execSQL = '' SELECT * ''+@HashKeyColSQL+'' INTO ' + @ToDatabase + '.' + @ToObject + ' FROM '
              + @FromDatabase + '.' + @FromObject + ' src' + '''' + @ln
              + IIF(LEN(@srcFilter) > 0, REPLACE(@srcFilter, CHAR(39), CHAR(39) + CHAR(39)) + CHAR(39), '') + @ln
              + IIF(@dbg >= 1, ' Print @execSQL', '') + @ln + 'BEGIN TRY' + @ln + 'exec(@execSQL);' + @ln
              + ' Update #LogDynSQL set InsertCmd=@execSQL , Inserted=@@ROWCOUNT WHERE FlowID='
              + CAST(@FlowID AS NVARCHAR(250)) + @ln + 'END TRY' + @ln + 'BEGIN CATCH' + @ln + @ln
              + 'SELECT @err = ERROR_MESSAGE()' + @ln
              + ' Update #LogDynSQL set InsertCmd=@execSQL , ErrorInsert = @err WHERE FlowID='
              + CAST(@FlowID AS NVARCHAR(250)) + @ln + ' END CATCH ' + @ln + ' END';
        IF @dbg > 1
        BEGIN
            SET @curSection = N'160 :: ' + @curObjName + N' :: Dupe temp table, if yes drop it';
            SET @curCode = @sqlCMDCur;
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;
        SET @sqlCMDInsert = @sqlCMDInsert + @sqlCMDCur;

        --Build column list for the insert statment. SysCols are ommited.
        SET @sqlCMDCur
            = ' Select @colSQL = @colSQL + '',src.['' + src.name + '']'' ' + @ln + ' From ' + @FromDatabase
              + '.sys.syscolumns Src inner join ' + @ToDatabase + '.sys.syscolumns Trg ' + @ln
              + ' on src.name = trg.name  ' + @ln
              + ' WHERE trg.name NOT IN (select Replace(Replace(item,''['',''''),'']'','''') from flw.SplitStr2Tbl('
              + CHAR(39) + ',UpdatedDate_DW,DeletedDate_DW,InsertedDate_DW,RowStatus_DW'
              + IIF(LEN(@HashKeyColumns) > 0, ',HashKey_DW', '') + CHAR(39) + ','','')) AND src.ID = object_id('
              + CHAR(39) + @FromDatabase + '.' + @FromObject + CHAR(39) + ')' + @ln + ' AND trg.ID = object_id('
              + CHAR(39) + @ToDatabase + '.' + @ToObject + CHAR(39) + ')' + @ln + ' ORDER BY trg.colorder ' + @ln;

        IF @dbg > 1
        BEGIN
            SET @curSection
                = N'160 :: ' + @curObjName + N' :: Build column list for the insert statment. SysCols are ommited.';
            SET @curCode = @sqlCMDCur;
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;
        SET @sqlCMDInsert = @sqlCMDInsert + @sqlCMDCur;

        --Check sys cols UpdatedDate_DW and RowStatus_DW exist. If yes add them to the list at a later point
        SET @sqlCMDCur
            = ' DECLARE @StatusColExp varchar(255), @StatusCol varchar(255) ' + @ln
              + ' SELECT @StatusColExp = Max(IIf(trg.name = ''UpdatedDate_DW'','', getdate() as ['' + trg.name + '']'','''')) + Max(IIf(trg.name = ''InsertedDate_DW'','', getdate() as ['' + trg.name + '']'','''')) + Max(IIf(trg.name = ''RowStatus_DW'','', ''''I'''' as ['' + trg.name + '']'' ,''''))'
              + @ln
              + ' ,@StatusCol = Max(IIf(trg.name = ''UpdatedDate_DW'','',['' + trg.name + '']'','''')) + Max(IIf(trg.name = ''InsertedDate_DW'','',['' + trg.name + '']'','''')) + Max(IIf(trg.name = ''RowStatus_DW'','',['' + trg.name + ''] '' ,''''))'
              + @ln + ' FROM ' + @ToDatabase + '.sys.syscolumns Trg ' + @ln + ' WHERE trg.ID = object_id(' + CHAR(39)
              + @ToDatabase + '.' + @ToObject + CHAR(39) + ')' + @ln;

        IF @dbg > 1
        BEGIN
            SET @curSection
                = N'170 :: ' + @curObjName
                  + N' :: Check sys cols UpdatedDate_DW and RowStatus_DW exist. If yes add them to the list at a later point';
            SET @curCode = @sqlCMDCur;
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;
        SET @sqlCMDInsert = @sqlCMDInsert + @sqlCMDCur;

        --Build outer join expresion
        --+ ' AND' + SUBSTRING(@HashKeyColSQL, 2, LEN(@HashKeyColSQL)) +
        SET @OuterJoin
            = N' FROM ' + @FromDatabase + N'.' + @FromObject + N' as Src LEFT OUTER JOIN ' + @ToDatabase + N'.'
              + @ToObject + N' as Trg ON' + @Join
              + IIF(CHARINDEX('HashKey_DW', @upsJoinColumns) > 0,
                    ' '' + IIF(len(@HashKeyColSQL)> 0, ''AND  trg.HashKey_DW='' + Substring(@HashKeyColSQL,2,len(@HashKeyColSQL)),'''') + '' ',
                    '') + N' WHERE trg.' + ISNULL((SELECT TOP 1 Item FROM flw.SplitStr2Tbl(@upsJoinColumns, ',') ), '')
              + N' IS NULL ';

        --IF @dbg = 1
        --BEGIN
        --    SET @curSection = N'180 :: ' + @curObjName + N' :: Build outer join expresion';
        --    SET @curCode = @OuterJoin;
        --    PRINT [flw].[GetLogSection](@curSection, @curCode);
        --END;

        --Build Final Insert statment. Created Temp Table, Insert To TempTablea transfer new rows to target.
        IF (@useTmpTblForinsert = 1)
        BEGIN
            SET @sqlCMDCur
                = ' SET @colSQL = substring(@colSQL,2,len(@colSQL)) ' + @ln
                  + ' DECLARE @SQLIns varchar(max) set  @SQLIns = ''SELECT '' + @colSQL + IIF(len(@HashKeyColSQL)> 0, @HashKeyColSQL + ''as HashKey_DW '','''') + '' INTO ''+'
                  + @ln + CHAR(39) + @ToDatabase + '.' + @tmpIncrTbl + @OuterJoin + ' AND 1 <> 1 ' + CHAR(39) + @ln
                  + '+'' INSERT INTO ' + @ToDatabase + '.' + @tmpIncrTbl
                  + '(''+@colSQL+IsNull(@StatusCol,'''')+@HashKeyCol+'')''+' + @ln + ''' SELECT '
                  + '''+@colSQL+IsNull(@StatusColExp,'''')+@HashKeyCol+''''+' + CHAR(39) + @OuterJoin + CHAR(39) + @ln
                  + @ln + '+'' INSERT INTO ' + @ToDatabase + '.' + @ToObject
                  + '(''+@colSQL+IsNull(@StatusCol,'''')+@HashKeyCol+'')''+' + @ln + ''' SELECT '
                  + '''+@colSQL+IsNull(@StatusColExp,'''')+@HashKeyCol+''''+' + CHAR(39) + ' From ' + @ToDatabase + '.'
                  + @tmpIncrTbl + ' src' + CHAR(39) + IIF(@dbg >= 1, ' Print @SQLIns ', '') + @ln + 'BEGIN TRY' + @ln
                  + 'exec(@SQLIns);' + @ln
                  + ' Update #LogDynSQL set InsertCmd=@SQLIns, Inserted=@@ROWCOUNT WHERE FlowID='
                  + CAST(@FlowID AS NVARCHAR(250)) + @ln + 'END TRY' + @ln + 'BEGIN CATCH' + @ln
                  + 'set @err  = ERROR_MESSAGE()' + @ln
                  + ' Update #LogDynSQL set InsertCmd=@SQLIns, ErrorInsert = @err WHERE FlowID='
                  + CAST(@FlowID AS NVARCHAR(250)) + @ln + 'END CATCH ' + @ln;
            IF @dbg > 1
            BEGIN
                SET @curSection
                    = N'190 :: ' + @curObjName
                      + N' :: Created Temp Table, Insert To TempTablea transfer new rows to target.';
                SET @curCode = @sqlCMDCur;
                PRINT [flw].[GetLogSection](@curSection, @curCode);
            END;
            SET @sqlCMDInsert = @sqlCMDInsert + @sqlCMDCur;
        END;
        ELSE
        BEGIN
            SET @sqlCMDCur
                = ' SET @colSQL = substring(@colSQL,2,len(@colSQL)) ' + @ln
                  + ' DECLARE @SQLIns varchar(max) set  @SQLIns = '' INSERT INTO ' + @ToDatabase + '.' + @ToObject
                  + '(''+@colSQL+IsNull(@StatusCol,'''')+@HashKeyCol+'')''+' + @ln + ''' SELECT '
                  + '''+@colSQL+IsNull(@StatusColExp,'''')+@HashKeyCol+''''+' + CHAR(39) + @OuterJoin + CHAR(39)
                  + IIF(@dbg >= 1, ' Print @SQLIns ', '') + @ln + 'BEGIN TRY' + @ln + 'exec(@SQLIns);' + @ln
                  + ' Update #LogDynSQL set InsertCmd=@SQLIns, Inserted=@@ROWCOUNT WHERE FlowID='
                  + CAST(@FlowID AS NVARCHAR(250)) + @ln + 'END TRY' + @ln + ' BEGIN CATCH ' + @ln
                  + 'SELECT  @err = ERROR_MESSAGE(); Update #LogDynSQL set InsertCmd=@SQLIns, ErrorInsert=@err WHERE FlowID='
                  + CAST(@FlowID AS NVARCHAR(250)) + @ln + 'END CATCH ' + @ln;
            IF @dbg > 1
            BEGIN
                SET @curSection = N'190 :: ' + @curObjName + N' :: Transfer new rows to target.';
                SET @curCode = @sqlCMDCur;
                PRINT [flw].[GetLogSection](@curSection, @curCode);
            END;
            SET @sqlCMDInsert = @sqlCMDInsert + @sqlCMDCur;

        END;
        --Execute sql and fetch key metrics
        SET @StartTime = GETDATE();
        BEGIN TRY
            EXEC (@sqlCMDInsert);
            SELECT @rowCount = @@ROWCOUNT,
                   @err = @@ERROR,
                   @err_msg = ERROR_MESSAGE(),
                   @EndTime = GETDATE();

            UPDATE TOP (1) trg
               SET         trg.[EndTime] = @EndTime,
                           trg.[Inserted] = src.Inserted,
                           trg.[InsertCmd] = src.InsertCmd,
                           trg.[RuntimeCmd] = CASE
                                                   WHEN LEN(src.ErrorInsert) > 0 THEN @sqlCMDInsert
                                                   ELSE NULL END,
                           trg.ErrorInsert = src.ErrorInsert,
                           trg.[Success] = CASE
                                                WHEN LEN(src.ErrorInsert) > 0 THEN 0
                                                ELSE 1 END
              FROM         flw.SysLog trg
             INNER JOIN    #LogDynSQL src
                ON trg.FlowID = src.FlowID;

        --If debug dont drop the temp table 
        --IF (@dbg) = 0
        --BEGIN
        --    SET @sqlCMDInsert
        --        = 'IF(OBJECT_ID(''' + @ToDatabase + '.' + @tmpIncrTbl + ''') IS NOT NULL) DROP TABLE '
        --          + @ToDatabase + '.' + @tmpIncrTbl;
        --    PRINT @sqlCMDInsert
        --    EXEC (@sqlCMDInsert);
        --END;
        END TRY
        BEGIN CATCH

            SELECT @rowCount = @@ROWCOUNT,
                   @err = @@ERROR,
                   @err_msg = ERROR_MESSAGE(),
                   @EndTime = GETDATE();

            SELECT *
              FROM #LogDynSQL src;

            UPDATE TOP (1) trg
               SET         trg.[EndTime] = GETDATE(),
                           trg.[Inserted] = src.Inserted,
                           trg.[InsertCmd] = src.InsertCmd,
                           trg.[RuntimeCmd] = CASE
                                                   WHEN LEN(@err_msg) > 0 THEN @sqlCMDInsert
                                                   ELSE NULL END,
                           trg.ErrorInsert = CASE
                                                  WHEN LEN(@err_msg) > 0 THEN @err_msg
                                                  ELSE trg.ErrorInsert END,
                           trg.[Success] = CASE
                                                WHEN LEN(@err_msg) > 0 THEN 0
                                                ELSE 1 END
              FROM         flw.SysLog trg
             INNER JOIN    #LogDynSQL src
                ON trg.FlowID = src.FlowID;

            IF (@StopCode = 16)
            BEGIN
                RAISERROR(@err_msg, @StopCode, -1);
            END;
        END CATCH;
        SET @Duration = DATEDIFF(ss, @StartTime, @EndTime);
        SET @DurationTot = @DurationTot + CAST(@Duration AS INT);
        SET @RowsAffected
            = @RowsAffected + 'Inserted: ' + CAST(@rowCount AS VARCHAR(255)) + ' (' + @Duration + ' sek)' + ', ';

        SET @sqlCMDDelete = '';
        IF (@upsSkipDelete = 0)
        BEGIN
            --Create Key Params for the dynamic SQL
            SET @sqlCMDCur
                = +'DECLARE @err nvarchar(4000), @HashKeyCol varchar(max)='''
                  + IIF(LEN(@HashKeyColumns) > 0, ',HashKey_DW', '')
                  + ''', @HashKeyColSQL varchar(max)='''', @ChkSumSrcColSQL varchar(max)='''', @RevChkSumSrcColSQL varchar(max)='''', @RevChkSumSrcColSortSQL varchar(max)='''' '
                  + @ln;

            IF @dbg > 1
            BEGIN
                SET @curSection = N'200 :: ' + @curObjName + N' :: Create Key Params for the dynamic SQL';
                SET @curCode = @sqlCMDCur;
                PRINT [flw].[GetLogSection](@curSection, @curCode);
            END;
            SET @sqlCMDDelete = @sqlCMDCur;

            --Check if a Hashkey calculation should be added to the target table
            IF (LEN(@HashKeyColumns) > 0)
            BEGIN
                SET @sqlCMDCur
                    = ' Select @ChkSumSrcColSQL = @ChkSumSrcColSQL + '',src.['' + src.name + '']'',  @RevChkSumSrcColSQL = @RevChkSumSrcColSQL + '',Reverse(src.['' + src.name + ''])'' from '
                      + @ln + @FromDatabase + '.sys.syscolumns Src inner join ' + @ToDatabase
                      + '.sys.syscolumns Trg on src.name = trg.name WHERE trg.name IN (select Replace(Replace(item,''['',''''),'']'','''') from flw.SplitStr2Tbl('
                      + CHAR(39) + @HashKeyColumns + CHAR(39) + ','','')) AND ' + @ln + ' src.ID = object_id('
                      + CHAR(39) + @FromDatabase + '.' + @FromObject + CHAR(39) + ')' + @ln
                      + ' AND trg.ID = object_id(' + CHAR(39) + @ToDatabase + '.' + @ToObject + CHAR(39) + ')' + @ln
                      + ' ORDER BY trg.colorder '
                      + ' SELECT  @RevChkSumSrcColSortSQL = @RevChkSumSrcColSortSQL +'',''+ ltrim(rtrim(Item)) FROM  flw.SplitStr2Tbl(@RevChkSumSrcColSQL, '','') where len(item)>0 Order by RecID desc ' --Resort column order for lesser collision
                      + ' Set @HashKeyColSQL = '', CHECKSUM('' + Substring(@ChkSumSrcColSQL,2,len(@ChkSumSrcColSQL)) + '','' + Substring(@RevChkSumSrcColSortSQL,2,len(@RevChkSumSrcColSortSQL)) + '')  ''  '
                      + @ln;
            END;

            IF @dbg > 1
            BEGIN
                SET @curSection
                    = N'210 :: ' + @curObjName
                      + N' :: Check if a Hashkey calculation should be added to the target table';
                SET @curCode = @sqlCMDCur;
                PRINT [flw].[GetLogSection](@curSection, @curCode);
            END;
            SET @sqlCMDDelete = @sqlCMDDelete + @sqlCMDCur;


            --Build outer join expresion
            SET @OuterJoinDel
                = N' FROM ' + @FromDatabase + N'.' + @FromObject + N' as Src LEFT OUTER JOIN ' + @ToDatabase + N'.'
                  + @ToObject + N' as Trg ON' + @Join
                  + IIF(CHARINDEX('HashKey_DW', @upsJoinColumns) > 0,
                        ' '' + IIF(len(@HashKeyColSQL)> 0, ''AND trg.HashKey_DW='' + Substring(@HashKeyColSQL,2,len(@HashKeyColSQL)),'''') + '' ',
                        '') + N' WHERE src. ' + (SELECT TOP 1 Item FROM flw.SplitStr2Tbl(@upsJoinColumns, ',') )
                  + N' IS NULL ';

            --IF @dbg = 1
            --BEGIN
            --    SET @curSection = N'220 :: ' + @curObjName + N' :: Build outer join expresion';
            --    SET @curCode = @OuterJoinDel;
            --    PRINT [flw].[GetLogSection](@curSection, @curCode);
            --END;

            IF (@upsDeleteNonExisting = 1)
            BEGIN
                IF @dbg > 1
                BEGIN
                    SET @curSection = N'225 :: ' + @curObjName + N' :: ###### Build DeleteSQL ######';
                    SET @curCode = N'';
                    PRINT [flw].[GetLogSection](@curSection, @curCode);
                END;

                SET @sqlCMDCur
                    = ' DECLARE @execSQLDel varchar(max)  set  @execSQLDel = ''DELETE FROM Trg '' +' + @ln + CHAR(39)
                      + @OuterJoinDel + CHAR(39) + @ln + IIF(@dbg >= 1, ' Print @execSQLDel ', '') + 'BEGIN TRY' + @ln
                      + 'exec(@execSQLDel);' + @ln
                      + ' Update #LogDynSQL set DeleteCmd=@execSQLDel,Deleted=@@ROWCOUNT WHERE FlowID='
                      + CAST(@FlowID AS NVARCHAR(250)) + @ln + 'END TRY' + @ln + 'BEGIN CATCH' + @ln + @ln
                      + 'SELECT @err = ERROR_MESSAGE()' + @ln
                      + ' Update #LogDynSQL set DeleteCmd=@execSQLDel, ErrorDelete = @err WHERE FlowID='
                      + CAST(@FlowID AS NVARCHAR(250)) + @ln + 'END CATCH ' + @ln;

                IF @dbg > 1
                BEGIN
                    SET @curSection = N'230 :: ' + @curObjName + N' :: @upsDeleteNonExisting Value';
                    SET @curCode = @sqlCMDCur;
                    PRINT [flw].[GetLogSection](@curSection, @curCode);
                END;
                SET @sqlCMDDelete = @sqlCMDDelete + @sqlCMDCur;
            END;
            ELSE
            BEGIN
                --Tag deleted rows
                SET @sqlCMDCur
                    = ' DECLARE  @LU varchar(255), @RS varchar(255) ' + @ln
                      + ' SELECT @LU = Max(IIf(trg.name = ''DeletedDate_DW'',''trg.['' + trg.name + '']=getdate()'','''')),'
                      + @ln
                      + ' @RS = Max(IIf(trg.name = ''RowStatus_DW'',''trg.['' + trg.name + '']= '' + char(39) +''D''  + char(39) ,''''))'
                      + @ln + ' FROM ' + @ToDatabase + '.sys.syscolumns Trg ' + @ln + ' WHERE trg.ID = object_id('
                      + CHAR(39) + @ToDatabase + '.' + @ToObject + CHAR(39) + ')' + @ln;

                IF @dbg > 1
                BEGIN
                    SET @curSection = N'240 :: ' + @curObjName + N' :: Tag deleted rows';
                    SET @curCode = @sqlCMDCur;
                    PRINT [flw].[GetLogSection](@curSection, @curCode);
                END;
                SET @sqlCMDDelete = @sqlCMDDelete + @sqlCMDCur;

                --DeletedDate_DW,InsertedDate_DW,
                SET @sqlCMDCur
                    = ' IF((len(Isnull(@LU,'''')) + len(IsNull(@RS,''''))) > 0) ' + @ln
                      + ' BEGIN DECLARE @execSQLDel varchar(max) set @execSQLDel = ''Update Trg SET '' +Isnull(@LU,'''') +IIF(len(Isnull(@LU,'''')) > 0,'','','''') + IsNull(@RS,'''') +'
                      + @ln + CHAR(39) + @OuterJoinDel + CHAR(39) + @ln + IIF(@dbg >= 1, ' Print @execSQLDel ', '')
                      + 'BEGIN TRY' + @ln + 'exec(@execSQLDel);' + @ln
                      + ' Update #LogDynSQL set DeleteCmd=@execSQLDel,Deleted=@@ROWCOUNT WHERE FlowID='
                      + CAST(@FlowID AS NVARCHAR(250)) + @ln + 'END TRY' + @ln + 'BEGIN CATCH' + @ln
                      + 'set @err  = ERROR_MESSAGE()'
                      + ' Update #LogDynSQL set DeleteCmd=@execSQLDel, ErrorDelete = @err WHERE FlowID='
                      + CAST(@FlowID AS NVARCHAR(250)) + @ln + 'END CATCH ' + @ln + ' END';

                IF @dbg > 1
                BEGIN
                    SET @curSection = N'250 :: ' + @curObjName + N' :: DeletedDate_DW,InsertedDate_DW';
                    SET @curCode = @sqlCMDCur;
                    PRINT [flw].[GetLogSection](@curSection, @curCode);
                END;
                SET @sqlCMDDelete = @sqlCMDDelete + @sqlCMDCur;
            END;

            --Execute sql and fetch key metrics
            SET @StartTime = GETDATE();
            BEGIN TRY
                EXEC (@sqlCMDDelete);
                SELECT @rowCount = @@ROWCOUNT,
                       @err = @@ERROR,
                       @EndTime = GETDATE();
                UPDATE TOP (1) trg
                   SET         trg.[EndTime] = @EndTime,
                               trg.[Deleted] = src.Deleted,
                               trg.[DeleteCmd] = src.DeleteCmd,
                               trg.[RuntimeCmd] = CASE
                                                       WHEN LEN(src.ErrorDelete) > 0 THEN @sqlCMDDelete
                                                       ELSE NULL END,
                               trg.ErrorDelete = src.ErrorDelete,
                               trg.[Success] = CASE
                                                    WHEN LEN(src.ErrorDelete) > 0 THEN 0
                                                    ELSE 0 END
                  FROM         flw.SysLog trg
                 INNER JOIN    #LogDynSQL src
                    ON trg.FlowID = src.FlowID;

            END TRY
            BEGIN CATCH
                SELECT @rowCount = @@ROWCOUNT,
                       @err = @@ERROR,
                       @err_msg = ERROR_MESSAGE(),
                       @EndTime = GETDATE();

                UPDATE TOP (1) trg
                   SET         trg.[EndTime] = @EndTime,
                               trg.[Deleted] = src.Deleted,
                               trg.[DeleteCmd] = src.DeleteCmd,
                               trg.[RuntimeCmd] = CASE
                                                       WHEN LEN(@err_msg) > 0 THEN @sqlCMDDelete
                                                       ELSE NULL END,
                               trg.ErrorDelete = CASE
                                                      WHEN LEN(@err_msg) > 0 THEN @err_msg
                                                      ELSE trg.[ErrorDelete] END,
                               trg.[Success] = CASE
                                                    WHEN LEN(@err_msg) > 0 THEN 0
                                                    ELSE 1 END
                  FROM         flw.SysLog trg
                 INNER JOIN    #LogDynSQL src
                    ON trg.FlowID = src.FlowID;

                IF (@StopCode = 16)
                BEGIN
                    RAISERROR(@err_msg, @StopCode, -1);
                END;

            END CATCH;
            SET @Duration = DATEDIFF(ss, @StartTime, @EndTime);
            SET @DurationTot = @DurationTot + CAST(@Duration AS INT);
            SET @RowsAffected
                = @RowsAffected + 'Deleted: ' + CAST(@rowCount AS VARCHAR(255)) + ' (' + @Duration + ' sek)' + ', ';

        END;

        DECLARE @sqlCMD NVARCHAR(MAX);
        SET @sqlCMD
            = N'--*' + @Process + N' ' + +CAST(@DurationTot AS VARCHAR(25)) + N' sekunder' + N'*--' + @ln
              + N' -- Update ' + @ln + ISNULL(@sqlCMDUpdate, 'Skipped') + @ln + N' -- Insert ' + @ln
              + ISNULL(@sqlCMDInsert, 'Skipped') + @ln + N' -- Delete ' + @ln + ISNULL(@sqlCMDDelete, 'Skipped') + @ln;

    --IF @dbg = 1
    --BEGIN
    --    SET @curSection = N'260 :: ' + @curObjName + N' :: TDeletedDate_DW,InsertedDate_DW';
    --    SET @curCode = @sqlCMD;
    --    PRINT [flw].[GetLogSection](@curSection, @curCode);
    --END;

    --Add flow log
    --If Print  Enabled logged by Service Broker
    --PRINT @RowsAffected;

    END;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   Merges a source dataset with the target. Not in use as the SQLFlow core engine genereates this now', 'SCHEMA', N'flw', 'PROCEDURE', N'Upsert', NULL, NULL
GO
