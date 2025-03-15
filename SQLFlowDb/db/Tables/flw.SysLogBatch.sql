CREATE TABLE [flw].[SysLogBatch]
(
[RecID] [int] NOT NULL IDENTITY(1, 1),
[BatchID] [varchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Batch] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FlowID] [int] NULL,
[FlowType] [varchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SysAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Step] [int] NULL,
[Sequence] [int] NULL,
[dbg] [int] NULL,
[Status] [varchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_SysBatch_Status] DEFAULT ((0)),
[BatchTime] [datetime] NULL,
[StartTime] [datetime] NULL,
[EndTime] [datetime] NULL,
[SourceIsAzCont] [bit] NULL CONSTRAINT [DF_SysLogBatch_SourceIsAzCont] DEFAULT ((0))
) ON [PRIMARY]
WITH
(
DATA_COMPRESSION = PAGE
)
GO
ALTER TABLE [flw].[SysLogBatch] ADD CONSTRAINT [PK_SysLogBatch] PRIMARY KEY CLUSTERED ([RecID]) WITH (DATA_COMPRESSION = PAGE) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [NCI_Batch] ON [flw].[SysLogBatch] ([Batch]) INCLUDE ([BatchID]) WHERE ([Status]<>'Done') ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [NCI_BatchID] ON [flw].[SysLogBatch] ([BatchID]) INCLUDE ([FlowID], [EndTime]) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [NCI_EndTime] ON [flw].[SysLogBatch] ([EndTime]) INCLUDE ([BatchID], [Batch], [FlowType], [SysAlias]) WHERE ([EndTime] IS NULL) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [NCI_FlowID_EndTime] ON [flw].[SysLogBatch] ([FlowID], [EndTime]) INCLUDE ([Batch], [BatchID]) ON [PRIMARY]
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLogBatch].[Batch] column stores the name or description of the batch process. This value is typically set by the user who initiates the batch process and can help identify the purpose or context of the batch.', 'SCHEMA', N'flw', 'TABLE', N'SysLogBatch', 'COLUMN', N'Batch'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The BatchID column in [flw].[SysLogBatch] is a string value that identifies a particular batch process run. This can be useful for tracking and organizing batch runs over time.', 'SCHEMA', N'flw', 'TABLE', N'SysLogBatch', 'COLUMN', N'BatchID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The BatchTime column in [flw].[SysLogBatch] represents the timestamp for when the batch process started.', 'SCHEMA', N'flw', 'TABLE', N'SysLogBatch', 'COLUMN', N'BatchTime'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The dbg column in the [flw].[SysLogBatch] table stores the debugging level that the SQLFlow process was executed with. Valid values are 0 (minimal), 1 (basic), and 2 (detailed).', 'SCHEMA', N'flw', 'TABLE', N'SysLogBatch', 'COLUMN', N'dbg'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysLogBatch].[EndTime] column stores the end time of the step''s execution. It indicates when the execution of the step completed.', 'SCHEMA', N'flw', 'TABLE', N'SysLogBatch', 'COLUMN', N'EndTime'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLogBatch].[FlowID] column contains the ID of the flow being executed as part of the batch process. It is a foreign key to the FlowID column in the [flw].[SysLog] table, which contains information about the execution of individual flows.', 'SCHEMA', N'flw', 'TABLE', N'SysLogBatch', 'COLUMN', N'FlowID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLogBatch].[FlowType] column indicates the type of the flow associated with the batch process. This value can be used for filtering and reporting on batch process executions.', 'SCHEMA', N'flw', 'TABLE', N'SysLogBatch', 'COLUMN', N'FlowType'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The RecID column is an auto-incrementing primary key column for the SysLogBatch table. It uniquely identifies each row in the table.



', 'SCHEMA', N'flw', 'TABLE', N'SysLogBatch', 'COLUMN', N'RecID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLogBatch].[Sequence] column is used for arbitrary ordering within a step. However, it is not currently being used.', 'SCHEMA', N'flw', 'TABLE', N'SysLogBatch', 'COLUMN', N'Sequence'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLogBatch].[SourceIsAzCont] column is a boolean column that indicates whether the source of the data being processed is an Azure Container. If the value is 1, it means that the source of the data is an Azure Container, and if the value is 0, it means that the source of the data is not an Azure Container.', 'SCHEMA', N'flw', 'TABLE', N'SysLogBatch', 'COLUMN', N'SourceIsAzCont'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The StartTime column in the [flw].[SysLogBatch] table stores the timestamp when the current batch process started executing.', 'SCHEMA', N'flw', 'TABLE', N'SysLogBatch', 'COLUMN', N'StartTime'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysLogBatch].[Status] column shows the current status of the batch process, whether it is running, completed, failed or any other custom status defined by the user.', 'SCHEMA', N'flw', 'TABLE', N'SysLogBatch', 'COLUMN', N'Status'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLogBatch].[Step] column is an integer that denotes the execution step of a flow in a batch process. It indicates the order in which a particular flow was executed in the batch.', 'SCHEMA', N'flw', 'TABLE', N'SysLogBatch', 'COLUMN', N'Step'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysLogBatch].[SysAlias] column is used to store the system alias of the execution. System alias is a logical name for the target system, for example, DEV, QA, PROD, etc.', 'SCHEMA', N'flw', 'TABLE', N'SysLogBatch', 'COLUMN', N'SysAlias'
GO
