CREATE TABLE [flw].[Parameter]
(
[ParameterID] [int] NOT NULL IDENTITY(1, 1),
[FlowID] [int] NOT NULL,
[ParamAltServer] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ParamName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SelectExp] [nvarchar] (4000) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[PreFetch] [bit] NULL CONSTRAINT [DF_Parameter_PreFetch] DEFAULT ((0)),
[Defaultvalue] [varchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_Parameter_Defaultvalue] DEFAULT ((0))
) ON [PRIMARY]
GO
ALTER TABLE [flw].[Parameter] ADD CONSTRAINT [PK_SPParameterID] PRIMARY KEY CLUSTERED ([ParameterID]) ON [PRIMARY]
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PrePostSPParameter] table is used to store information about the dynamic parameters of the stored procedures that are registered in the [flw].[PrePostSP] table. ', 'SCHEMA', N'flw', 'TABLE', N'Parameter', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PrePostSPParameter].[FlowID] column is an integer column that stores the ID of the flow to which the stored procedure parameter belongs. This column is used to link the parameters with the corresponding stored procedures defined in the [flw].[PrePostSP] table.', 'SCHEMA', N'flw', 'TABLE', N'Parameter', 'COLUMN', N'FlowID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PrePostSPParameter].[ParamAltServer] column in the flw.PrePostSPParameter table is a column that stores an Alias of the server from where the values will be fetched. The fetched values will be passed to the stored procedure specified in the [flw].[PrePostSP] table. If this column is null, the parameter value will be fetched from the server defined in [flw].[PrePostSP]', 'SCHEMA', N'flw', 'TABLE', N'Parameter', 'COLUMN', N'ParamAltServer'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PrePostSPParameter].[SPParameterID] column is an integer identity column that uniquely identifies each row in the table. It serves as the primary key of the table.', 'SCHEMA', N'flw', 'TABLE', N'Parameter', 'COLUMN', N'ParameterID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [ParamName] column in [flw].[PrePostSPParameter] table stores the name of the parameter for a stored procedure. It is a required field and cannot be null.', 'SCHEMA', N'flw', 'TABLE', N'Parameter', 'COLUMN', N'ParamName'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The column [flw].[PrePostSPParameter].[SelectExp] contains the T-SQL select expression for the dynamic parameter input of the stored procedure. This expression can be used to compute the value of the stored procedure''s input parameter dynamically based on the data in the source tables.', 'SCHEMA', N'flw', 'TABLE', N'Parameter', 'COLUMN', N'SelectExp'
GO
