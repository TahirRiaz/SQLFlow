CREATE TABLE [flw].[SysCFG]
(
[ParamName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[ParamValue] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS MASKED WITH (FUNCTION = 'default()') NULL,
[ParamJsonValue] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS MASKED WITH (FUNCTION = 'default()') NULL,
[Description] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL
) ON [PRIMARY]
GO
ALTER TABLE [flw].[SysCFG] ADD CONSTRAINT [PK_ParamName] PRIMARY KEY CLUSTERED ([ParamName]) ON [PRIMARY]
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysCFG] is a table that stores system configuration parameters. It is used to define various settings that affect the behavior of SQLFlow.', 'SCHEMA', N'flw', 'TABLE', N'SysCFG', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysCFG].[Description] column is a description of the system configuration parameter specified in the [flw].[SysCFG].[ParamName] column. It provides additional information and context about the purpose and usage of the parameter.', 'SCHEMA', N'flw', 'TABLE', N'SysCFG', 'COLUMN', N'Description'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysCFG].[ParamName] is a column in the flw.SysCFG table that stores the name of a system configuration parameter. This table is used to store various system-wide parameters, such as connection strings, timeout durations, and other settings that are used by the SQLFlow system. The ParamName column is the primary key of the flw.SysCFG table and is used to uniquely identify each configuration parameter.', 'SCHEMA', N'flw', 'TABLE', N'SysCFG', 'COLUMN', N'ParamName'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysCFG].[ParamValue] stores the value of the configuration parameter specified in [flw].[SysCFG].[ParamName]. These values can be modified based on the specific needs of the SQLFlow system.', 'SCHEMA', N'flw', 'TABLE', N'SysCFG', 'COLUMN', N'ParamValue'
GO
