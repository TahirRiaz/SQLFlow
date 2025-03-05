CREATE TABLE [flw].[SysLogExport]
(
[RecID] [int] NOT NULL IDENTITY(1, 1),
[BatchID] [varchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FlowID] [int] NULL,
[SqlCMD] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[WhereClause] [nvarchar] (4000) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FilePath_DW] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FileName_DW] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FileSize_DW] [decimal] (18, 0) NULL,
[FileRows_DW] [int] NULL,
[NextExportDate] [date] NULL,
[NextExportValue] [int] NULL,
[ExportDate] [datetime] NULL CONSTRAINT [DF_SysLogExport_ExportDate] DEFAULT (getdate())
)
WITH
(
DATA_COMPRESSION = PAGE
)
GO
ALTER TABLE [flw].[SysLogExport] ADD CONSTRAINT [PK_SysLogExport] PRIMARY KEY CLUSTERED ([RecID]) WITH (DATA_COMPRESSION = PAGE)
GO
CREATE NONCLUSTERED INDEX [NCI_FlowID] ON [flw].[SysLogExport] ([FlowID])
GO
EXEC sp_addextendedproperty N'MS_Description', '[SysLogExport] is a table in a database that stores logs for exported files. Whenever a file is exported, a log entry is created in this table. By checking the table, it can be determined when the export process last produced a file.
Overall, the [flw].[SysLogExport] table provides a way to track and manage the export process, ensuring that all necessary files are being generated and exported on a regular basis.', 'SCHEMA', N'flw', 'TABLE', N'SysLogExport', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLogExport].[BatchID] column in the flw.SysLogExport table stores the ID of the batch process that initiated the export operation. This can be used to link the export process to a specific batch and trace any issues or errors that may have occurred during the export.', 'SCHEMA', N'flw', 'TABLE', N'SysLogExport', 'COLUMN', N'BatchID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The ExportDate column in the [flw].[SysLogExport] table stores the date and time when a file was exported. Each time a file is exported, a new row is inserted into the table with the relevant information, including the ExportDate. This allows users to track the export history of the files and determine when the last export was performed. The column has a default constraint set to GETDATE(), which means that the current date and time will be used if no value is explicitly provided during insertion.', 'SCHEMA', N'flw', 'TABLE', N'SysLogExport', 'COLUMN', N'ExportDate'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysLogExport].[FileName_DW]', 'SCHEMA', N'flw', 'TABLE', N'SysLogExport', 'COLUMN', N'FileName_DW'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The column [flw].[SysLogExport].[FilePath_DW] stores the file path where the exported file is saved in the data warehouse.', 'SCHEMA', N'flw', 'TABLE', N'SysLogExport', 'COLUMN', N'FilePath_DW'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLogExport].[FileRows_DW] column stores the number of rows exported to the file in the last export operation.', 'SCHEMA', N'flw', 'TABLE', N'SysLogExport', 'COLUMN', N'FileRows_DW'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLogExport].[FileSize_DW] column in the flw.SysLogExport table stores the size of the exported file in bytes.', 'SCHEMA', N'flw', 'TABLE', N'SysLogExport', 'COLUMN', N'FileSize_DW'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysLogExport].[FlowID] is an integer column in the flw.SysLogExport table, which stores logs for exported files. It indicates the unique identifier for the data flow that produced the exported file. The FlowID can be used to join with the [flw].[Export] table to obtain additional metadata about the data flow, such as the source table, the target file name and path, the column delimiter, and other parameters.', 'SCHEMA', N'flw', 'TABLE', N'SysLogExport', 'COLUMN', N'FlowID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLogExport].[NextExportDate] column represents the date of the next export for the corresponding flow. This column is used in the incremental process to determine the date range for the next export.', 'SCHEMA', N'flw', 'TABLE', N'SysLogExport', 'COLUMN', N'NextExportDate'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The NextExportValue column in [flw].[SysLogExport] represents the next key value to be used for incremental loads. It is used to keep track of the last processed key value in the source data, so that only new or updated data is extracted and loaded during subsequent runs.', 'SCHEMA', N'flw', 'TABLE', N'SysLogExport', 'COLUMN', N'NextExportValue'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLogExport].[RecID] column is an integer type column that serves as the primary key for the flw.SysLogExport table. It is an identity column, which means that its value is automatically generated and incremented by the system for each new row inserted into the table.', 'SCHEMA', N'flw', 'TABLE', N'SysLogExport', 'COLUMN', N'RecID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLogExport].[SqlCMD] column is used to store the SQL command executed during the export process. This can be useful for troubleshooting purposes, as it provides a record of the exact query used to extract the data being exported.', 'SCHEMA', N'flw', 'TABLE', N'SysLogExport', 'COLUMN', N'SqlCMD'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLogExport].[WhereClause] column is used to store the SQL WHERE clause that was used in the export process to filter the data being exported. This can be useful for auditing and troubleshooting purposes, as well as for ensuring that the correct data is being exported.', 'SCHEMA', N'flw', 'TABLE', N'SysLogExport', 'COLUMN', N'WhereClause'
GO
