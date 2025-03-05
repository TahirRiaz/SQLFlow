CREATE TABLE [flw].[SysAlias]
(
[SystemID] [int] NOT NULL IDENTITY(1, 1),
[System] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SysAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Description] [nvarchar] (2000) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Owner] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DomainExpert] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL
)
GO
ALTER TABLE [flw].[SysAlias] ADD CONSTRAINT [PK_SysAlias] PRIMARY KEY CLUSTERED ([SystemID])
GO
CREATE UNIQUE NONCLUSTERED INDEX [NCI_SystemAlias] ON [flw].[SysAlias] ([SysAlias])
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysAlias] table is used to store system aliases for various databases or servers. These aliases can be used to tag and group processes, making it easier to manage and organize them.  Please note that this table is not used to enforce referential integrity', 'SCHEMA', N'flw', 'TABLE', N'SysAlias', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysAlias].[Description] stores a description of the system alias, which is a textual description of what the system alias represents or is used for. It can be used to provide additional information or context about the system alias.', 'SCHEMA', N'flw', 'TABLE', N'SysAlias', 'COLUMN', N'Description'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The DomainExpert column in the flw.SysAlias table stores the name of the domain expert who is responsible for the system or server associated with the SysAlias. This column is used to keep track of ownership and responsibility of the systems or servers that are being tagged using the system aliases.', 'SCHEMA', N'flw', 'TABLE', N'SysAlias', 'COLUMN', N'DomainExpert'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysAlias].[Owner] column stores the name of the owner or team responsible for the system or server represented by the alias.', 'SCHEMA', N'flw', 'TABLE', N'SysAlias', 'COLUMN', N'Owner'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysAlias].[SysAlias] column stores the arbitrary system alias for a specific database or server. It can be used to group and tag processes together.', 'SCHEMA', N'flw', 'TABLE', N'SysAlias', 'COLUMN', N'SysAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The System column in the [flw].[SysAlias] table stores the name of a database or server that is being aliased. For example, if a server named ServerA needs to be aliased, then ServerA would be stored in the System column.', 'SCHEMA', N'flw', 'TABLE', N'SysAlias', 'COLUMN', N'System'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysAlias].[SystemID]', 'SCHEMA', N'flw', 'TABLE', N'SysAlias', 'COLUMN', N'SystemID'
GO
