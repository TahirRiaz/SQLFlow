CREATE TABLE [flw].[StoredProcedure]
(
[FlowID] [int] NOT NULL CONSTRAINT [DF_StoredProcedure_FlowID] DEFAULT (NEXT VALUE FOR [flw].[FlowID]),
[Batch] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SysAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[trgServer] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[trgDBSchSP] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[OnErrorResume] [bit] NULL CONSTRAINT [DF_PrePostSP_OnErrorResume] DEFAULT ((1)),
[PostInvokeAlias] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Description] [nvarchar] (2048) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FlowType] [varchar] (25) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_Procedure_FlowType] DEFAULT ('sp'),
[DeactivateFromBatch] [bit] NULL CONSTRAINT [DF_PrePostSP_DeactivateFromBatch] DEFAULT ((0)),
[FromObjectMK] [int] NULL,
[ToObjectMK] [int] NULL,
[CreatedBy] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_StoredProcedure_CreatedBy] DEFAULT (suser_sname()),
[CreatedDate] [datetime] NULL
)
GO
SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE TRIGGER [flw].[AddStoredProcedureToLogTable]
ON [flw].[StoredProcedure]
FOR INSERT
AS
INSERT INTO flw.SysLog
(
    FlowID,
    [FlowType],
    [Process],
    [ProcessShort],
    [Batch],
    [SysAlias],
    Success
)
SELECT FlowID,
       [FlowType],
       '-->' + [trgServer] +'.'+ [trgDBSchSP] AS [Process],
       '-->' + [trgServer] +'.'+ [trgDBSchSP] AS [ProcessShort],
       [Batch],
       [Batch],
       1
FROM inserted;
GO
ALTER TABLE [flw].[StoredProcedure] ADD CONSTRAINT [PK_PrePostSP] PRIMARY KEY CLUSTERED ([FlowID])
GO
CREATE NONCLUSTERED INDEX [NCI_FlowID] ON [flw].[StoredProcedure] ([FlowID]) INCLUDE ([Batch], [SysAlias], [DeactivateFromBatch])
GO
EXEC sp_addextendedproperty N'MS_Description', 'This table hosts meta data for independent stored procedures. Optimal execution order within a Batch and overall is calculated by SQLFlows linage module.', 'SCHEMA', N'flw', 'TABLE', N'StoredProcedure', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', 'Batch is an arbitrary identifier that groups flow executions. Execution of a flows can be grouped by Batch and SysAlias in conjunction or independently. Defining Batch is mandatory in all data flow tables. Valid Batch values can be registered in [flw].[ SysBatch] for convenience, but this doesn''t enforce referential integrity.', 'SCHEMA', N'flw', 'TABLE', N'StoredProcedure', 'COLUMN', N'Batch'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[PrePostSP].[DeactivateFromBatch] is a boolean column in the [flw].[PrePostSP] table. It specifies whether to deactivate the stored procedure from the batch or not. The default value of this column is 0, which means the stored procedure will not be deactivated from the batch.', 'SCHEMA', N'flw', 'TABLE', N'StoredProcedure', 'COLUMN', N'DeactivateFromBatch'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PrePostSP].[Description] column in the flw.PrePostSP table is used to store the description of the independent stored procedure. It is a nullable NVARCHAR(2048) data type. It can contain up to 2048 Unicode characters and its purpose is to give a brief description of the stored procedure that can be helpful in understanding the stored procedure''s functionality.', 'SCHEMA', N'flw', 'TABLE', N'StoredProcedure', 'COLUMN', N'Description'
GO
EXEC sp_addextendedproperty N'MS_Description', 'FlowID is a system generated unique numeric identifier for each data pipeline. FlowID can be utilized to track and audit the execution of data pipelines flows. See the search command for details.', 'SCHEMA', N'flw', 'TABLE', N'StoredProcedure', 'COLUMN', N'FlowID'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[PrePostSP].[FlowType] is a column in the [flw].[PrePostSP] table, which is used to indicate the type of stored procedure. It is a VARCHAR(25) column, and its default value is ''sp''', 'SCHEMA', N'flw', 'TABLE', N'StoredProcedure', 'COLUMN', N'FlowType'
GO
EXEC sp_addextendedproperty N'MS_Description', 'FromObjectMK column stores the unique identifier for the source object being ingested. A unique Master Key (MK) is generated for each SQLFlow Object. The master key is essential in calculating lineage dependencies and the correct execution order for each batch. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'StoredProcedure', 'COLUMN', N'FromObjectMK'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PrePostSP].[OnErrorResume] column is a nullable BIT column in the [flw].[PrePostSP] table. It represents whether the pipeline execution should continue if an error occurs while executing the stored procedure associated with the current row. If the value is 1 (true), then the pipeline will continue executing after the error occurs. If the value is 0 (false) or null, then the pipeline will stop executing after the error occurs. The default constraint for this column is set to 1 (true), which means that by default the pipeline will continue executing after an error occurs unless explicitly set otherwise.', 'SCHEMA', N'flw', 'TABLE', N'StoredProcedure', 'COLUMN', N'OnErrorResume'
GO
EXEC sp_addextendedproperty N'MS_Description', 'You are correct. PostInvokeAlias is an optional reference to an Azure Data Factory or Azure Automation Runbook, used to trigger the execution of a specific task after the stored procedure has been executed. If specified, SQLFlows will automatically trigger the task upon successful completion of the stored procedure.', 'SCHEMA', N'flw', 'TABLE', N'StoredProcedure', 'COLUMN', N'PostInvokeAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', 'SysAlias is an arbitrary alias that identifies the source system from which the data was initially fetched. Execution of data pipelines can be grouped by Batch and SysAlias in conjunction or independently. Defining SysAlias is mandatory in all data flow tables. Valid SysAlias values can be registered in [flw].[SysAlias] for convenience, but this doesn''t enforce referential integrity.', 'SCHEMA', N'flw', 'TABLE', N'StoredProcedure', 'COLUMN', N'SysAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', 'ToObjectMK column stores the unique identifier for the target object where the data is being executed. A unique Master Key (MK) is generated for each SQLFlow Object. The master key is essential in calculating lineage dependencies and the correct execution order for each batch. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'StoredProcedure', 'COLUMN', N'ToObjectMK'
GO
EXEC sp_addextendedproperty N'MS_Description', 'This column stores the name of the target database schema and stored procedure where data will be inserted or updated during the pipeline process.', 'SCHEMA', N'flw', 'TABLE', N'StoredProcedure', 'COLUMN', N'trgDBSchSP'
GO
EXEC sp_addextendedproperty N'MS_Description', 'trgServer is a column in the [flw].[PrePostSP] table that stores the name of the target server where the stored procedure defined in the [trgDBSchSP] column is executed during the data pipeline process. This column is of data type NVARCHAR(250) and is marked as NOT NULL.', 'SCHEMA', N'flw', 'TABLE', N'StoredProcedure', 'COLUMN', N'trgServer'
GO
