SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE PROCEDURE [flw].[InitPipeline]
    @FlowType VARCHAR(50) = '',
    @BaseFlowId INT = 0
AS
BEGIN

    --DECLARE @FlowType VARCHAR(50) = 'csv';
    --DECLARE @BaseFlowId INT = 1263

    DECLARE @BaseFlowType VARCHAR(50) = '';
    DECLARE @BaseBatch NVARCHAR(255) = N'';
    DECLARE @BaseSysAlias NVARCHAR(255) = N'';
    DECLARE @BaseSrcServer NVARCHAR(255) = N'';
    DECLARE @BaseSrcObj NVARCHAR(255) = N'';

    DECLARE @BaseSrcDbName NVARCHAR(255) = N'';
    DECLARE @BaseSrcSchName NVARCHAR(255) = N'';
    DECLARE @BaseSrcObjName NVARCHAR(255) = N'';
    DECLARE @BaseKeyColumns NVARCHAR(1024) = N'';
    DECLARE @BaseDateColumn NVARCHAR(255) = N'';

    DECLARE @BaseTrgServer NVARCHAR(255) = N'';
    DECLARE @BaseTrgObj NVARCHAR(255) = N'';
    DECLARE @BaseTrgObjPKName NVARCHAR(255) = N'';
    DECLARE @ObjStamp NVARCHAR(255) = FORMAT(GETDATE(), 'ddhhss');
    DECLARE @NewSurrogateKeyID INT = 0;
    DECLARE @MatchKeyID INT = 0;

    SELECT @BaseFlowType = FlowType,
           @BaseBatch = Batch,
           @BaseSysAlias = SysAlias,
           @BaseSrcServer = srcServer,
           @BaseSrcObj
               = QUOTENAME(PARSENAME(trgDBSchObj, 3)) + N'.' + QUOTENAME(PARSENAME(trgDBSchObj, 2)) + N'.'
                 + QUOTENAME(IIF(FlowType = 'ing', '', 'v_') + PARSENAME(trgDBSchObj, 1)),
           @BaseTrgServer = trgServer,
           @BaseTrgObj = trgDBSchObj,
           @BaseTrgObjPKName = QUOTENAME(PARSENAME(trgDBSchObj, 1) + @ObjStamp + 'PK'),
           @BaseSrcDbName = QUOTENAME(PARSENAME(srcDBSchObj, 3)),
           @BaseSrcSchName = QUOTENAME(PARSENAME(srcDBSchObj, 2)),
           @BaseSrcObjName = QUOTENAME(PARSENAME(srcDBSchObj, 1)),
           @BaseKeyColumns = [KeyColumns],
           @BaseDateColumn = [DateColumn]
    FROM [flw].[FlowDS]
    WHERE FlowID = @BaseFlowId;

    --SELECT @BaseTrgDBSchObj

    IF @FlowType IN ( 'csv' )
    BEGIN
        PRINT @FlowType;

        INSERT [flw].[PreIngestionCSV]
        (
            [Batch],
            [SysAlias],
            [ServicePrincipalAlias],
            [srcPath],
            [srcFile],
            [SearchSubDirectories],
            [ColumnDelimiter],
            [TextQualifier],
            [FirstRowHasHeader],
            [IncludeFileLineNumber],
            [trgServer],
            [trgDBSchTbl],
            [SyncSchema],
            [ExpectedColumnCount],
            [DefaultColDataType],
            [FetchDataTypes],
            [OnErrorResume],
            [ShowPathWithFileName],
            [MaxBufferSize],
            [NoOfThreads],
            [DeactivateFromBatch],
            [CreatedBy],
            [CreatedDate]
        )
        SELECT [DefaultBatch] AS [Batch],
               [DefaultSysAlias] AS [SysAlias],
               [DefaultPreIngServicePrincipalAlias] AS [ServicePrincipalAlias],
               [DefaultPreIngSrcPath] AS [srcPath],
               [DefaultPreIngSrcFileName] + '.csv' AS [srcFile],
               1 AS [SearchSubDirectories],
               N';' AS [ColumnDelimiter],
               '"' AS [TextQualifier],
               1 AS [FirstRowHasHeader],
               0 AS [IncludeFileLineNumber],
               [DefaultPreIngTrgServer] AS [trgServer],
               [DefaultPreIngTrgDbName] + '.' + [DefaultPreIngTrgSchema] + '.'
               + CASE
                     WHEN RIGHT([DefaultPreIngTrgTable], 1) = ']' THEN
                         SUBSTRING([DefaultPreIngTrgTable], 1, LEN([DefaultPreIngTrgTable]) - 1)
                         + FORMAT(GETDATE(), 'ddhhss') + ']'
                     ELSE
                         [DefaultPreIngTrgTable] + FORMAT(GETDATE(), 'ddhhss')
                 END AS [trgDBSchTbl],
               1 AS [SyncSchema],
               0 AS [ExpectedColumnCount],
               [flw].[GetCFGParamVal]('DefaultColDataType') AS [DefaultColDataType],
               1 AS [FetchDataTypes],
               1 AS [OnErrorResume],
               0 AS [ShowPathWithFileName],
               1024 AS [MaxBufferSize],
               1 AS [NoOfThreads],
               0 AS [DeactivateFromBatch],
               'SQLFlowInit' AS [CreatedBy],
               GETDATE() AS [CreatedDate]
        FROM [flw].[SysDefaultInitPipelineValues];

    END;

    ELSE IF @FlowType IN ( 'xls' )
    BEGIN
        PRINT @FlowType;

        INSERT [flw].[PreIngestionXLS]
        (
            [Batch],
            [SysAlias],
            [ServicePrincipalAlias],
            [srcPath],
            [srcFile],
            [SearchSubDirectories],
            [SheetName],
            [SheetRange],
            [UseSheetIndex],
            [FirstRowHasHeader],
            [IncludeFileLineNumber],
            [trgServer],
            [trgDBSchTbl],
            [SyncSchema],
            [ExpectedColumnCount],
            [DefaultColDataType],
            [FetchDataTypes],
            [OnErrorResume],
            [ShowPathWithFileName],
            [NoOfThreads],
            [DeactivateFromBatch],
            [CreatedBy],
            [CreatedDate]
        )
        SELECT [DefaultBatch] AS [Batch],
               [DefaultSysAlias] AS [SysAlias],
               [DefaultPreIngServicePrincipalAlias] AS [ServicePrincipalAlias],
               [DefaultPreIngSrcPath] AS [srcPath],
               [DefaultPreIngSrcFileName] + '.xlsx' AS [srcFile],
               1 AS [SearchSubDirectories],
               N'0' AS [SheetName],
               N'A1:P9999' AS [SheetRange],
               1 AS [UseSheetIndex],
               1 AS [FirstRowHasHeader],
               0 AS [IncludeFileLineNumber],
               [DefaultPreIngTrgServer] AS [trgServer],
               [DefaultPreIngTrgDbName] + '.' + [DefaultPreIngTrgSchema] + '.'
               + CASE
                     WHEN RIGHT([DefaultPreIngTrgTable], 1) = ']' THEN
                         SUBSTRING([DefaultPreIngTrgTable], 1, LEN([DefaultPreIngTrgTable]) - 1)
                         + FORMAT(GETDATE(), 'ddhhss') + ']'
                     ELSE
                         [DefaultPreIngTrgTable] + FORMAT(GETDATE(), 'ddhhss')
                 END AS [trgDBSchTbl],
               1 AS [SyncSchema],
               0 AS [ExpectedColumnCount],
               [flw].[GetCFGParamVal]('DefaultColDataType') AS [DefaultColDataType],
               1 AS [FetchDataTypes],
               1 AS [OnErrorResume],
               0 AS [ShowPathWithFileName],
               1 AS [NoOfThreads],
               0 AS [DeactivateFromBatch],
               'SQLFlowInit' AS [CreatedBy],
               GETDATE() AS [CreatedDate]
        FROM [flw].[SysDefaultInitPipelineValues];

    END;

    ELSE IF @FlowType IN ( 'prq' )
    BEGIN
        PRINT @FlowType;


        INSERT [flw].[PreIngestionPRQ]
        (
            [Batch],
            [SysAlias],
            [ServicePrincipalAlias],
            [srcPath],
            [srcFile],
            [SearchSubDirectories],
            [trgServer],
            [trgDBSchTbl],
            [SyncSchema],
            [ExpectedColumnCount],
            [DefaultColDataType],
            [FetchDataTypes],
            [OnErrorResume],
            [ShowPathWithFileName],
            [NoOfThreads],
            [DeactivateFromBatch],
            [CreatedBy],
            [CreatedDate]
        )
        SELECT [DefaultBatch] AS [Batch],
               [DefaultSysAlias] AS [SysAlias],
               [DefaultPreIngServicePrincipalAlias] AS [ServicePrincipalAlias],
               [DefaultPreIngSrcPath] AS [srcPath],
               [DefaultPreIngSrcFileName] + '.parquet' AS [srcFile],
               1 AS [SearchSubDirectories],
               [DefaultPreIngTrgServer] AS [trgServer],
               [DefaultPreIngTrgDbName] + '.' + [DefaultPreIngTrgSchema] + '.'
               + CASE
                     WHEN RIGHT([DefaultPreIngTrgTable], 1) = ']' THEN
                         SUBSTRING([DefaultPreIngTrgTable], 1, LEN([DefaultPreIngTrgTable]) - 1)
                         + FORMAT(GETDATE(), 'ddhhss') + ']'
                     ELSE
                         [DefaultPreIngTrgTable] + FORMAT(GETDATE(), 'ddhhss')
                 END AS [trgDBSchTbl],
               1 AS [SyncSchema],
               0 AS [ExpectedColumnCount],
               [flw].[GetCFGParamVal]('DefaultColDataType') AS [DefaultColDataType],
               1 AS [FetchDataTypes],
               1 AS [OnErrorResume],
               0 AS [ShowPathWithFileName],
               1 AS [NoOfThreads],
               0 AS [DeactivateFromBatch],
               'SQLFlowInit' AS [CreatedBy],
               GETDATE() AS [CreatedDate]
        FROM [flw].[SysDefaultInitPipelineValues];

    END;

    ELSE IF @FlowType IN ( 'prc' )
    BEGIN
        PRINT @FlowType;

        INSERT [flw].[PreIngestionPRC]
        (
            [Batch],
            [SysAlias],
            [ServicePrincipalAlias],
            [srcPath],
            [srcFile],
            [trgServer],
            [trgDBSchTbl],
            [SyncSchema],
            [ExpectedColumnCount],
            [FetchDataTypes],
            [OnErrorResume],
            [ShowPathWithFileName],
            [NoOfThreads],
            [DeactivateFromBatch],
            [CreatedBy],
            [CreatedDate]
        )
        SELECT [DefaultBatch] AS [Batch],
               [DefaultSysAlias] AS [SysAlias],
               [DefaultPreIngServicePrincipalAlias] AS [ServicePrincipalAlias],
               [DefaultPreIngSrcPath] AS [srcPath],
               [DefaultPreIngSrcFileName] + '.sql' AS [srcFile],
               [DefaultPreIngTrgServer] AS [trgServer],
               [DefaultPreIngTrgDbName] + '.' + [DefaultPreIngTrgSchema] + '.'
               + CASE
                     WHEN RIGHT([DefaultPreIngTrgTable], 1) = ']' THEN
                         SUBSTRING([DefaultPreIngTrgTable], 1, LEN([DefaultPreIngTrgTable]) - 1)
                         + FORMAT(GETDATE(), 'ddhhss') + ']'
                     ELSE
                         [DefaultPreIngTrgTable] + FORMAT(GETDATE(), 'ddhhss')
                 END AS [trgDBSchTbl],
               1 AS [SyncSchema],
               0 AS [ExpectedColumnCount],
               1 AS [FetchDataTypes],
               1 AS [OnErrorResume],
               0 AS [ShowPathWithFileName],
               1 AS [NoOfThreads],
               0 AS [DeactivateFromBatch],
               'SQLFlowInit' AS [CreatedBy],
               GETDATE() AS [CreatedDate]
        FROM [flw].[SysDefaultInitPipelineValues];

    END;

    ELSE IF @FlowType IN ( 'jsn' )
    BEGIN
        PRINT @FlowType;

        INSERT [flw].[PreIngestionJSN]
        (
            [Batch],
            [SysAlias],
            [ServicePrincipalAlias],
            [srcPath],
            [srcFile],
            [SearchSubDirectories],
            [JsonToDataTableCode],
            [trgServer],
            [trgDBSchTbl],
            [SyncSchema],
            [ExpectedColumnCount],
            [DefaultColDataType],
            [FetchDataTypes],
            [OnErrorResume],
            [ShowPathWithFileName],
            [NoOfThreads],
            [DeactivateFromBatch],
            [CreatedBy],
            [CreatedDate]
        )
        SELECT [DefaultBatch] AS [Batch],
               [DefaultSysAlias] AS [SysAlias],
               [DefaultPreIngServicePrincipalAlias] AS [ServicePrincipalAlias],
               [DefaultPreIngSrcPath] AS [srcPath],
               [DefaultPreIngSrcFileName] + '.json' AS [srcFile],
               1 AS [SearchSubDirectories],
               N'public static DataTable ToDataTable(string json){
			return JsonConvert.DeserializeObject<DataTable>(JArray.Parse(json).ToString());
			}' AS [JsonToDataTableCode],
               [DefaultPreIngTrgServer] AS [trgServer],
               [DefaultPreIngTrgDbName] + '.' + [DefaultPreIngTrgSchema] + '.'
               + CASE
                     WHEN RIGHT([DefaultPreIngTrgTable], 1) = ']' THEN
                         SUBSTRING([DefaultPreIngTrgTable], 1, LEN([DefaultPreIngTrgTable]) - 1)
                         + FORMAT(GETDATE(), 'ddhhss') + ']'
                     ELSE
                         [DefaultPreIngTrgTable] + FORMAT(GETDATE(), 'ddhhss')
                 END AS [trgDBSchTbl],
               1 AS [SyncSchema],
               0 AS [ExpectedColumnCount],
               [flw].[GetCFGParamVal]('DefaultColDataType') AS [DefaultColDataType],
               1 AS [FetchDataTypes],
               1 AS [OnErrorResume],
               0 AS [ShowPathWithFileName],
               1 AS [NoOfThreads],
               0 AS [DeactivateFromBatch],
               'SQLFlowInit' AS [CreatedBy],
               GETDATE() AS [CreatedDate]
        FROM [flw].[SysDefaultInitPipelineValues];

    END;
    ELSE IF @FlowType IN ( 'xml' )
    BEGIN
        PRINT @FlowType;

        INSERT [flw].[PreIngestionXML]
        (
            [Batch],
            [SysAlias],
            [ServicePrincipalAlias],
            [srcPath],
            [srcFile],
            [SearchSubDirectories],
            [hierarchyIdentifier],
            [XmlToDataTableCode],
            [trgServer],
            [trgDBSchTbl],
            [SyncSchema],
            [ExpectedColumnCount],
            [DefaultColDataType],
            [FetchDataTypes],
            [OnErrorResume],
            [ShowPathWithFileName],
            [NoOfThreads],
            [DeactivateFromBatch],
            [CreatedBy],
            [CreatedDate]
        )
        SELECT [DefaultBatch] AS [Batch],
               [DefaultSysAlias] AS [SysAlias],
               [DefaultPreIngServicePrincipalAlias] AS [ServicePrincipalAlias],
               [DefaultPreIngSrcPath] AS [srcPath],
               [DefaultPreIngSrcFileName] + '.xml' AS [srcFile],
               1 AS [SearchSubDirectories],
               N'StartNodeNameForXmlFlattening' AS [hierarchyIdentifier],
               '[XmlToDataTableCode]' AS [XmlToDataTableCode],
               [DefaultPreIngTrgServer] AS [trgServer],
               [DefaultPreIngTrgDbName] + '.' + [DefaultPreIngTrgSchema] + '.'
               + CASE
                     WHEN RIGHT([DefaultPreIngTrgTable], 1) = ']' THEN
                         SUBSTRING([DefaultPreIngTrgTable], 1, LEN([DefaultPreIngTrgTable]) - 1)
                         + FORMAT(GETDATE(), 'ddhhss') + ']'
                     ELSE
                         [DefaultPreIngTrgTable] + FORMAT(GETDATE(), 'ddhhss')
                 END AS [trgDBSchTbl],
               1 AS [SyncSchema],
               0 AS [ExpectedColumnCount],
               [flw].[GetCFGParamVal]('DefaultColDataType') AS [DefaultColDataType],
               1 AS [FetchDataTypes],
               1 AS [OnErrorResume],
               0 AS [ShowPathWithFileName],
               1 AS [NoOfThreads],
               0 AS [DeactivateFromBatch],
               'SQLFlowInit' AS [CreatedBy],
               GETDATE() AS [CreatedDate]
        FROM [flw].[SysDefaultInitPipelineValues];


    END;
    ELSE IF @FlowType IN ( 'ado' )
    BEGIN
        PRINT @FlowType;

        INSERT [flw].[PreIngestionADO]
        (
            [Batch],
            [SysAlias],
            [srcServer],
            [srcDatabase],
            [srcSchema],
            [srcObject],
            [trgServer],
            [trgDBSchTbl],
            [srcFilterIsAppend],
            [NoOfOverlapDays],
            [SyncSchema],
            [OnErrorResume],
            [NoOfThreads],
            [DeactivateFromBatch],
            [CreatedBy],
            [CreatedDate]
        )
        SELECT [DefaultBatch] AS [Batch],
               [DefaultSysAlias] AS [SysAlias],
               'SelectYourAdoSource' AS [srcServer],
               'AdoDatabaseName' AS [srcDatabase],
               NULL AS [srcSchema],
               N'YourSourceObject' AS [srcObject],
               [DefaultPreIngTrgServer] AS [trgServer],
               [DefaultPreIngTrgDbName] + '.' + [DefaultPreIngTrgSchema] + '.'
               + CASE
                     WHEN RIGHT([DefaultPreIngTrgTable], 1) = ']' THEN
                         SUBSTRING([DefaultPreIngTrgTable], 1, LEN([DefaultPreIngTrgTable]) - 1)
                         + FORMAT(GETDATE(), 'ddhhss') + ']'
                     ELSE
                         [DefaultPreIngTrgTable] + FORMAT(GETDATE(), 'ddhhss')
                 END AS [trgDBSchTbl],
               1 AS [srcFilterIsAppend],
               7 AS [NoOfOverlapDays],
               1 AS [SyncSchema],
               1 AS [OnErrorResume],
               1 AS [NoOfThreads],
               0 AS [DeactivateFromBatch],
               'SQLFlowInit' AS [CreatedBy],
               GETDATE() AS [CreatedDate]
        FROM [flw].[SysDefaultInitPipelineValues];


    END;


    ELSE IF @FlowType = 'skey'
    BEGIN
        PRINT @FlowType;

        INSERT [flw].[SurrogateKey]
        (
            [FlowID],
            [SurrogateServer],
            [SurrogateDbSchTbl],
            [SurrogateColumn],
            [KeyColumns],
            [sKeyColumns]
        )
        SELECT @BaseFlowId AS [FlowID],
               '' AS [SurrogateServer],
               QUOTENAME(PARSENAME(@BaseTrgObj, 3)) + '.' + QUOTENAME([flw].[GetCFGParamVal]('Schema13Skey')) + '.'
               + QUOTENAME(PARSENAME(@BaseTrgObj, 1)) AS [SurrogateDbSchTbl],
               N'[AddSurrogateColumnName_ID]' AS [SurrogateColumn],
               N'[AddColumnsForSurrogateKeyGeneration]' AS [IncrementalColumn],
               '' AS [sKeyColumns]
        FROM [flw].[SysDefaultInitPipelineValues];
        SELECT @NewSurrogateKeyID = @@IDENTITY;

    END;


    ELSE IF @FlowType = 'mkey'
    BEGIN
        PRINT @FlowType;

        INSERT [flw].[MatchKey]
        (
            FlowId,
            [Batch],
            [SysAlias],
            [srcServer],
            [srcDatabase],
            [srcSchema],
            [srcObject],
            [trgServer],
            [trgDBSchTbl],
            [DeactivateFromBatch],
            [KeyColumns],
            [DateColumn],
            [ActionType],
            [IgnoreDeletedRowsAfter],
            [srcFilter],
            [OnErrorResume],
            [CreatedBy],
            [CreatedDate]
        )
        SELECT @BaseFlowId,
               CASE
                   WHEN LEN(@BaseBatch) > 0 THEN
                       @BaseBatch
                   ELSE
                       [DefaultBatch]
               END AS [Batch],
               CASE
                   WHEN LEN(@BaseSysAlias) > 0 THEN
                       @BaseSysAlias
                   ELSE
                       [DefaultSysAlias]
               END AS [SysAlias],
               CASE
                   WHEN LEN(@BaseSrcServer) > 0 THEN
                       @BaseSrcServer
                   ELSE
                       [DefaultSrcServer]
               END AS [srcServer],
               CASE
                   WHEN LEN(@BaseSrcDbName) > 0 THEN
                       @BaseSrcDbName
                   ELSE
                       '[SourceDbName]'
               END AS [srcDatabase],
               CASE
                   WHEN LEN(@BaseSrcSchName) > 0 THEN
                       @BaseSrcSchName
                   ELSE
                       '[SourceSchemaName]'
               END AS [srcSchema],
               CASE
                   WHEN LEN(@BaseSrcObjName) > 0 THEN
                       @BaseSrcObjName
                   ELSE
                       '[SourceObjectName]'
               END AS [srcObject],
               CASE
                   WHEN LEN(@BaseTrgServer) > 0 THEN
                       @BaseTrgServer
                   ELSE
                       [DefaultTrgServer]
               END AS [trgServer],
               CASE
                   WHEN LEN(@BaseTrgObj) > 0 THEN
                       @BaseTrgObj
                   ELSE
                       '[TargetObjectName]'
               END AS [trgDBSchTbl],
               0 AS [DeactivateFromBatch],
               CASE
                   WHEN LEN(@BaseKeyColumns) > 0 THEN
                       @BaseKeyColumns
                   ELSE
                       '[KeyColumns]'
               END [KeyColumns],
               CASE
                   WHEN LEN(@BaseDateColumn) > 0 THEN
                       @BaseDateColumn
                   ELSE
                       '[DateColumn]'
               END [DateColumn],
               1 AS [ActionType],
               NULL [IgnoreDeletedRowsAfter],
               NULL AS [srcFilter],
               1 AS [OnErrorResume],
               'SQLFlowInit' AS [CreatedBy],
               GETDATE() AS [CreatedDate]
        FROM [flw].[SysDefaultInitPipelineValues];
        SELECT @MatchKeyID = @@IDENTITY;
    END;
    ELSE IF @FlowType = 'exp'
    BEGIN
        PRINT @FlowType;

        INSERT [flw].[Export]
        (
            [Batch],
            [SysAlias],
            [srcServer],
            [srcDBSchTbl],
            [IncrementalColumn],
            [NoOfOverlapDays],
            [ServicePrincipalAlias],
            [trgPath],
            [trgFileName],
            [trgFiletype],
            [ColumnDelimiter],
            [TextQualifier],
            [ExportBy],
            [ExportSize],
            [AddTimeStampToFileName],
            [Subfolderpattern],
            [NoOfThreads],
            [CompressionType],
            [OnErrorResume],
            [DeactivateFromBatch],
            [CreatedBy],
            [CreatedDate]
        )
        SELECT CASE
                   WHEN LEN(@BaseBatch) > 0 THEN
                       @BaseBatch
                   ELSE
                       [DefaultBatch]
               END AS [Batch],
               CASE
                   WHEN LEN(@BaseSysAlias) > 0 THEN
                       @BaseSysAlias
                   ELSE
                       [DefaultSysAlias]
               END AS [SysAlias],
               CASE
                   WHEN LEN(@BaseTrgServer) > 0 THEN
                       @BaseTrgServer
                   ELSE
                       [DefaultTrgServer]
               END AS [srcServer],
               CASE
                   WHEN LEN(@BaseTrgObj) > 0 THEN
                       @BaseTrgObj
                   ELSE
                       [DefaultTrgDbName] + '.' + [DefaultTrgSchema] + '.' + '[YourSourceTable]'
               END [srcDBSchTbl],
               N'[ColumnForIncrementalExport]' AS [IncrementalColumn],
               1 AS [NoOfOverlapDays],
               [DefaultTrgServicePrincipalAlias] AS [ServicePrincipalAlias],
               [DefaultTrgPath] AS [trgPath],
               [DefaultTrgFileName] AS [trgFileName],
               N'csv' AS [trgFiletype],
               N';' AS [ColumnDelimiter],
               '"' AS [TextQualifier],
               N'M' AS [ExportBy],
               1 AS [ExportSize],
               0 AS [AddTimeStampToFileName],
               N'YYYYMM' AS [Subfolderpattern],
               1 AS [NoOfThreads],
               'Gzip' AS [CompressionType],
               1 AS [OnErrorResume],
               0 AS [DeactivateFromBatch],
               'SQLFlowInit' AS [CreatedBy],
               GETDATE() AS [CreatedDate]
        FROM [flw].[SysDefaultInitPipelineValues];

    END;
    ELSE IF @FlowType = 'ing'
    BEGIN
        PRINT @FlowType;
        IF (LEN(ISNULL(@BaseBatch, '')) > 0)
        BEGIN
            INSERT [flw].[Ingestion]
            (
                [Batch],
                [SysAlias],
                [srcServer],
                [srcDBSchTbl],
                [trgServer],
                [trgDBSchTbl],
                [KeyColumns],
                [IncrementalColumns],
                [DataSetColumn],
                [SysColumns],
                [ColumnStoreIndexOnTrg],
                [IdentityColumn],
                [StreamData],
                [NoOfThreads],
                [srcFilterIsAppend],
                [NoOfOverlapDays],
                [FetchMinValuesFromSrc],
                [SkipUpdateExsisting],
                [SkipInsertNew],
                [trgVersioning],
                [InsertUnknownDimRow],
                [SyncSchema],
                [OnErrorResume],
                [DeactivateFromBatch],
                [CreatedBy],
                [CreatedDate]
            )
            SELECT CASE
                       WHEN LEN(@BaseBatch) > 0 THEN
                           @BaseBatch
                       ELSE
                           [DefaultBatch]
                   END AS [Batch],
                   CASE
                       WHEN LEN(@BaseSysAlias) > 0 THEN
                           @BaseSysAlias
                       ELSE
                           [DefaultSysAlias]
                   END AS [SysAlias],
                   CASE
                       WHEN LEN(@BaseTrgServer) > 0 THEN
                           @BaseTrgServer
                       ELSE
                           [DefaultSrcServer]
                   END AS [srcServer],
                   CASE
                       WHEN LEN(@BaseSrcObj) > 0 THEN
                           @BaseSrcObj
                       ELSE
                           [DefaultSrcDbName] + '.' + [DefaultSrcSchema] + '.'
                           + CASE
                                 WHEN RIGHT([DefaultSrcObject], 1) = ']' THEN
                                     SUBSTRING([DefaultSrcObject], 1, LEN([DefaultSrcObject]) - 1)
                                     + FORMAT(GETDATE(), 'ddhhss') + ']'
                                 ELSE
                                     [DefaultSrcObject] + FORMAT(GETDATE(), 'ddhhss')
                             END
                   END AS [srcDBSchTbl],
                   [DefaultTrgServer] AS [trgServer],
                   [DefaultTrgDbName] + '.' + [DefaultTrgSchema] + '.'
                   + CASE
                         WHEN LEN(@BaseTrgObj) > 0 THEN
                             QUOTENAME(PARSENAME(@BaseTrgObj, 1) + @ObjStamp)
                         ELSE
                             CASE
                                 WHEN RIGHT([DefaultTrgTable], 1) = ']' THEN
                                     SUBSTRING([DefaultTrgTable], 1, LEN([DefaultTrgTable]) - 1)
                                     + FORMAT(GETDATE(), 'ddhhss') + ']'
                                 ELSE
                                     [DefaultTrgTable] + FORMAT(GETDATE(), 'ddhhss')
                             END
                     END AS [trgDBSchTbl],
                   '[SourceKeyColumn]' [KeyColumns],
                   '[FileDate_DW]' [IncrementalColumns],
                   '[DataSet_DW]' AS [DataSetColumn],
                   N'InsertedDate_DW,UpdatedDate_DW' AS [SysColumns],
                   0 AS [ColumnStoreIndexOnTrg],
                   CASE
                       WHEN RIGHT([DefaultTrgTable], 1) = ']' THEN
                           SUBSTRING([DefaultTrgTable], 1, LEN([DefaultTrgTable]) - 1) + FORMAT(GETDATE(), 'ddhhss')
                           + 'PK]'
                       ELSE
                           [DefaultTrgTable] + FORMAT(GETDATE(), 'ddhhss') + 'PK'
                   END AS [IdentityColumn],
                   1 AS [StreamData],
                   1 AS [NoOfThreads],
                   1 AS [srcFilterIsAppend],
                   7 AS [NoOfOverlapDays],
                   0 AS [FetchMinValuesFromSrc],
                   0 AS [SkipUpdateExsisting],
                   0 AS [SkipInsertNew],
                   0 AS [trgVersioning],
                   0 AS [InsertUnknownDimRow],
                   1 AS [SyncSchema],
                   1 AS [OnErrorResume],
                   0 AS [DeactivateFromBatch],
                   'SQLFlowInit' AS [CreatedBy],
                   GETDATE() AS [CreatedDate]
            FROM [flw].[SysDefaultInitPipelineValues];
        END;
        ELSE
        BEGIN
            INSERT [flw].[Ingestion]
            (
                [Batch],
                [SysAlias],
                [srcServer],
                [srcDBSchTbl],
                [trgServer],
                [trgDBSchTbl],
                [KeyColumns],
                [IncrementalColumns],
                [DataSetColumn],
                [SysColumns],
                [ColumnStoreIndexOnTrg],
                [IdentityColumn],
                [StreamData],
                [NoOfThreads],
                [srcFilterIsAppend],
                [NoOfOverlapDays],
                [FetchMinValuesFromSrc],
                [SkipUpdateExsisting],
                [SkipInsertNew],
                [trgVersioning],
                [InsertUnknownDimRow],
                [SyncSchema],
                [OnErrorResume],
                [DeactivateFromBatch],
                [CreatedBy],
                [CreatedDate]
            )
            SELECT [DefaultBatch] AS [Batch],
                   [DefaultSysAlias] AS [SysAlias],
                   [DefaultSrcServer] AS [srcServer],
                   [DefaultSrcDbName] + '.' + [DefaultSrcSchema] + '.'
                   + CASE
                         WHEN RIGHT([DefaultSrcObject], 1) = ']' THEN
                             SUBSTRING([DefaultSrcObject], 1, LEN([DefaultSrcObject]) - 1)
                             + FORMAT(GETDATE(), 'ddhhss') + ']'
                         ELSE
                             [DefaultSrcObject] + FORMAT(GETDATE(), 'ddhhss')
                     END AS [srcDBSchTbl],
                   [DefaultTrgServer] AS [trgServer],
                   [DefaultTrgDbName] + '.' + [DefaultTrgSchema] + '.'
                   + CASE
                         WHEN RIGHT([DefaultTrgTable], 1) = ']' THEN
                             SUBSTRING([DefaultTrgTable], 1, LEN([DefaultTrgTable]) - 1) + FORMAT(GETDATE(), 'ddhhss')
                             + ']'
                         ELSE
                             [DefaultTrgTable] + FORMAT(GETDATE(), 'ddhhss')
                     END AS [trgDBSchTbl],
                   '[SourceKeyColumn]' [KeyColumns],
                   '[FileDate_DW]' [IncrementalColumns],
                   '[DataSet_DW]' AS [DataSetColumn],
                   N'InsertedDate_DW,UpdatedDate_DW' AS [SysColumns],
                   1 AS [ColumnStoreIndexOnTrg],
                   CASE
                       WHEN RIGHT([DefaultTrgTable], 1) = ']' THEN
                           SUBSTRING([DefaultTrgTable], 1, LEN([DefaultTrgTable]) - 1) + FORMAT(GETDATE(), 'ddhhss')
                           + 'PK]'
                       ELSE
                           [DefaultTrgTable] + FORMAT(GETDATE(), 'ddhhss') + 'PK'
                   END AS [IdentityColumn],
                   1 AS [StreamData],
                   1 AS [NoOfThreads],
                   1 AS [srcFilterIsAppend],
                   7 AS [NoOfOverlapDays],
                   0 AS [FetchMinValuesFromSrc],
                   0 AS [SkipUpdateExsisting],
                   0 AS [SkipInsertNew],
                   0 AS [trgVersioning],
                   0 AS [InsertUnknownDimRow],
                   1 AS [SyncSchema],
                   1 AS [OnErrorResume],
                   0 AS [DeactivateFromBatch],
                   'SQLFlowInit' AS [CreatedBy],
                   GETDATE() AS [CreatedDate]
            FROM [flw].[SysDefaultInitPipelineValues];

        END;

    END;




    ELSE IF @FlowType = 'ps'
    BEGIN
        PRINT @FlowType;

        INSERT [flw].[Invoke]
        (
            [Batch],
            [SysAlias],
            trgServicePrincipalAlias,
            [InvokeAlias],
            [InvokeType],
            [InvokePath],
            [InvokeFile],
            Code,
            [OnErrorResume],
            [DeactivateFromBatch],
            [CreatedBy],
            [CreatedDate]
        )
        SELECT [DefaultBatch] AS [Batch],
               [DefaultSysAlias] AS [SysAlias],
               [DefaultSrcServicePrincipalAlias] AS [ServicePrincipalAlias],
               'ps_invoke_' + FORMAT(GETDATE(), 'ddhhss') AS [InvokeAlias],
               N'ps' AS [InvokeType],
               'PathToYourPSFile' AS [InvokePath],
               'YourPSFile' AS [InvokeFile],
               '' AS [Script],
               1 AS [OnErrorResume],
               0 AS [DeactivateFromBatch],
               'SQLFlowInit' AS [CreatedBy],
               GETDATE() AS [CreatedDate]
        FROM [flw].[SysDefaultInitPipelineValues];

    END;

    ELSE IF @FlowType = 'aut'
    BEGIN
        PRINT @FlowType;

        INSERT [flw].[Invoke]
        (
            [Batch],
            [SysAlias],
            trgServicePrincipalAlias,
            [InvokeAlias],
            [InvokeType],
            [RunbookName],
            [OnErrorResume],
            [DeactivateFromBatch],
            [CreatedBy],
            [CreatedDate]
        )
        SELECT [DefaultBatch] AS [Batch],
               [DefaultSysAlias] AS [SysAlias],
               [DefaultSrcServicePrincipalAlias] AS [ServicePrincipalAlias],
               'aut_invoke_' + FORMAT(GETDATE(), 'ddhhss') AS [InvokeAlias],
               N'aut' AS [InvokeType],
               'aut_your_runbookname' AS [RunbookName],
               1 AS [OnErrorResume],
               0 AS [DeactivateFromBatch],
               'SQLFlowInit' AS [CreatedBy],
               GETDATE() AS [CreatedDate]
        FROM [flw].[SysDefaultInitPipelineValues];

    END;

    ELSE IF @FlowType = 'adf'
    BEGIN
        PRINT @FlowType;

        INSERT [flw].[Invoke]
        (
            [Batch],
            [SysAlias],
            trgServicePrincipalAlias,
            [InvokeAlias],
            [InvokeType],
            PipelineName,
            [OnErrorResume],
            [DeactivateFromBatch],
            [CreatedBy],
            [CreatedDate]
        )
        SELECT [DefaultBatch] AS [Batch],
               [DefaultSysAlias] AS [SysAlias],
               [DefaultSrcServicePrincipalAlias] AS [ServicePrincipalAlias],
               'adf_invoke_' + FORMAT(GETDATE(), 'ddhhss') AS [InvokeAlias],
               N'adf' AS [InvokeType],
               'adf_your_pipelinename' AS PipelineName,
               1 AS [OnErrorResume],
               0 AS [DeactivateFromBatch],
               'SQLFlowInit' AS [CreatedBy],
               GETDATE() AS [CreatedDate]
        FROM [flw].[SysDefaultInitPipelineValues];

    END;

    ELSE IF @FlowType = 'cs'
    BEGIN
        PRINT @FlowType;

        INSERT [flw].[Invoke]
        (
            [Batch],
            [SysAlias],
            trgServicePrincipalAlias,
            [InvokeAlias],
            [InvokeType],
            [RunbookName],
            [OnErrorResume],
            [DeactivateFromBatch],
            [CreatedBy],
            [CreatedDate]
        )
        SELECT [DefaultBatch] AS [Batch],
               [DefaultSysAlias] AS [SysAlias],
               [DefaultSrcServicePrincipalAlias] AS [ServicePrincipalAlias],
               'cs_invoke_' + FORMAT(GETDATE(), 'ddhhss') AS [InvokeAlias],
               N'cs' AS [InvokeType],
               'aut_your_runbookname' AS [RunbookName],
               1 AS [OnErrorResume],
               0 AS [DeactivateFromBatch],
               'SQLFlowInit' AS [CreatedBy],
               GETDATE() AS [CreatedDate]
        FROM [flw].[SysDefaultInitPipelineValues];

    END;


    ELSE IF @FlowType = 'sp'
    BEGIN
        PRINT @FlowType;

        INSERT [flw].[StoredProcedure]
        (
            [Batch],
            [SysAlias],
            [trgServer],
            [trgDBSchSP],
            [OnErrorResume],
            [DeactivateFromBatch],
            [CreatedBy],
            [CreatedDate]
        )
        SELECT [DefaultBatch] AS [Batch],
               [DefaultSysAlias] AS [SysAlias],
               [DefaultTrgServer] AS [trgServer],
               [DefaultTrgDbName] + '.' + [DefaultTrgSchema] + '.' + '[NameOfYourSP]' + FORMAT(GETDATE(), 'ddhhss') AS [trgDBSchSP],
               1 AS [OnErrorResume],
               0 AS [DeactivateFromBatch],
               'SQLFlowInit' AS [CreatedBy],
               GETDATE() AS [CreatedDate]
        FROM [flw].[SysDefaultInitPipelineValues];

    END;


    EXEC [flw].[SyncSysLog];



    SELECT ISNULL(MAX(FlowID), 0) AS FlowID,
           [flw].[GetFlowTypeUiUrl](@FlowType)
           + CAST(COALESCE(@MatchKeyID, @NewSurrogateKeyID, MAX(FlowID), 0) AS VARCHAR(MAX)) AS EditUrl,
           ISNULL(@NewSurrogateKeyID, 0) AS SurrogateKeyID,
           ISNULL(@MatchKeyID, 0) AS MatchKeyID
    FROM [flw].[FlowDS];
END;
GO
