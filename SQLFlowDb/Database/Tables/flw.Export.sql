CREATE TABLE [flw].[Export]
(
[FlowID] [int] NOT NULL CONSTRAINT [DF_Export_FlowID] DEFAULT (NEXT VALUE FOR [flw].[FlowID]),
[Batch] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SysAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[srcServer] [nvarchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcDBSchTbl] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcWithHint] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcFilter] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[IncrementalColumn] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DateColumn] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[NoOfOverlapDays] [int] NULL CONSTRAINT [DF_Export_NoOfOverlapDays] DEFAULT ((1)),
[FromDate] [date] NULL,
[ToDate] [date] NULL,
[ExportBy] [char] (1) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_Export_ExportBy] DEFAULT ('D'),
[ExportSize] [int] NULL CONSTRAINT [DF_Export_ExportSize] DEFAULT ((1)),
[ServicePrincipalAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[trgPath] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[trgFileName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[trgFiletype] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_Export_trgFiletype] DEFAULT (N'csv'),
[trgEncoding] [nvarchar] (25) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[CompressionType] [nvarchar] (25) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_Export_CompressionType] DEFAULT (N'gzip'),
[ColumnDelimiter] [nvarchar] (2) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_Export_ColumnDelimiter] DEFAULT (N';'),
[TextQualifier] [nvarchar] (2) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_Export_TextQualifier] DEFAULT (N'"'),
[AddTimeStampToFileName] [bit] NULL CONSTRAINT [DF_Export_AddTimeStampToFileName] DEFAULT ((1)),
[Subfolderpattern] [nvarchar] (25) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[NoOfThreads] [int] NULL CONSTRAINT [DF_Export_NoOfThreads] DEFAULT ((0)),
[ZipTrg] [bit] NULL CONSTRAINT [DF_Export_ZipTrg] DEFAULT ((0)),
[OnErrorResume] [bit] NULL CONSTRAINT [DF_Export_OnErrorResume] DEFAULT ((1)),
[PostInvokeAlias] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DeactivateFromBatch] [bit] NULL CONSTRAINT [DF_Export_DeactivateFromBatch] DEFAULT ((0)),
[FlowType] [varchar] (25) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL CONSTRAINT [DF_Export_FlowType] DEFAULT (N'exp'),
[FromObjectMK] [int] NULL,
[ToObjectMK] [int] NULL,
[CreatedBy] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_Export_CreatedBy] DEFAULT (suser_sname()),
[CreatedDate] [datetime] NULL CONSTRAINT [DF_Export_CreatedDate] DEFAULT (getdate())
) ON [PRIMARY]
GO
SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE TRIGGER [flw].[AddExpToLogTable] ON [flw].[Export]
FOR INSERT
AS

INSERT INTO  flw.SysLog (FlowID,[FlowType], [Process])
    SELECT FlowId ,FlowType ,  [srcServer] + '.sqlCMD-->' + [trgPath] + '.' + [trgFileName] AS [Process]
        FROM inserted
