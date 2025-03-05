CREATE TABLE [flw].[PreIngestionCSV]
(
[FlowID] [int] NOT NULL CONSTRAINT [DF_PreIngestionCSV_FlowID] DEFAULT (NEXT VALUE FOR [flw].[FlowID]),
[Batch] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SysAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[ServicePrincipalAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcPath] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[srcPathMask] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcFile] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SearchSubDirectories] [bit] NULL CONSTRAINT [DF_PreIngestionCSV_SearchSubDirectories] DEFAULT ((0)),
[copyToPath] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcDeleteIngested] [bit] NULL CONSTRAINT [DF_PreIngestionCSV_srcDeleteIngested] DEFAULT ((0)),
[srcDeleteAtPath] [bit] NULL CONSTRAINT [DF_PreIngestionCSV_srcDelete] DEFAULT ((0)),
[zipToPath] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[trgServer] [nvarchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[trgDBSchTbl] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[trgDesiredIndex] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[preFilter] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ColumnDelimiter] [nvarchar] (2) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[TextQualifier] [nvarchar] (2) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ColumnWidths] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FirstRowHasHeader] [bit] NULL CONSTRAINT [DF_PreIngestionCSV_FirstRowHasHeader] DEFAULT ((1)),
[SyncSchema] [bit] NULL CONSTRAINT [DF_PreIngestionCSV_SyncSchema] DEFAULT ((1)),
[ExpectedColumnCount] [int] NULL CONSTRAINT [DF_PreIngestionCSV_ExpectedColumnCount] DEFAULT ((0)),
[DefaultColDataType] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FetchDataTypes] [bit] NULL CONSTRAINT [DF_PreIngestionCSV_FetchDataTypes] DEFAULT ((0)),
[OnErrorResume] [bit] NULL CONSTRAINT [DF_PreIngestionCSV_OnErrorResume] DEFAULT ((1)),
[SkipStartingDataRows] [int] NULL,
[SkipEmptyRows] [bit] NULL CONSTRAINT [DF_PreIngestionCSV_SkipEmptyRows] DEFAULT ((1)),
[IncludeFileLineNumber] [bit] NULL CONSTRAINT [DF_PreIngestionCSV_IncludeFileLineNumber] DEFAULT ((0)),
[TrimResults] [bit] NULL CONSTRAINT [DF_PreIngestionCSV_TrimResults] DEFAULT ((0)),
[StripControlChars] [bit] NULL,
[FirstRowSetsExpectedColumnCount] [bit] NULL CONSTRAINT [DF_PreIngestionCSV_FirstRowSetsExpectedColumnCount] DEFAULT ((0)),
[EscapeCharacter] [nvarchar] (2) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_PreIngestionCSV_EscapeCharacter] DEFAULT ((0)),
[CommentCharacter] [nvarchar] (2) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SkipEndingDataRows] [int] NULL CONSTRAINT [DF_PreIngestionCSV_SkipEndingDataRows] DEFAULT ((0)),
[MaxBufferSize] [int] NULL CONSTRAINT [DF__PreIngest__MaxBu__789EE131] DEFAULT ((1024)),
[MaxRows] [int] NULL CONSTRAINT [DF__PreIngest__MaxRo__7993056A] DEFAULT ((0)),
[srcEncoding] [nvarchar] (25) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[NoOfThreads] [int] NULL CONSTRAINT [DF_PreIngestionCSV_Parallelize] DEFAULT ((4)),
[InitFromFileDate] [varchar] (15) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[InitToFileDate] [varchar] (15) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PreProcessOnTrg] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PostProcessOnTrg] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PreInvokeAlias] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[BatchOrderBy] [int] NULL CONSTRAINT [DF_PreIngestionCSV_BatchOrderBy] DEFAULT ((0)),
[DeactivateFromBatch] [bit] NULL CONSTRAINT [DF_PreIngestionCSV_DeactivateFromBatch] DEFAULT ((0)),
[EnableEventExecution] [bit] NULL CONSTRAINT [DF_PreIngestionCSV_EnableEventExecution] DEFAULT ((0)),
[FlowType] [varchar] (25) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL CONSTRAINT [DF_PreIngestionCSV_FlowType] DEFAULT (N'csv'),
[FromObjectMK] [int] NULL,
[ToObjectMK] [int] NULL,
[ShowPathWithFileName] [bit] NULL CONSTRAINT [DF_PreIngestionCSV_ShowPathWithFileName] DEFAULT ((0)),
[CreatedBy] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_PreIngestionCSV_CreatedBy] DEFAULT (suser_sname()),
[CreatedDate] [datetime] NULL CONSTRAINT [DF_PreIngestionCSV_CreatedDate] DEFAULT (getdate())
)
GO
SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE TRIGGER [flw].[AddPreFlowToLogTable] ON [flw].[PreIngestionCSV]
FOR INSERT, UPDATE
AS
BEGIN
	-- Handling INSERT operations
    IF (SELECT COUNT(*) FROM inserted) > 0 AND (SELECT COUNT(*) FROM deleted) = 0
    BEGIN
        INSERT INTO [flw].[SysLog] ([FlowID], [FlowType], [Process], [ProcessShort])
                SELECT FlowID, FlowType,   'CSV -->' + [trgServer] + '.' + [trgDBSchTbl] as [Process], 'CSV -->' + [trgServer] + '.' + [trgDBSchTbl] as [ProcessShort]
        FROM inserted
    END

    -- Handling UPDATE operations
    IF (SELECT COUNT(*) FROM inserted) > 0 AND (SELECT COUNT(*) FROM deleted) > 0
    BEGIN
        UPDATE [flw].[SysLog]
        SET [FlowType] = i.[FlowType],
            [Process] = 'CSV -->' + i.[trgServer] + '.' + i.[trgDBSchTbl],
			[ProcessShort] = 'CSV -->' + i.[trgServer] + '.' + i.[trgDBSchTbl]
        FROM inserted i
        INNER JOIN [flw].[SysLog] sl ON sl.[FlowID] = i.[FlowID]; 
    END
