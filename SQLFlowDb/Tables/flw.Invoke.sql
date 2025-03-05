CREATE TABLE [flw].[Invoke]
(
[FlowID] [int] NOT NULL CONSTRAINT [DF_Invoke_FlowID] DEFAULT (NEXT VALUE FOR [flw].[FlowID]),
[Batch] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SysAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[trgServicePrincipalAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[InvokeAlias] [nvarchar] (25) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[InvokeType] [varchar] (25) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL CONSTRAINT [DF_Invoke_ScriptType] DEFAULT (N'aut'),
[InvokePath] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[InvokeFile] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcServicePrincipalAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Code] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Arguments] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PipelineName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[RunbookName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ParameterJSON] [nvarchar] (2000) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[OnErrorResume] [bit] NULL CONSTRAINT [DF_Invoke_OnErrorResume] DEFAULT ((1)),
[DeactivateFromBatch] [bit] NULL CONSTRAINT [DF_Invoke_DeactivateFromBatch] DEFAULT ((0)),
[ToObjectMK] [int] NULL,
[CreatedBy] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_Invoke_CreatedBy] DEFAULT (suser_sname()),
[CreatedDate] [datetime] NULL CONSTRAINT [DF_Invoke_CreatedDate] DEFAULT (getdate())
)
GO
SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE TRIGGER [flw].[AddInvokeToLogTable]
ON [flw].[Invoke]
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
       [InvokeType],
       '-->' + [InvokeAlias] AS [Process],
       '-->' + [InvokeAlias] AS [ProcessShort],
       [Batch],
       [Batch],
       1
