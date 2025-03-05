CREATE TABLE [flw].[SysBatch]
(
[SysBatchID] [int] NOT NULL IDENTITY(1, 1),
[Batch] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[NoOfThreads] [int] NULL
)
GO
ALTER TABLE [flw].[SysBatch] ADD CONSTRAINT [PK_SysBatch] PRIMARY KEY CLUSTERED ([SysBatchID])
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysLogBatch] is a table that logs the execution status of a batch process, which enables the batch process to restart from where it failed.', 'SCHEMA', N'flw', 'TABLE', N'SysBatch', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysBatch].[Batch] column stores the name of the SQLFlow batch. This column is used to identify and group the SQLFlow processes that belong to the same batch.', 'SCHEMA', N'flw', 'TABLE', N'SysBatch', 'COLUMN', N'Batch'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysBatch].[NoOfThreads] column specifies the number of parallel threads that the batch can use to execute its processes. This parameter can be used to optimize performance by controlling the degree of parallelism in the batch execution.', 'SCHEMA', N'flw', 'TABLE', N'SysBatch', 'COLUMN', N'NoOfThreads'
GO