END
GO
ALTER TABLE [flw].[PreIngestionCSV] ADD CONSTRAINT [Chk_trgDBSchTbl_CSV] CHECK ((parsename([trgDBSchTbl],(3)) IS NOT NULL))
GO
ALTER TABLE [flw].[PreIngestionCSV] ADD CONSTRAINT [PK_PreIngestionCSV] PRIMARY KEY CLUSTERED ([FlowID])
GO
CREATE NONCLUSTERED INDEX [NCI_FlowID] ON [flw].[PreIngestionCSV] ([FlowID]) INCLUDE ([Batch], [SysAlias], [DeactivateFromBatch])
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[PreIngestionCSV] provides metadata for ingesting CSV files into a SQL Server database. It contains columns about the source and destination paths, file names, server, and database information, column delimiters and qualifiers, filters, and other processing options. There are also columns that allow for control over the ingestion process, such as batching and concurrency settings. ', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', 'The Batch column of [flw].[PreIngestionCSV] table stores the name of the batch to which the ingestion process belongs. This column can be used to group multiple ingestion processes into a single batch. The default value for this column is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'Batch'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The BatchOrderBy column of [flw].[PreIngestionCSV] table stores an integer value that specifies the order in which the ingestion processes within a batch should be executed. This column can be used to specify the order of execution for ingestion processes in a batch. The default value for this column is 0.
', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'BatchOrderBy'
GO
EXEC sp_addextendedproperty N'MS_Description', 'ColumnDelimiter column stores the delimiter used to separate columns in the CSV file. This column is used to correctly parse the CSV file and ingest the data into the target table. The default value for this column is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'ColumnDelimiter'
GO
EXEC sp_addextendedproperty N'MS_Description', 'ColumnWidths column stores the fixed width of each column in the CSV file. This column is used to correctly parse the CSV file and ingest the data into the target table. The default value for this column is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'ColumnWidths'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The CommentCharacter column stores a string value representing the character used to indicate a comment in the source file. Any row starting with the comment character will be ignored during ingestion. If not specified, the default value is null.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'CommentCharacter'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The copyToPath column in the [flw].[PreIngestionCSV] table stores the path to which the source CSV file(s) should be copied. If this value is set, the source file(s) will be copied to the specified path before ingestion. This can be used to backup the source files or to ensure that the source files are not modified during ingestion. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'copyToPath'
GO
EXEC sp_addextendedproperty N'MS_Description', 'CreatedBy column stores the name of the user who created the data export flow. This can be useful for tracking and auditing the creation of data export flows. The value is automatically fetched from the session information between the client and SQL server. The default value for this column is the current user''s name, obtained through SUSER_SNAME().
[flw].[PreIngestionCSV].[CreatedDate]:
CreatedDate column stores the date and time when the data export flow was created. The default value for this column is the current date and time, obtained through GETDATE().', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'CreatedBy'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The "CreatedDate" column in the [flw].[PreIngestionCSV] table stores the date and time when a particular row was created in the table. This column has a default constraint set to get the current system date and time using the "getdate()" function, which means that if no value is specified for this column during row insertion, it will automatically store the current system date and time.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'CreatedDate'
GO
EXEC sp_addextendedproperty N'MS_Description', 'A boolean flag indicating whether to deactivate this flow from the batch process. If set to 1, the flow will not be processed during the batch process. The default value is 0.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'DeactivateFromBatch'
GO
EXEC sp_addextendedproperty N'MS_Description', 'A string that overrides default data type for creating columns in the target table. Default values is varchar(255) and is defined in [flw].[SysCFG]. This is useful when the source file contains columns that are longer than 255 characters. The default column value is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'DefaultColDataType'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The EscapeCharacter column stores a string value representing the character used to escape column delimiters or text qualifiers within a field. If not specified, the default value is null.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'EscapeCharacter'
GO
EXEC sp_addextendedproperty N'MS_Description', 'ExpectedColumnCount column stores the expected number of columns in the CSV file. This column is used to validate that the CSV file has the correct number of columns before ingesting the data into the target table. If the number of columns in the CSV file does not match the expected number of columns, an error will be thrown. The default value for this column is 0.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'ExpectedColumnCount'
GO
EXEC sp_addextendedproperty N'MS_Description', 'A boolean flag indicating whether to infer the data types of the columns in the CSV file. The default value is 0. Enabling this flag will assert the data an suggest datatypes for each column. Results are stored in table ', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'FetchDataTypes'
GO
EXEC sp_addextendedproperty N'MS_Description', 'FirstRowHasHeader column stores a Boolean value indicating whether the first row of the CSV file contains column headers. If set to True, the column headers will be used as the column names for the target table. The default value for this column is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'FirstRowHasHeader'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The FirstRowSetsExpectedColumnCount column stores a Boolean value indicating whether to use the number of columns in the first row of the source file to determine the expected number of columns for the entire file. If set to True, the number of columns in the first row will be used to determine the expected number of columns. The default value for this column is 1 (True).', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'FirstRowSetsExpectedColumnCount'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionCSV].[FlowID] column is an integer value that is used as a unique identifier for a specific data pipeline flow. It serves as the primary key for the [flw].[PreIngestionCSV] table and is used to reference the flow from other tables within the SQLFlow system. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'FlowID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'This column specifies the type of data flow. In the case of [flw].[PreIngestionCSV], the flow type is ''CSV'', indicating that the data source is a CSV file.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'FlowType'
GO
EXEC sp_addextendedproperty N'MS_Description', 'FromObjectMK column stores the unique identifier for the source object being ingested. A unique Master Key (MK) is generated for each SQLFlow Object. The master key is essential in calculating lineage dependencies and the correct execution order for each batch. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'FromObjectMK'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The IncludeFileLineNumber column stores a Boolean value indicating whether to include the line number of the source file from where the data was read. This can be useful in tracing data lineage and debugging. The default value for this column is not specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'IncludeFileLineNumber'
GO
EXEC sp_addextendedproperty N'MS_Description', 'A string that specifies the earliest file creation date that should be included in the ingestion process. Only files created on or after this date will be processed. The default value is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'InitFromFileDate'
GO
EXEC sp_addextendedproperty N'MS_Description', 'A string that specifies the latest file creation date that should be included in the ingestion process. Only files created before or on this date will be processed. The default value is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'InitToFileDate'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The MaxBufferSize column of [flw].[PreIngestionCSV] table stores the maximum size of the buffer used to read data from the source file. The default value for this column is 1024 bytes.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'MaxBufferSize'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The MaxRows column of [flw].[PreIngestionCSV] table stores the maximum number of rows to be read from the source file. The default value for this column is 0, which means all rows are read.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'MaxRows'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The NoOfThreads column of [flw].[PreIngestionCSV] table stores the number of threads to be used during the ingestion process. This column can be used to specify the number of threads that should be used for parallel processing of the data. The default value for this column is 4.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'NoOfThreads'
GO
EXEC sp_addextendedproperty N'MS_Description', 'OnErrorResume column stores a Boolean value indicating whether to continue ingesting data if an error occurs during the ingestion process. If set to True, the ingestion process will continue, and errors will be logged. If set to False, the ingestion process will be aborted if an error occurs. The default value for this column is 1 (True).', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'OnErrorResume'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The PostProcessOnTrg column of [flw].[PreIngestionCSV] table stores the name of the stored procedure that should be executed on the target table after data is ingested. This column can be used to apply transformations or data cleansing on the target table. The default value for this column is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'PostProcessOnTrg'
GO
EXEC sp_addextendedproperty N'MS_Description', 'preFilter column stores the SQL WHERE clause expression which is appended to the transformation view. This expression can be used to filter the data based on certain criteria, such as date ranges or specific column values.  A transformation view is automatically generated for each target table and the definition for each transformation is read from the table [flw].[PreIngestionTransfrom]. [flw].[PreIngestionTransfrom] is populated dynamically, but the values can be overridden in accordance with contents of various columns. The default value for this column is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'preFilter'
GO
EXEC sp_addextendedproperty N'MS_Description', 'PreInvokeAlias column stores an alias for a pre-registered Azure Data Factory or Automation Runbook to be executed before the ingestion process. This feature can be used for tasks such as external API integrations. The desired invoke must be pre-registered in the table [flw].[Invoke] and referenced by its Alias. This column does not have a default value', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'PreInvokeAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The PreProcessOnTrg column of [flw].[PreIngestionCSV] table stores the name of the stored procedure that should be executed on the target table before data is ingested. This column can be used to apply transformations or data cleansing on the target table. The default value for this column is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'PreProcessOnTrg'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The SearchSubDirectories column in the [flw].[PreIngestionCSV] table stores a Boolean value indicating whether to search for files in subdirectories of the source path. If set to 1 (True), files in subdirectories will also be processed. The default value for this column is 0 (False).', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'SearchSubDirectories'
GO
EXEC sp_addextendedproperty N'MS_Description', 'This column specifies whether the file path should be concatenated into the target column FileName_DW. The default value is 0, indicating that the error message will not include the file name.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'ShowPathWithFileName'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The SkipEmptyRows column stores a Boolean value indicating whether empty rows in the source file should be skipped during ingestion. If set to True, empty rows will be ignored during ingestion. The default value for this column is 1 (True).', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'SkipEmptyRows'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The SkipEndingDataRows column stores an integer value indicating the number of rows at the end of the source file that should be skipped during the ingestion process. This can be useful for files that contain footer or other meta data at the end of the file. The default value for this column is 0.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'SkipEndingDataRows'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The SkipStartingDataRows column stores an integer value indicating the number of rows at the beginning of the source file that should be skipped during the ingestion process. This can be useful for files that contain header or other meta data at the beginning of the file. The default value for this column is not specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'SkipStartingDataRows'
GO
EXEC sp_addextendedproperty N'MS_Description', 'A boolean flag indicating whether to delete the CSV file after it has been ingested into the target database. The default value is 0.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'srcDeleteAtPath'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The srcDeleteIngested column of [flw].[PreIngestionCSV] table stores a Boolean value indicating whether the source file should be deleted after it has been ingested. The default value for this column is 0 (False).', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'srcDeleteIngested'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The srcEncoding column of [flw].[PreIngestionCSV] table stores the encoding format of the source file. This column can be used to specify the encoding format for files that have non-standard encoding formats. The default value for this column is NULL, which means that the encoding format is detected automatically. Valid values are ASCII, Unicode, UTF32, UTF7, UTF8.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'srcEncoding'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The srcFile column in the [flw].[PreIngestionCSV] table stores the name of the source CSV file from where the data is fetched. The file name can be specified as a regular expression. Only files matching the expression will be processed by the ingestion logic. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'srcFile'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The srcPath column in the [flw].[PreIngestionCSV] table stores the path of the source CSV file(s) from where the data is fetched. This value must be a valid file path, local or Azure Data Lake Gen2 container Path. This column does not have a default value specified.
[flw].[PreIngestionCSV].[srcPathMask]: The srcPathMask column in the [flw].[PreIngestionCSV] table stores the mask of the source path from where the data is fetched. This value is a regular expression mask. Only files within folders matching the mask will be processed. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'srcPath'
GO
EXEC sp_addextendedproperty N'MS_Description', 'This column stores a regular expression mask that is used to filter the folders in the source path. Only files within folders matching the mask will be processed', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'srcPathMask'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The StripControlChars column stores a Boolean value indicating whether to strip control characters (non-printable characters) from the source file during ingestion. The default value for this column is not specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'StripControlChars'
GO
EXEC sp_addextendedproperty N'MS_Description', 'SyncSchema column stores a Boolean value indicating whether the schema of the target table should be automatically synchronized with the source CSV file. This value ensures that schema changes in the source CSV file are propagated to the target table. The default value for this column is 1 (True).', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'SyncSchema'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The SysAlias column in the [flw].[PreIngestionCSV] table stores an arbitrary alias that identifies the source system from which the data was initially fetched. This value is used to group data flows by SysAlias and Batch in conjunction or independently. Defining SysAlias is mandatory in all data flow tables. Valid SysAlias values can be registered in [flw].[SysAlias] for convenience, but this does not enforce referential integrity. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'SysAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', 'TextQualifier column stores the text qualifier used to enclose string values in the CSV file. This column is used to correctly parse the CSV file and ingest the data into the target table. The default value for this column is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'TextQualifier'
GO
EXEC sp_addextendedproperty N'MS_Description', 'ToObjectMK column stores the unique identifier for the target object where the data is being ingested. A unique Master Key (MK) is generated for each SQLFlow Object. The master key is essential in calculating lineage dependencies and the correct execution order for each batch. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'ToObjectMK'
GO
EXEC sp_addextendedproperty N'MS_Description', 'trgDBSchTbl column stores the fully qualified name of the target table or view where the data is to be ingested. The format should follow the recommended format: [Database].[Schema].[ObjectName]. This column is required to ingest the data into the target table. The default value for this column is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'trgDBSchTbl'
GO
EXEC sp_addextendedproperty N'MS_Description', 'trgServer column stores the Alias of the SQL Server instance where the target table resides. This information is required to establish a connection to the target database and ingest the data. The default value for this column is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'trgServer'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The TrimResults column stores a Boolean value indicating whether to trim white spaces from the beginning and end of values in each column during ingestion. The default value for this column is not specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'TrimResults'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The zipToPath column in the [flw].[PreIngestionCSV] table stores the path to which the source CSV file(s) should be compressed. If this value is set, the source file(s) will be compressed to the specified path. This can be used to reduce the size of the source files and optimize storage. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionCSV', 'COLUMN', N'zipToPath'
GO
