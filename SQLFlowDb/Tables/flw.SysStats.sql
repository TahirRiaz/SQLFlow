CREATE TABLE [flw].[SysStats]
(
[StatsID] [int] NOT NULL IDENTITY(1, 1),
[FlowType] [varchar] (25) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[StatsDate] [datetime] NULL CONSTRAINT [DF_IngestionStats_StatsDate] DEFAULT (getdate()),
[FlowID] [int] NOT NULL,
[StartTime] [datetime] NULL,
[EndTime] [datetime] NULL,
[DurationFlow] [int] NULL,
[DurationPre] [int] NULL,
[DurationPost] [int] NULL,
[Fetched] [int] NULL,
[Inserted] [int] NULL,
[Updated] [int] NULL,
[Deleted] [int] NULL,
[Success] [bit] NULL,
[FlowRate] [decimal] (18, 2) NULL,
[NoOfThreads] [int] NULL,
[ExecMode] [varchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FileName] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FileSize] [int] NULL,
[FileDate] [varchar] (15) COLLATE SQL_Latin1_General_CP1_CI_AS NULL
)
GO
ALTER TABLE [flw].[SysStats] ADD CONSTRAINT [PK_SysStats] PRIMARY KEY CLUSTERED ([StatsID])
GO
CREATE NONCLUSTERED INDEX [NCI_FlowId] ON [flw].[SysStats] ([FlowID]) INCLUDE ([Success], [DurationFlow])
GO
CREATE NONCLUSTERED INDEX [NCI_FlowType] ON [flw].[SysStats] ([FlowType]) INCLUDE ([FlowID])
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysStats] table is used to store key attributes of all executions. This information can be utilized to assess data concurrency and data stream patterns.', 'SCHEMA', N'flw', 'TABLE', N'SysStats', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysStats].[Deleted] column stores the number of rows deleted during the execution of the flow.', 'SCHEMA', N'flw', 'TABLE', N'SysStats', 'COLUMN', N'Deleted'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysStats].[DurationFlow] column stores the duration of the flow execution in seconds.', 'SCHEMA', N'flw', 'TABLE', N'SysStats', 'COLUMN', N'DurationFlow'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysStats].[DurationPost] column stores the duration in milliseconds of the post-processing phase of an execution.', 'SCHEMA', N'flw', 'TABLE', N'SysStats', 'COLUMN', N'DurationPost'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The column [flw].[SysStats].[DurationPre] indicates the duration of the pre-processing phase of the data flow.', 'SCHEMA', N'flw', 'TABLE', N'SysStats', 'COLUMN', N'DurationPre'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysStats].[EndTime] column stores the end time of the execution.', 'SCHEMA', N'flw', 'TABLE', N'SysStats', 'COLUMN', N'EndTime'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysStats].[ExecMode] column stores the execution mode of a flow. This can be used to distinguish between different types of execution modes such as development, testing, or production. The value in this column is a string that describes the execution mode.', 'SCHEMA', N'flw', 'TABLE', N'SysStats', 'COLUMN', N'ExecMode'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysStats].[Fetched] column stores the number of records fetched during the execution of a data flow.', 'SCHEMA', N'flw', 'TABLE', N'SysStats', 'COLUMN', N'Fetched'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysStats].[FileDate]', 'SCHEMA', N'flw', 'TABLE', N'SysStats', 'COLUMN', N'FileDate'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The FileName column in the flw.SysStats table is a nvarchar(MAX) type column that stores the name of the file that was processed as part of the flow execution.', 'SCHEMA', N'flw', 'TABLE', N'SysStats', 'COLUMN', N'FileName'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysStats].[FileSize] column stores the size of the file used in the flow in bytes.', 'SCHEMA', N'flw', 'TABLE', N'SysStats', 'COLUMN', N'FileSize'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysStats].[FlowID]', 'SCHEMA', N'flw', 'TABLE', N'SysStats', 'COLUMN', N'FlowID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysStats].[FlowRate] column stores the rate of data flow during the execution of the flow, measured in rows per second.', 'SCHEMA', N'flw', 'TABLE', N'SysStats', 'COLUMN', N'FlowRate'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysStats].[FlowType] stores the type of data flow or process that was executed, such as ADO object, csv, exp, ing, inv, sp, xls, or xml.', 'SCHEMA', N'flw', 'TABLE', N'SysStats', 'COLUMN', N'FlowType'
GO
EXEC sp_addextendedproperty N'MS_Description', 'This column stores the number of rows inserted during the execution of a flow.', 'SCHEMA', N'flw', 'TABLE', N'SysStats', 'COLUMN', N'Inserted'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysStats].[NoOfThreads] column stores the number of threads used in the execution.', 'SCHEMA', N'flw', 'TABLE', N'SysStats', 'COLUMN', N'NoOfThreads'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysStats].[StartTime] column in [flw].[SysStats] table stores the start time of the execution of a particular flow.', 'SCHEMA', N'flw', 'TABLE', N'SysStats', 'COLUMN', N'StartTime'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The StatsDate column in the flw.SysStats table stores the date and time when the execution statistics were recorded. The default value for this column is the current system date and time when a new row is inserted.', 'SCHEMA', N'flw', 'TABLE', N'SysStats', 'COLUMN', N'StatsDate'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysStats].[StatsID]', 'SCHEMA', N'flw', 'TABLE', N'SysStats', 'COLUMN', N'StatsID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysStats].[Success] column stores a boolean value indicating whether the execution was successful or not. A value of 1 indicates success, while a value of 0 indicates failure.', 'SCHEMA', N'flw', 'TABLE', N'SysStats', 'COLUMN', N'Success'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysStats].[Updated] column stores the number of rows updated during the execution of a data flow.', 'SCHEMA', N'flw', 'TABLE', N'SysStats', 'COLUMN', N'Updated'
GO