FROM inserted;
GO
ALTER TABLE [flw].[Invoke] ADD CONSTRAINT [PK_Invoke] PRIMARY KEY CLUSTERED ([FlowID])
GO
CREATE NONCLUSTERED INDEX [NCI_FlowID] ON [flw].[Invoke] ([FlowID]) INCLUDE ([Batch], [SysAlias], [DeactivateFromBatch])
GO
CREATE UNIQUE NONCLUSTERED INDEX [NCI_UniqueScriptAlias] ON [flw].[Invoke] ([InvokeAlias])
GO
EXEC sp_addextendedproperty N'MS_Description', 'This table hosts meta data for invoking Azure Data Factory pipelines or Azure Automation Runbooks. Various data flow tables are referencing this table by InvokeAlias. ', 'SCHEMA', N'flw', 'TABLE', N'Invoke', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', 'The Arguments column in the flw.Invoke table is used to store any arguments or parameters required for the invocation of a particular powershell script.', 'SCHEMA', N'flw', 'TABLE', N'Invoke', 'COLUMN', N'Arguments'
GO
EXEC sp_addextendedproperty N'MS_Description', 'Batch is an arbitrary identifier that groups flow executions. Execution of a flows can be grouped by Batch and SysAlias in conjunction or independently. Defining Batch is mandatory in all data flow tables. Valid Batch values can be registered in [flw].[ SysBatch] for convenience, but this doesn''t enforce referential integrity.', 'SCHEMA', N'flw', 'TABLE', N'Invoke', 'COLUMN', N'Batch'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The Script column in the flw.Invoke table contains the actual powershell script that will be executed', 'SCHEMA', N'flw', 'TABLE', N'Invoke', 'COLUMN', N'Code'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The DeactivateFromBatch column in the [flw].[Invoke] table is a boolean column that determines whether the current Invoke record should be deactivated from the batch if it encounters an error during execution. If the value is set to 1, it will be deactivated from the batch, and if it is set to 0, it will not be deactivated. By default, the value of this column is set to 0, meaning that the Invoke record will not be deactivated from the batch even if it encounters an error.', 'SCHEMA', N'flw', 'TABLE', N'Invoke', 'COLUMN', N'DeactivateFromBatch'
GO
EXEC sp_addextendedproperty N'MS_Description', 'FlowID is a system generated unique numeric identifier for each data pipeline. FlowID can be utilized to track and audit the execution of data pipelines flows. See the search command for details.', 'SCHEMA', N'flw', 'TABLE', N'Invoke', 'COLUMN', N'FlowID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The column [flw].[Invoke].[InvokeAlias] is used to uniquely identify the invoke operation in other data flow tables. It is a required field and must be unique for each Invoke operation. The InvokeAlias value is used as a reference in other tables, allowing for the mapping of the invocation of the pipeline or runbook.', 'SCHEMA', N'flw', 'TABLE', N'Invoke', 'COLUMN', N'InvokeAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[Invoke].[InvokeFile] is a column in the flw.Invoke table that specifies the file name of the script to be invoked. The script can be a PowerShell script. This column is used in conjunction with the [InvokePath] column to identify the exact location of the script to be executed.', 'SCHEMA', N'flw', 'TABLE', N'Invoke', 'COLUMN', N'InvokeFile'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[Invoke].[InvokePath] column is used to store the path to the executable file that needs to be invoked. This column is used when the [flw].[Invoke].[InvokeType] is set to ''ps'' (indicating the file to be executed is an executable file).', 'SCHEMA', N'flw', 'TABLE', N'Invoke', 'COLUMN', N'InvokePath'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The column [flw].[Invoke].[InvokeType] specifies the type of the invoke, which can be either ''ps'' for PowerShell, ''adf'' for Azure Data Factory or ''aut'' for Azure Automation. This column is used in the SQLFlows pipeline process to determine which type of invoke to execute.', 'SCHEMA', N'flw', 'TABLE', N'Invoke', 'COLUMN', N'InvokeType'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The OnErrorResume column in the flw.Invoke table is a boolean value that indicates whether the pipeline or runbook should continue executing even if an error occurs during execution. If OnErrorResume is set to true, then the pipeline or runbook will continue executing even if an error occurs. If it is set to false, then the pipeline or runbook will stop executing as soon as an error occurs. By default, the OnErrorResume column is set to true.', 'SCHEMA', N'flw', 'TABLE', N'Invoke', 'COLUMN', N'OnErrorResume'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[Invoke].[ParameterJSON] is a column that stores a JSON string containing the parameters required for an Azure Automation Runbook. The parameters are passed as key-value pairs in the JSON string. These parameters are used when the Runbook is executed through the pipeline.', 'SCHEMA', N'flw', 'TABLE', N'Invoke', 'COLUMN', N'ParameterJSON'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [PipelineName] column in the [flw].[Invoke] table holds the name of the Azure Data Factory Pipeline that will be executed.', 'SCHEMA', N'flw', 'TABLE', N'Invoke', 'COLUMN', N'PipelineName'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[Invoke].[RunbookName] is a column in the flw.Invoke table. It stores the name of the Azure Automation Runbook to be executed.', 'SCHEMA', N'flw', 'TABLE', N'Invoke', 'COLUMN', N'RunbookName'
GO
EXEC sp_addextendedproperty N'MS_Description', 'SysAlias is an arbitrary alias that identifies the source system from which the data was initially fetched. Execution of data pipelines can be grouped by Batch and SysAlias in conjunction or independently. Defining SysAlias is mandatory in all data flow tables. Valid SysAlias values can be registered in [flw].[SysAlias] for convenience, but this doesn''t enforce referential integrity.', 'SCHEMA', N'flw', 'TABLE', N'Invoke', 'COLUMN', N'SysAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', 'ToObjectMK column stores the unique identifier for the target object where the data is being executed. A unique Master Key (MK) is generated for each SQLFlow Object. The master key is essential in calculating lineage dependencies and the correct execution order for each batch. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'Invoke', 'COLUMN', N'ToObjectMK'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The column [flw].[Invoke].[ServicePrincipalAlias] in the flw.Invoke table holds a reference to an Azure Active Directory (AD) service principal that is used to authorize access to resources when invoking an Azure Automation runbook. A service principal is an identity that is created for use by a specific application or automation tool, and it allows the application to authenticate and access Azure resources.', 'SCHEMA', N'flw', 'TABLE', N'Invoke', 'COLUMN', N'trgServicePrincipalAlias'
GO
