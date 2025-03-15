CREATE TABLE [flw].[SysLogFile]
(
[LogFileID] [int] NOT NULL IDENTITY(1, 1),
[BatchID] [varchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FlowID] [int] NULL,
[FileDate_DW] [varchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FileName_DW] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FileRowDate_DW] [datetime] NULL CONSTRAINT [DF_SysFileLog_FileRowDate_DW] DEFAULT (getdate()),
[FileSize_DW] [decimal] (18, 0) NULL,
[FileColumnCount] [int] NULL,
[ExpectedColumnCount] [int] NULL,
[DataSet_DW] [varchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL
) ON [PRIMARY]
WITH
(
DATA_COMPRESSION = PAGE
)
GO
ALTER TABLE [flw].[SysLogFile] ADD CONSTRAINT [PK_SysFileLog] PRIMARY KEY CLUSTERED ([LogFileID]) WITH (DATA_COMPRESSION = PAGE) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [NCI_FileName] ON [flw].[SysLogFile] ([FileName_DW]) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [NCI_FlowID] ON [flw].[SysLogFile] ([FlowID]) INCLUDE ([FileRowDate_DW], [FileName_DW]) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [NCI_FlowID_WithRowDate] ON [flw].[SysLogFile] ([FlowID], [FileRowDate_DW] DESC) INCLUDE ([FileName_DW]) ON [PRIMARY]
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLogFile] table is used to log all imported files, and it can be used to determine when a certain Flow last imported a file.', 'SCHEMA', N'flw', 'TABLE', N'SysLogFile', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', 'The BatchID column in the [flw].[SysLogFile] table stores a unique identifier for the batch in which the imported file was processed. This can be used to track the progress of a specific batch or to identify which batch an imported file belongs to.', 'SCHEMA', N'flw', 'TABLE', N'SysLogFile', 'COLUMN', N'BatchID'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysLogFile].[ExpectedColumnCount] is a column in the SysLogFile table that stores the expected number of columns in the imported file. This can be used to validate that the imported file has the expected structure before further processing is performed on the data.', 'SCHEMA', N'flw', 'TABLE', N'SysLogFile', 'COLUMN', N'ExpectedColumnCount'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLogFile].[FileColumnCount] column in the [flw].[SysLogFile] table stores the number of columns in the imported file. It can be used to ensure that the correct number of columns are being imported and to identify any issues or discrepancies with the data.', 'SCHEMA', N'flw', 'TABLE', N'SysLogFile', 'COLUMN', N'FileColumnCount'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The FileDate_DW column in the [flw].[SysLogFile] table is a string column that stores the date of the file that was imported. It is used to easily determine when a certain flow last imported a file. However, note that this column stores the date as a string, which can cause issues when sorting or comparing the values.', 'SCHEMA', N'flw', 'TABLE', N'SysLogFile', 'COLUMN', N'FileDate_DW'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLogFile].[FileName_DW] column stores the name of the file that was imported.', 'SCHEMA', N'flw', 'TABLE', N'SysLogFile', 'COLUMN', N'FileName_DW'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLogFile].[FileRowDate_DW] column stores the date and time when the file was imported into the system. It has a default constraint that sets its value to the current date and time when a new row is inserted into the table.', 'SCHEMA', N'flw', 'TABLE', N'SysLogFile', 'COLUMN', N'FileRowDate_DW'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLogFile].[FileSize_DW] column stores the size of the imported file in bytes.', 'SCHEMA', N'flw', 'TABLE', N'SysLogFile', 'COLUMN', N'FileSize_DW'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysLogFile].[FlowID] is an integer column in the flw.SysLogFile table that stores the unique identifier of the data flow that imported the file. This column is used to associate imported files with the data flow that processed them.', 'SCHEMA', N'flw', 'TABLE', N'SysLogFile', 'COLUMN', N'FlowID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLogFile].[RecID] is an auto-incrementing integer field that serves as the primary key for the flw.SysLogFile table. It uniquely identifies each record (row) in the table.', 'SCHEMA', N'flw', 'TABLE', N'SysLogFile', 'COLUMN', N'LogFileID'
GO
CREATE FULLTEXT INDEX ON [flw].[SysLogFile] KEY INDEX [PK_SysFileLog] ON [FT_SysLogFile]
GO
ALTER FULLTEXT INDEX ON [flw].[SysLogFile] ADD ([BatchID] LANGUAGE 1033)
GO
ALTER FULLTEXT INDEX ON [flw].[SysLogFile] ADD ([FileDate_DW] LANGUAGE 1033)
GO
ALTER FULLTEXT INDEX ON [flw].[SysLogFile] ADD ([FileName_DW] LANGUAGE 1033)
GO
