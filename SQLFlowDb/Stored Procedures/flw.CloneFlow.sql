SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
/*
  ##################################################################################################################################################
  -- Name				:   [flw].[CloneFlow]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   Clones a specific flow based on the provided FlowID and, depending on the value of the @Extended parameter, clones additional related information.
  -- Summary			:	The stored procedure handles different types of flows, such as 'csv', 'ADO', 'xls', 'xml', and 'ing'. For each flow type, 
							it selects the relevant data from the corresponding source table and inserts it into the destination table with the new cloned object name. 
							If the @Extended parameter is set to 1, it also clones related transformation and virtual column data for the flow.

							The main steps of the stored procedure are as follows:

							Get the flow type, target object name, and clone object name based on the input @FlowID.
							Based on the flow type, perform the following actions:
							a. Print the flow type.
							b. Insert the cloned data into the relevant destination table with the new cloned object name.
							c. If @Extended is set to 1, clone the related transformation and virtual column data for the flow.
							The procedure is used to clone an existing flow and its associated metadata, which might be helpful when you want to create a new flow with similar properties or settings as an existing one.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/

CREATE PROCEDURE [flw].[CloneFlow]
    @FlowID INT = 0,
    @Extended BIT = 0
AS
BEGIN
    --DECLARE @FlowID INT = 0;
    DECLARE @NewFlowID INT;
    DECLARE @FlowType VARCHAR(50) = '';
    DECLARE @TrgObjName NVARCHAR(255) = N'';
    DECLARE @CloneObjName NVARCHAR(255) = N'';

    SELECT @FlowType = FlowType,
           @TrgObjName = trgDBSchObj,
           @CloneObjName = CASE
                               WHEN RIGHT(trgDBSchObj, 1) = ']' THEN
                                   SUBSTRING(trgDBSchObj, 1, LEN(trgDBSchObj) - 1) + FORMAT(GETDATE(), 'ddhhss') + ']'
                               ELSE
                                   trgDBSchObj + FORMAT(GETDATE(), 'ddhhss')
                           END
    FROM [flw].[FlowDS]
    WHERE FlowID = @FlowID;

    IF @FlowType IN ( 'csv' )
    BEGIN
        PRINT @FlowType;
        INSERT INTO [flw].[PreIngestionCSV]
        (
            [SysAlias],
            [ServicePrincipalAlias],
            [Batch],
            [srcPath],
            [srcPathMask],
            [srcFile],
            [SearchSubDirectories],
            [copyToPath],
            [srcDeleteIngested],
            [srcDeleteAtPath],
            [zipToPath],
            [trgServer],
            [trgDBSchTbl],
            [preFilter],
            [ColumnDelimiter],
            [TextQualifier],
            [ColumnWidths],
            [FirstRowHasHeader],
            [SyncSchema],
            [ExpectedColumnCount],
            [DefaultColDataType],
            [OnErrorResume],
            [SkipStartingDataRows],
            [SkipEmptyRows],
            [IncludeFileLineNumber],
            [TrimResults],
            [StripControlChars],
            [FirstRowSetsExpectedColumnCount],
            [EscapeCharacter],
            [CommentCharacter],
            [SkipEndingDataRows],
            [MaxBufferSize],
            [MaxRows],
            [srcEncoding],
            [NoOfThreads],
            [PreProcessOnTrg],
            [PostProcessOnTrg],
            [PreInvokeAlias],
            [BatchOrderBy],
            [DeactivateFromBatch],
            [FlowType],
            [FromObjectMK],
            [ToObjectMK],
            [ShowPathWithFileName],
            [CreatedBy],
            [CreatedDate],
            [FetchDataTypes]
        )
        SELECT [SysAlias],
               [ServicePrincipalAlias],
               [Batch],
               [srcPath],
               [srcPathMask],
               [srcFile],
               [SearchSubDirectories],
               [copyToPath],
               [srcDeleteIngested],
               [srcDeleteAtPath],
               [zipToPath],
               [trgServer],
               @CloneObjName AS [trgDBSchTbl],
               [preFilter],
               [ColumnDelimiter],
               [TextQualifier],
               [ColumnWidths],
               [FirstRowHasHeader],
               [SyncSchema],
               [ExpectedColumnCount],
               [DefaultColDataType],
               [OnErrorResume],
               [SkipStartingDataRows],
               [SkipEmptyRows],
               [IncludeFileLineNumber],
               [TrimResults],
               [StripControlChars],
               [FirstRowSetsExpectedColumnCount],
               [EscapeCharacter],
               [CommentCharacter],
               [SkipEndingDataRows],
               [MaxBufferSize],
               [MaxRows],
               [srcEncoding],
               [NoOfThreads],
               [PreProcessOnTrg],
               [PostProcessOnTrg],
               [PreInvokeAlias],
               [BatchOrderBy],
               [DeactivateFromBatch],
               [FlowType],
               NULL AS [FromObjectMK],
               NULL AS [ToObjectMK],
               [ShowPathWithFileName],
               [CreatedBy],
               [CreatedDate],
               1
        FROM [flw].[PreIngestionCSV]
        WHERE FlowID = @FlowID;

        SELECT @NewFlowID = FlowID
        FROM [flw].[PreIngestionCSV]
        WHERE [trgDBSchTbl] = @CloneObjName;

        IF (@Extended = 1)
        BEGIN
            INSERT INTO [flw].[PreIngestionTransfrom]
            (
                [FlowID],
                [FlowType],
                [Virtual],
                [ColName],
                [SelectExp],
                [ColAlias],
                [SortOrder],
                [ExcludeColFromView]
            )
            SELECT @NewFlowID AS [FlowID],
                   [FlowType],
                   [Virtual],
                   [ColName],
                   [SelectExp],
                   [ColAlias],
                   [SortOrder],
                   [ExcludeColFromView]
            FROM [flw].[PreIngestionTransfrom]
            WHERE FlowID = @FlowID
            ORDER BY [TransfromID] ASC;

        END;

    END;
    ELSE IF @FlowType IN ( 'ADO' )
    BEGIN
        PRINT @FlowType;
        INSERT INTO [flw].[PreIngestionADO]
        (
            [SysAlias],
            [srcServer],
            [srcDatabase],
            [srcSchema],
            [srcObject],
            [trgServer],
            [trgDBSchTbl],
            [Batch],
            [BatchOrderBy],
            [DeactivateFromBatch],
            [StreamData],
            [NoOfThreads],
            [IncrementalColumns],
            [IncrementalClauseExp],
            [DateColumn],
            [NoOfOverlapDays],
            [FullLoad],
            [TruncateTrg],
            [srcFilter],
            [preFilter],
            [IgnoreColumns],
            [InitLoad],
            [InitLoadFromDate],
            [InitLoadToDate],
            [InitLoadBatchBy],
            [InitLoadBatchSize],
            [SyncSchema],
            [OnErrorResume],
            [CleanColumnNameSQLRegExp],
            [RemoveInColumnName],
            [PreProcessOnTrg],
            [PostProcessOnTrg],
            [PreInvokeAlias],
            [PostInvokeAlias],
            [FlowType],
            [Description],
            [FromObjectMK],
            [ToObjectMK],
            [CreatedBy],
            [CreatedDate]
        )
        SELECT [SysAlias],
               [srcServer],
               [srcDatabase],
               [srcSchema],
               [srcObject],
               [trgServer],
               @CloneObjName AS [trgDBSchTbl],
               [Batch],
               [BatchOrderBy],
               [DeactivateFromBatch],
               [StreamData],
               [NoOfThreads],
               [IncrementalColumns],
               [IncrementalClauseExp],
               [DateColumn],
               [NoOfOverlapDays],
               [FullLoad],
               [TruncateTrg],
               [srcFilter],
               [preFilter],
               [IgnoreColumns],
               [InitLoad],
               [InitLoadFromDate],
               [InitLoadToDate],
               [InitLoadBatchBy],
               [InitLoadBatchSize],
               [SyncSchema],
               [OnErrorResume],
               [CleanColumnNameSQLRegExp],
               [RemoveInColumnName],
               [PreProcessOnTrg],
               [PostProcessOnTrg],
               [PreInvokeAlias],
               [PostInvokeAlias],
               [FlowType],
               [Description],
               NULL AS [FromObjectMK],
               NULL AS [ToObjectMK],
               [CreatedBy],
               [CreatedDate]
        FROM [flw].[PreIngestionADO]
        WHERE FlowID = @FlowID;

        SELECT @NewFlowID = FlowID
        FROM [flw].[PreIngestionADO]
        WHERE [trgDBSchTbl] = @CloneObjName;

        IF (@Extended = 1)
        BEGIN
            INSERT INTO [flw].[PreIngestionTransfrom]
            (
                [FlowID],
                [FlowType],
                [Virtual],
                [ColName],
                [SelectExp],
                [ColAlias],
                [SortOrder],
                [ExcludeColFromView]
            )
            SELECT @NewFlowID AS [FlowID],
                   [FlowType],
                   [Virtual],
                   [ColName],
                   [SelectExp],
                   [ColAlias],
                   [SortOrder],
                   [ExcludeColFromView]
            FROM [flw].[PreIngestionTransfrom]
            WHERE FlowID = @FlowID
            ORDER BY [TransfromID] ASC;

            INSERT INTO [flw].[PreIngestionADOVirtual]
            (
                [FlowID],
                [ColumnName],
                [DataType],
                [Length],
                [Precision],
                [Scale],
                [SelectExp]
            )
            SELECT @NewFlowID AS [FlowID],
                   [ColumnName],
                   [DataType],
                   [Length],
                   [Precision],
                   [Scale],
                   [SelectExp]
            FROM [flw].[PreIngestionADOVirtual]
            WHERE FlowID = @FlowID
            ORDER BY [VirtualID] ASC;

        END;

    END;
    ELSE IF @FlowType IN ( 'xls' )
    BEGIN
        PRINT @FlowType;
        --@CloneObjName as [trgDBSchTbl]
        INSERT INTO [flw].[PreIngestionXLS]
        (
            [SysAlias],
            [ServicePrincipalAlias],
            [srcPath],
            [srcPathMask],
            [srcFile],
            [SearchSubDirectories],
            [copyToPath],
            [srcDeleteIngested],
            [srcDeleteAtPath],
            [zipToPath],
            [trgServer],
            [trgDBSchTbl],
            [preFilter],
            [FirstRowHasHeader],
            [SheetName],
            [SheetRange],
            [UseSheetIndex],
            [SyncSchema],
            [ExpectedColumnCount],
            [DefaultColDataType],
            [OnErrorResume],
            [NoOfThreads],
            [PreProcessOnTrg],
            [PostProcessOnTrg],
            [PreInvokeAlias],
            [Batch],
            [BatchOrderBy],
            [DeactivateFromBatch],
            [FlowType],
            [FromObjectMK],
            [ToObjectMK],
            [ShowPathWithFileName],
            [CreatedBy],
            [CreatedDate],
            [FetchDataTypes]
        )
        SELECT [SysAlias],
               [ServicePrincipalAlias],
               [srcPath],
               [srcPathMask],
               [srcFile],
               [SearchSubDirectories],
               [copyToPath],
               [srcDeleteIngested],
               [srcDeleteAtPath],
               [zipToPath],
               [trgServer],
               @CloneObjName AS [trgDBSchTbl],
               [preFilter],
               [FirstRowHasHeader],
               [SheetName],
               [SheetRange],
               [UseSheetIndex],
               [SyncSchema],
               [ExpectedColumnCount],
               [DefaultColDataType],
               [OnErrorResume],
               [NoOfThreads],
               [PreProcessOnTrg],
               [PostProcessOnTrg],
               [PreInvokeAlias],
               [Batch],
               [BatchOrderBy],
               [DeactivateFromBatch],
               [FlowType],
               NULL AS [FromObjectMK],
               NULL AS [ToObjectMK],
               [ShowPathWithFileName],
               [CreatedBy],
               [CreatedDate],
               1
        FROM [flw].[PreIngestionXLS]
        WHERE FlowID = @FlowID;

        SELECT @NewFlowID = FlowID
        FROM [flw].[PreIngestionXLS]
        WHERE [trgDBSchTbl] = @CloneObjName;

        IF (@Extended = 1)
        BEGIN
            INSERT INTO [flw].[PreIngestionTransfrom]
            (
                [FlowID],
                [FlowType],
                [Virtual],
                [ColName],
                [SelectExp],
                [ColAlias],
                [SortOrder],
                [ExcludeColFromView]
            )
            SELECT @NewFlowID AS [FlowID],
                   [FlowType],
                   [Virtual],
                   [ColName],
                   [SelectExp],
                   [ColAlias],
                   [SortOrder],
                   [ExcludeColFromView]
            FROM [flw].[PreIngestionTransfrom]
            WHERE FlowID = @FlowID
            ORDER BY [TransfromID] ASC;
        END;

    END;

    ELSE IF @FlowType IN ( 'prq' )
    BEGIN
        PRINT @FlowType;
        --@CloneObjName as [trgDBSchTbl]
        INSERT INTO [flw].[PreIngestionPRQ]
        (
            [SysAlias],
            [ServicePrincipalAlias],
            [srcPath],
            [srcPathMask],
            [srcFile],
            [SearchSubDirectories],
            [copyToPath],
            [srcDeleteIngested],
            [srcDeleteAtPath],
            [zipToPath],
            [trgServer],
            [trgDBSchTbl],
            [preFilter],
            [PartitionList],
            [SyncSchema],
            [ExpectedColumnCount],
            [FetchDataTypes],
            [OnErrorResume],
            [NoOfThreads],
            [PreProcessOnTrg],
            [PostProcessOnTrg],
            [InitFromFileDate],
            [InitToFileDate],
            [PreInvokeAlias],
            [Batch],
            [BatchOrderBy],
            [DeactivateFromBatch],
            [FlowType],
            [FromObjectMK],
            [ToObjectMK],
            [ShowPathWithFileName],
            [CreatedBy],
            [CreatedDate]
        )
        SELECT [SysAlias],
               [ServicePrincipalAlias],
               [srcPath],
               [srcPathMask],
               [srcFile],
               [SearchSubDirectories],
               [copyToPath],
               [srcDeleteIngested],
               [srcDeleteAtPath],
               [zipToPath],
               [trgServer],
               @CloneObjName AS [trgDBSchTbl],
               [preFilter],
               [PartitionList],
               [SyncSchema],
               [ExpectedColumnCount],
               [FetchDataTypes],
               [OnErrorResume],
               [NoOfThreads],
               [PreProcessOnTrg],
               [PostProcessOnTrg],
               [InitFromFileDate],
               [InitToFileDate],
               [PreInvokeAlias],
               [Batch],
               [BatchOrderBy],
               [DeactivateFromBatch],
               [FlowType],
               NULL AS [FromObjectMK],
               NULL AS [ToObjectMK],
               [ShowPathWithFileName],
               [CreatedBy],
               [CreatedDate]
        FROM [flw].[PreIngestionPRQ]
        WHERE FlowID = @FlowID;

        SELECT @NewFlowID = FlowID
        FROM [flw].[PreIngestionPRQ]
        WHERE [trgDBSchTbl] = @CloneObjName;

        IF (@Extended = 1)
        BEGIN
            INSERT INTO [flw].[PreIngestionTransfrom]
            (
                [FlowID],
                [FlowType],
                [Virtual],
                [ColName],
                [SelectExp],
                [ColAlias],
                [SortOrder],
                [ExcludeColFromView]
            )
            SELECT @NewFlowID AS [FlowID],
                   [FlowType],
                   [Virtual],
                   [ColName],
                   [SelectExp],
                   [ColAlias],
                   [SortOrder],
                   [ExcludeColFromView]
            FROM [flw].[PreIngestionTransfrom]
            WHERE FlowID = @FlowID
            ORDER BY [TransfromID] ASC;
        END;

    END;

    ELSE IF @FlowType IN ( 'PRC' )
    BEGIN
        PRINT @FlowType;
        --@CloneObjName as [trgDBSchTbl]
        INSERT INTO [flw].[PreIngestionPRC]
        (
            [FlowID],
            [SysAlias],
            [ServicePrincipalAlias],
            [srcServer],
            [srcPath],
            [srcFile],
            [srcCode],
            [trgServer],
            [trgDBSchTbl],
            [preFilter],
            [SyncSchema],
            [ExpectedColumnCount],
            [FetchDataTypes],
            [OnErrorResume],
            [NoOfThreads],
            [PreProcessOnTrg],
            [PostProcessOnTrg],
            [PreInvokeAlias],
            [Batch],
            [BatchOrderBy],
            [DeactivateFromBatch],
            [FlowType],
            [FromObjectMK],
            [ToObjectMK],
            [ShowPathWithFileName],
            [CreatedBy],
            [CreatedDate]
        )
        SELECT [FlowID],
               [SysAlias],
               [ServicePrincipalAlias],
               [srcServer],
               [srcPath],
               [srcFile],
               [srcCode],
               [trgServer],
               @CloneObjName AS [trgDBSchTbl],
               [preFilter],
               [SyncSchema],
               [ExpectedColumnCount],
               [FetchDataTypes],
               [OnErrorResume],
               [NoOfThreads],
               [PreProcessOnTrg],
               [PostProcessOnTrg],
               [PreInvokeAlias],
               [Batch],
               [BatchOrderBy],
               [DeactivateFromBatch],
               [FlowType],
               NULL AS [FromObjectMK],
               NULL AS [ToObjectMK],
               [ShowPathWithFileName],
               [CreatedBy],
               [CreatedDate]
        FROM [flw].[PreIngestionPRC]
        WHERE FlowID = @FlowID;

        SELECT @NewFlowID = FlowID
        FROM [flw].[PreIngestionPRC]
        WHERE [trgDBSchTbl] = @CloneObjName;

        IF (@Extended = 1)
        BEGIN
            INSERT INTO [flw].[Parameter]
            (
                [FlowID],
                [ParamAltServer],
                [ParamName],
                [SelectExp],
                [PreFetch]
            )
            SELECT @NewFlowID AS [FlowID],
                   [ParamAltServer],
                   [ParamName],
                   [SelectExp],
                   [PreFetch]
            FROM [flw].[Parameter]
            WHERE FlowID = @FlowID
            ORDER BY [ParameterID] ASC;
        END;

    END;

    ELSE IF @FlowType IN ( 'JSN' )
    BEGIN
        PRINT @FlowType;
        --@CloneObjName as [trgDBSchTbl]

        INSERT INTO [flw].[PreIngestionJSN]
        (
            [SysAlias],
            [ServicePrincipalAlias],
            [srcPath],
            [srcPathMask],
            [srcFile],
            [trgServer],
            [trgDBSchTbl],
            [preFilter],
            [SearchSubDirectories],
            [copyToPath],
            [srcDeleteIngested],
            [srcDeleteAtPath],
            [zipToPath],
            [SyncSchema],
            [ExpectedColumnCount],
            [DefaultColDataType],
            [OnErrorResume],
            [NoOfThreads],
            [PreProcessOnTrg],
            [PostProcessOnTrg],
            [PreInvokeAlias],
            [Batch],
            [BatchOrderBy],
            [DeactivateFromBatch],
            [FlowType],
            [FromObjectMK],
            [ToObjectMK],
            [ShowPathWithFileName],
            [CreatedBy],
            [CreatedDate],
            [FetchDataTypes]
        )
        SELECT [SysAlias],
               [ServicePrincipalAlias],
               [srcPath],
               [srcPathMask],
               [srcFile],
               [trgServer],
               @CloneObjName AS [trgDBSchTbl],
               [preFilter],
               [SearchSubDirectories],
               [copyToPath],
               [srcDeleteIngested],
               [srcDeleteAtPath],
               [zipToPath],
               [SyncSchema],
               [ExpectedColumnCount],
               [DefaultColDataType],
               [OnErrorResume],
               [NoOfThreads],
               [PreProcessOnTrg],
               [PostProcessOnTrg],
               [PreInvokeAlias],
               [Batch],
               [BatchOrderBy],
               [DeactivateFromBatch],
               [FlowType],
               NULL AS [FromObjectMK],
               NULL AS [ToObjectMK],
               [ShowPathWithFileName],
               [CreatedBy],
               [CreatedDate],
               1
        FROM [flw].[PreIngestionJSN]
        WHERE FlowID = @FlowID;

        SELECT @NewFlowID = FlowID
        FROM [flw].[PreIngestionJSN]
        WHERE [trgDBSchTbl] = @CloneObjName;

        IF (@Extended = 1)
        BEGIN
            INSERT INTO [flw].[PreIngestionTransfrom]
            (
                [FlowID],
                [FlowType],
                [Virtual],
                [ColName],
                [SelectExp],
                [ColAlias],
                [SortOrder],
                [ExcludeColFromView]
            )
            SELECT @NewFlowID AS [FlowID],
                   [FlowType],
                   [Virtual],
                   [ColName],
                   [SelectExp],
                   [ColAlias],
                   [SortOrder],
                   [ExcludeColFromView]
            FROM [flw].[PreIngestionTransfrom]
            WHERE FlowID = @FlowID
            ORDER BY [TransfromID] ASC;
        END;

    END;
    ELSE IF @FlowType IN ( 'xml' )
    BEGIN
        PRINT @FlowType;
        --@CloneObjName as [trgDBSchTbl]

        INSERT INTO [flw].[PreIngestionXML]
        (
            [SysAlias],
            [ServicePrincipalAlias],
            [srcPath],
            [srcPathMask],
            [srcFile],
            [trgServer],
            [trgDBSchTbl],
            [preFilter],
            [SearchSubDirectories],
            [copyToPath],
            [srcDeleteIngested],
            [srcDeleteAtPath],
            [zipToPath],
            [SyncSchema],
            [ExpectedColumnCount],
            [DefaultColDataType],
            [OnErrorResume],
            [NoOfThreads],
            [PreProcessOnTrg],
            [PostProcessOnTrg],
            [PreInvokeAlias],
            [Batch],
            [BatchOrderBy],
            [DeactivateFromBatch],
            [FlowType],
            [FromObjectMK],
            [ToObjectMK],
            [ShowPathWithFileName],
            [CreatedBy],
            [CreatedDate],
            [FetchDataTypes]
        )
        SELECT [SysAlias],
               [ServicePrincipalAlias],
               [srcPath],
               [srcPathMask],
               [srcFile],
               [trgServer],
               @CloneObjName AS [trgDBSchTbl],
               [preFilter],
               [SearchSubDirectories],
               [copyToPath],
               [srcDeleteIngested],
               [srcDeleteAtPath],
               [zipToPath],
               [SyncSchema],
               [ExpectedColumnCount],
               [DefaultColDataType],
               [OnErrorResume],
               [NoOfThreads],
               [PreProcessOnTrg],
               [PostProcessOnTrg],
               [PreInvokeAlias],
               [Batch],
               [BatchOrderBy],
               [DeactivateFromBatch],
               [FlowType],
               NULL AS [FromObjectMK],
               NULL AS [ToObjectMK],
               [ShowPathWithFileName],
               [CreatedBy],
               [CreatedDate],
               1
        FROM [flw].[PreIngestionXML]
        WHERE FlowID = @FlowID;

        SELECT @NewFlowID = FlowID
        FROM [flw].[PreIngestionXML]
        WHERE [trgDBSchTbl] = @CloneObjName;

        IF (@Extended = 1)
        BEGIN
            INSERT INTO [flw].[PreIngestionTransfrom]
            (
                [FlowID],
                [FlowType],
                [Virtual],
                [ColName],
                [SelectExp],
                [ColAlias],
                [SortOrder],
                [ExcludeColFromView]
            )
            SELECT @NewFlowID AS [FlowID],
                   [FlowType],
                   [Virtual],
                   [ColName],
                   [SelectExp],
                   [ColAlias],
                   [SortOrder],
                   [ExcludeColFromView]
            FROM [flw].[PreIngestionTransfrom]
            WHERE FlowID = @FlowID
            ORDER BY [TransfromID] ASC;
        END;

    END;

    ELSE IF @FlowType = 'ing'
    BEGIN
        PRINT @FlowType;

        INSERT INTO [flw].[Ingestion]
        (
            [SysAlias],
            [srcServer],
            [srcDBSchTbl],
            [trgServer],
            [trgDBSchTbl],
            [Batch],
            [BatchOrderBy],
            [DeactivateFromBatch],
            [StreamData],
            [NoOfThreads],
            [KeyColumns],
            [IncrementalColumns],
            [IncrementalClauseExp],
            [DateColumn],
            [DataSetColumn],
            [NoOfOverlapDays],
            [SkipUpdateExsisting],
            [SkipInsertNew],
            [FullLoad],
            [TruncateTrg],
            [srcFilter],
            IdentityColumn,
            [IgnoreColumns],
            [SysColumns],
            [ColumnStoreIndexOnTrg],
            [SyncSchema],
            [OnErrorResume],
            [ReplaceInvalidCharsWith],
            [OnSyncCleanColumnName],
            [OnSyncConvertUnicodeDataType],
            
            CleanColumnNameSQLRegExp,
            [trgVersioning],
            InsertUnknownDimRow,
            [TokenVersioning],
            [TokenRetentionDays],
            [PreProcessOnTrg],
            [PostProcessOnTrg],
            [PreInvokeAlias],
            [PostInvokeAlias],
            [FlowType],
            [Description],
            [FromObjectMK],
            [ToObjectMK],
            [CreatedBy],
            [CreatedDate],
            [Assertions],
			TruncatePreTableOnCompletion
        )
        SELECT [SysAlias],
               [srcServer],
               [srcDBSchTbl],
               [trgServer],
               @CloneObjName AS [trgDBSchTbl],
               [Batch],
               [BatchOrderBy],
               [DeactivateFromBatch],
               [StreamData],
               [NoOfThreads],
               [KeyColumns],
               [IncrementalColumns],
               [IncrementalClauseExp],
               [DateColumn],
               [DataSetColumn],
               [NoOfOverlapDays],
               [SkipUpdateExsisting],
               [SkipInsertNew],
               [FullLoad],
               [TruncateTrg],
               [srcFilter],
               IdentityColumn,
               [IgnoreColumns],
               [SysColumns],
               [ColumnStoreIndexOnTrg],
               [SyncSchema],
               [OnErrorResume],
               [ReplaceInvalidCharsWith],
               [OnSyncCleanColumnName],
               [OnSyncConvertUnicodeDataType],
               CleanColumnNameSQLRegExp,
               [trgVersioning],
               InsertUnknownDimRow,
               [TokenVersioning],
               [TokenRetentionDays],
               [PreProcessOnTrg],
               [PostProcessOnTrg],
               [PreInvokeAlias],
               [PostInvokeAlias],
               [FlowType],
               [Description],
               NULL AS [FromObjectMK],
               NULL AS [ToObjectMK],
               [CreatedBy],
               [CreatedDate],
               [Assertions],
			   TruncatePreTableOnCompletion
        FROM [flw].[Ingestion]
        WHERE FlowID = @FlowID;

        SELECT @NewFlowID = FlowID
        FROM [flw].[Ingestion]
        WHERE [trgDBSchTbl] = @CloneObjName;


        IF (@Extended = 1)
        BEGIN

            INSERT INTO [flw].[IngestionVirtual]
            (
                [FlowID],
                [ColumnName],
                [DataType],
                [DataTypeExp],
                [SelectExp]
            )
            SELECT @NewFlowID AS [FlowID],
                   [ColumnName],
                   [DataType],
                   [DataTypeExp],
                   [SelectExp]
            FROM [flw].[IngestionVirtual]
            WHERE FlowID = @FlowID;

        END;



    END;

    ELSE IF @FlowType = 'exp'
    BEGIN
        PRINT @FlowType;

        INSERT INTO [flw].[Export]
        (
            [SysAlias],
            [Batch],
            [srcServer],
            [srcDBSchTbl],
            [IncrementalColumn],
            [NoOfOverlapDays],
            [DateColumn],
            [FromDate],
            [ToDate],
            [ExportBy],
            [ExportSize],
            [ServicePrincipalAlias],
            [trgPath],
            [trgFileName],
            [trgFiletype],
            [trgEncoding],
            [ColumnDelimiter],
            [TextQualifier],
            [NoOfThreads],
            [ZipTrg],
            [OnErrorResume],
            [PostInvokeAlias],
            [DeactivateFromBatch],
            [FlowType],
            [FromObjectMK],
            [ToObjectMK],
            [CreatedBy],
            [CreatedDate]
        )
        SELECT [SysAlias],
               [Batch],
               [srcServer],
               CASE
                   WHEN RIGHT([srcDBSchTbl], 1) = ']' THEN
                       SUBSTRING([srcDBSchTbl], 1, LEN([srcDBSchTbl]) - 1) + FORMAT(GETDATE(), 'ddhhss') + ']'
                   ELSE
                       [srcDBSchTbl] + FORMAT(GETDATE(), 'ddhhss')
               END [srcDBSchTbl],
               [IncrementalColumn],
               [NoOfOverlapDays],
               [DateColumn],
               [FromDate],
               [ToDate],
               [ExportBy],
               [ExportSize],
               [ServicePrincipalAlias],
               [trgPath],
               [trgFileName] + FORMAT(GETDATE(), 'ddhhss') [trgFileName],
               [trgFiletype],
               [trgEncoding],
               [ColumnDelimiter],
               [TextQualifier],
               [NoOfThreads],
               [ZipTrg],
               [OnErrorResume],
               [PostInvokeAlias],
               [DeactivateFromBatch],
               [FlowType],
               NULL [FromObjectMK],
               NULL [ToObjectMK],
               [CreatedBy],
               [CreatedDate]
        FROM [flw].[Export]
        WHERE FlowID = @FlowID;


    END;


    ELSE IF @FlowType IN ( 'sp' )
    BEGIN
        PRINT @FlowType;
        --@CloneObjName as [trgDBSchTbl]
        INSERT INTO [flw].[StoredProcedure]
        (
            [SysAlias],
            [Batch],
            [trgServer],
            [trgDBSchSP],
            [OnErrorResume],
            [PostInvokeAlias],
            [Description],
            [FlowType],
            [DeactivateFromBatch],
            [FromObjectMK],
            [ToObjectMK],
            [CreatedBy],
            [CreatedDate]
        )
        SELECT [SysAlias],
               [Batch],
               [trgServer],
               @CloneObjName AS [trgDBSchSP],
               [OnErrorResume],
               [PostInvokeAlias],
               [Description],
               [FlowType],
               [DeactivateFromBatch],
               NULL [FromObjectMK],
               NULL [ToObjectMK],
               [CreatedBy],
               [CreatedDate]
        FROM [flw].[StoredProcedure]
        WHERE FlowID = @FlowID;

        SELECT @NewFlowID = FlowID
        FROM [flw].[StoredProcedure]
        WHERE [trgDBSchSP] = @CloneObjName;

        IF (@Extended = 1)
        BEGIN
            INSERT INTO [flw].[Parameter]
            (
                [FlowID],
                [ParamAltServer],
                [ParamName],
                [SelectExp],
                [PreFetch]
            )
            SELECT @NewFlowID AS [FlowID],
                   [ParamAltServer],
                   [ParamName],
                   [SelectExp],
                   [PreFetch]
            FROM [flw].[Parameter]
            WHERE FlowID = @FlowID
            ORDER BY [ParameterID] ASC;
        END;

    END;
	

    ELSE IF @FlowType IN ( 'inv', 'adf', 'aut','cs' )
    BEGIN
        PRINT @FlowType;
        --@CloneObjName as [trgDBSchTbl]
        INSERT INTO [flw].[Invoke]
        (
            [Batch],
            [SysAlias],
            trgServicePrincipalAlias,
            [InvokeAlias],
            [InvokeType],
            [InvokePath],
            [InvokeFile],
            Code,
            [Arguments],
            [PipelineName],
            [RunbookName],
            [ParameterJSON],
            [OnErrorResume],
            [DeactivateFromBatch],
            [ToObjectMK],
            [CreatedBy],
            [CreatedDate]
        )
        SELECT [Batch],
               [SysAlias],
			   trgServicePrincipalAlias,
               [InvokeAlias] + +FORMAT(GETDATE(), 'ddhhss') AS [InvokeAlias],
               [InvokeType],
               [InvokePath],
               [InvokeFile],
               Code,
               [Arguments],
               [PipelineName],
               [RunbookName],
               [ParameterJSON],
               [OnErrorResume],
               [DeactivateFromBatch],
               NULL AS [ToObjectMK],
               [CreatedBy],
               [CreatedDate]
        FROM [flw].[Invoke]
        WHERE FlowID = @FlowID;
    END;


    EXEC [flw].[SyncSysLog];

    SELECT MAX(FlowID) AS FlowID,
           [flw].[GetFlowTypeUiUrl](@FlowType) + CAST(MAX(FlowID) AS VARCHAR(255)) AS EditUrl
    FROM [flw].[FlowDS];
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   Clones a specific flow based on the provided FlowID and, depending on the value of the @Extended parameter, clones additional related information.
  -- Summary			:	The stored procedure handles different types of flows, such as ''csv'', ''ADO'', ''xls'', ''xml'', and ''ing''. For each flow type, 
							it selects the relevant data from the corresponding source table and inserts it into the destination table with the new cloned object name. 
							If the @Extended parameter is set to 1, it also clones related transformation and virtual column data for the flow.

							The main steps of the stored procedure are as follows:

							Get the flow type, target object name, and clone object name based on the input @FlowID.
							Based on the flow type, perform the following actions:
							a. Print the flow type.
							b. Insert the cloned data into the relevant destination table with the new cloned object name.
							c. If @Extended is set to 1, clone the related transformation and virtual column data for the flow.
							The procedure is used to clone an existing flow and its associated metadata, which might be helpful when you want to create a new flow with similar properties or settings as an existing one.', 'SCHEMA', N'flw', 'PROCEDURE', N'CloneFlow', NULL, NULL
GO