GO
ALTER TABLE [flw].[Export] ADD CONSTRAINT [PK_Export] PRIMARY KEY CLUSTERED ([FlowID]) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [NCI_FlowID] ON [flw].[Export] ([FlowID]) INCLUDE ([Batch], [SysAlias], [DeactivateFromBatch]) ON [PRIMARY]
GO
EXEC sp_addextendedproperty N'MS_Description', 'The table [flw].[Export] is used to store metadata related to data flows producing CSV files. The files can be stored locally or in Azure containers.', 'SCHEMA', N'flw', 'TABLE', N'Export', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', 'AddTimeStampToFileName column stores a Boolean value indicating whether a timestamp should be added to the file name of the exported data. Default value is False.', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'AddTimeStampToFileName'
GO
EXEC sp_addextendedproperty N'MS_Description', 'Batch is an arbitrary identifier that groups flow executions. Execution of a flow can be grouped by Batch and SysAlias in conjunction or independently. Defining Batch is mandatory in all data flow tables. Valid Batch values can be registered in [flw].[ SysBatch] for convenience, but this doesn''t enforce referential integrity.
', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'Batch'
GO
EXEC sp_addextendedproperty N'MS_Description', 'ColumnDelimiter column stores the delimiter used to separate columns in the exported file. Semicolon is the default value.', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'ColumnDelimiter'
GO
EXEC sp_addextendedproperty N'MS_Description', 'CreatedBy column stores the name of the user who created the data export flow. This can be useful for tracking and auditing the creation of data export flows. The value is automatically fetched from the session information between the client and SQL server. The default value for this column is the current user''s name, obtained through SUSER_SNAME().', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'CreatedBy'
GO
EXEC sp_addextendedproperty N'MS_Description', 'CreatedDate column stores the date and time when the data export flow was created. The default value for this column is the current date and time, obtained through GETDATE().', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'CreatedDate'
GO
EXEC sp_addextendedproperty N'MS_Description', 'DeactivateFromBatch stores a Boolean value indicating whether the data export flow should be deactivated from the batch execution. This feature can be utilized to deactivate a data pipeline from the scheduled execution. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'DeactivateFromBatch'
GO
EXEC sp_addextendedproperty N'MS_Description', 'ExportBy column defines the type of range that should be used to create export files. The valid options are number of days (D), months (M), or Key values (K). The range is defined in the ExportSize column.', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'ExportBy'
GO
EXEC sp_addextendedproperty N'MS_Description', 'ExportSize column defines the size of the clustering key that should be used to create export files based on the selected ExportBy option.', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'ExportSize'
GO
EXEC sp_addextendedproperty N'MS_Description', 'FlowID is a system-generated unique numeric identifier for each data pipeline. FlowID can be used to track and audit the execution of data pipeline flows. See the search command for details.', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'FlowID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'FlowType is a system-generated value. Export flows are abbreviated as "exp". FlowType is used to route execution to the correct backend logic. The default value for this column is ''exp''', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'FlowType'
GO
EXEC sp_addextendedproperty N'MS_Description', 'FromDate column stores the starting date value for the data being exported. This can be used when you want to export only a certain portion of the data. After the initial load, SQLFlow will switch to incremental export based on the last processed date.', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'FromDate'
GO
EXEC sp_addextendedproperty N'MS_Description', 'FromObjectMK column stores the unique identifier for the source object being exported. A unique Master Key (MK) is generated for each SQLFlow Object. The master key is essential in calculating lineage dependencies and the correct execution order for each batch. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'FromObjectMK'
GO
EXEC sp_addextendedproperty N'MS_Description', 'IncrementalColumn stores the column name used for correctly fetching changed data since the last execution. Min and max values are automatically calculated and logged to facilitate the incremental logic. Processed values are stored in table [flw].[SysLog] column [NextExportDate] and [NextExportValue], respectively. Incremental exports can optimize processing times and resource consumption.', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'IncrementalColumn'
GO
EXEC sp_addextendedproperty N'MS_Description', 'NoOfOverlapDays stores the number of days to overlap when incremental data is exported. Date offset is calculated based on present values within the provided IncrementalColumn. This ensures that all relevant data is included in the export. The default value for this column is 1.', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'NoOfOverlapDays'
GO
EXEC sp_addextendedproperty N'MS_Description', 'NoOfThreads defines the number of threads that should be used during the export process. Multithreaded operation is recommended for larger tables. The default value for this column is 0.', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'NoOfThreads'
GO
EXEC sp_addextendedproperty N'MS_Description', 'OnErrorResume column stores a Boolean value indicating whether the next data pipeline should run if the current process fails. This feature is utilized in Batch execution. The default value for this column is 1 (True).', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'OnErrorResume'
GO
EXEC sp_addextendedproperty N'MS_Description', 'Azure Data Factory or an Automation Runbook can be executed after the export process has completed successfully. This feature can be used to further process the exported files, for example uploading them to an SFTP. The desired invoke must be pre-registered in the table [flw].[Invoke] and referenced by its Alias. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'PostInvokeAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', 'srcDBSchTbl column stores the name of the source database, schema, and table/view from where the data is fetched. Please ensure that your value follows the recommened format: [Database]. [Schema]. [ObjectName]', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'srcDBSchTbl'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[Export].[srcFilter] column in the [flw].[Export] table contains the filter expression that is applied to the source table/view in order to select a subset of rows that need to be exported. This filter expression can be used to implement various scenarios such as incremental loading or data filtering based on a certain condition. The expression is written in SQL syntax and can reference any column in the source table/view.', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'srcFilter'
GO
EXEC sp_addextendedproperty N'MS_Description', 'srcDBSchTbl stores the name of the source database, schema, and table/view from where the data is fetched. Please ensure that your value follows the recommended format: [Database].[Schema].[ObjectName]', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'srcServer'
GO
EXEC sp_addextendedproperty N'MS_Description', 'Subfolderpattern defines a subfolder pattern for the exported files. Valid values are driven from YYYYMMDD. Using YYYYMM distributes files based on year and month subfolders. Default value is "YYYYMM".', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'Subfolderpattern'
GO
EXEC sp_addextendedproperty N'MS_Description', 'SysAlias is an arbitrary alias that identifies the source system from which the data was initially fetched. Execution of data pipeline can be grouped by Batch and SysAlias in conjunction or independently. Defining SysAlias is mandatory in all data flow tables. Valid SysAlias values can be registered in [flw].[SysAlias] for convenience, but this doesn''t enforce referential integrity.', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'SysAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', 'Text qualifier is used to qualify all string based columns with the provided value. Default value is double quote.', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'TextQualifier'
GO
EXEC sp_addextendedproperty N'MS_Description', 'ToDate column stores the ending date value for the data being exported. Defining an end date is a hard limit. No data will be exported after the provided end date.', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'ToDate'
GO
EXEC sp_addextendedproperty N'MS_Description', 'ToObjectMK column stores the unique identifier for the target file where the data is being exported. A unique Master Key (MK) is generated for each SQLFlow Object. The master key is essential in calculating lineage dependencies and the correct execution order for each batch. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'ToObjectMK'
GO
EXEC sp_addextendedproperty N'MS_Description', 'trgEncoding column stores the encoding to be used for the target file. Valid values are ASCII, Unicode, UTF32, UTF7, UTF8. This value ensures that the exported data is stored with the correct character encoding. Default value is "UTF8".', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'trgEncoding'
GO
EXEC sp_addextendedproperty N'MS_Description', 'trgFileName column stores the file name of the target file name. The target file name is postfixed with the selected range defined in ExportBy. This information can be used to locate the exported data and identify its contents. Default value is an empty string.', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'trgFileName'
GO
EXEC sp_addextendedproperty N'MS_Description', 'trgFiletype column stores the file type of the exported file. Currently, only csv files are supported. Support for writing Parquet file is a planned task. Default value is "csv".', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'trgFiletype'
GO
EXEC sp_addextendedproperty N'MS_Description', 'trgPath column stores the file path or directory where the exported data is stored. This information can be used to locate the exported data. Target path can be a local drive, shared folder, or an Azure Container.', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'trgPath'
GO
EXEC sp_addextendedproperty N'MS_Description', 'ZipTrg column stores a Boolean value indicating whether the exported data should be compressed into a ZIP file. This option is currently not completed. The default value for this column is 0 (False).', 'SCHEMA', N'flw', 'TABLE', N'Export', 'COLUMN', N'ZipTrg'
GO
