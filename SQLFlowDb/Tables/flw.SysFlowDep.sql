CREATE TABLE [flw].[SysFlowDep]
(
[RecID] [int] NOT NULL IDENTITY(1, 1),
[FlowID] [int] NOT NULL,
[FlowType] [varchar] (25) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[Step] [int] NULL,
[DepFlowID] [int] NULL,
[DepFlowIDStep] [int] NULL,
[ExecDep] [varchar] (4000) COLLATE SQL_Latin1_General_CP1_CI_AS NULL
)
GO
ALTER TABLE [flw].[SysFlowDep] ADD CONSTRAINT [PK_SysFlowDep] PRIMARY KEY CLUSTERED ([RecID])
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysFlowDep] is a table that contains the execution order of various processes within the SQLFlow framework. It is used to provide the execution order for batches.', 'SCHEMA', N'flw', 'TABLE', N'SysFlowDep', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', 'The ID of the dependent flow that needs to be executed before the current flow.', 'SCHEMA', N'flw', 'TABLE', N'SysFlowDep', 'COLUMN', N'DepFlowID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysFlowDep].[DepFlowIDStep] column is used to specify the step number of the dependent flow identified in the [flw].[SysFlowDep].[DepFlowID] column. It indicates the step within the dependent flow that must complete before the current flow can start. This enables the SQLFlow framework to determine the correct execution order of various processes.', 'SCHEMA', N'flw', 'TABLE', N'SysFlowDep', 'COLUMN', N'DepFlowIDStep'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysFlowDep].[ExecDep] contains the flow path for the execution of the processes within the SQLFlow framework, based on the calculated execution order in the table.', 'SCHEMA', N'flw', 'TABLE', N'SysFlowDep', 'COLUMN', N'ExecDep'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The FlowID column in [flw].[SysFlowDep] refers to the ID of the flow or process within the SQLFlow framework. It is used to identify the specific flow or process for which the execution order is being determined.', 'SCHEMA', N'flw', 'TABLE', N'SysFlowDep', 'COLUMN', N'FlowID'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysFlowDep].[FlowType] column in the flw.SysFlowDep table stores the type of the flow, which is used to differentiate different types of objects. Some examples of valid FlowType values are ado, csv, exp, ing, inv, sp, xls, and xml.', 'SCHEMA', N'flw', 'TABLE', N'SysFlowDep', 'COLUMN', N'FlowType'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysFlowDep].[RecID] is an auto-incrementing identity column used as the primary key of the table. It provides a unique identifier for each row in the table.', 'SCHEMA', N'flw', 'TABLE', N'SysFlowDep', 'COLUMN', N'RecID'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysFlowDep].[Step]', 'SCHEMA', N'flw', 'TABLE', N'SysFlowDep', 'COLUMN', N'Step'
GO
