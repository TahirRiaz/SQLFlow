CREATE TABLE [flw].[SysSourceControl]
(
[SourceControlID] [int] NOT NULL IDENTITY(1, 1),
[Batch] [nvarchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SCAlias] [nvarchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[Server] [nvarchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[DBName] [nvarchar] (255) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[RepoName] [nvarchar] (255) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ScriptToPath] [nvarchar] (255) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ScriptDataForTables] [nvarchar] (4000) COLLATE SQL_Latin1_General_CP1_CI_AS NULL
) ON [PRIMARY]
GO
ALTER TABLE [flw].[SysSourceControl] ADD CONSTRAINT [PK_SysSourceControl] PRIMARY KEY CLUSTERED ([SourceControlID]) ON [PRIMARY]
GO
EXEC sp_addextendedproperty N'MS_Description', 'The flw.SysSourceControl table is used to store metadata about databases and target source control repository. The synchronization commits changes to database roles, schemas, sequences, stored procedures, synonyms, tables, user-defined functions, views, and data for selected tables.', 'SCHEMA', N'flw', 'TABLE', N'SysSourceControl', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysSourceControl].[Batch] column stores the batch name of the synchronization process for the corresponding entry in the table.', 'SCHEMA', N'flw', 'TABLE', N'SysSourceControl', 'COLUMN', N'Batch'
GO
EXEC sp_addextendedproperty N'MS_Description', 'This column stores the name of the database that is being synchronized with the target source control repository.', 'SCHEMA', N'flw', 'TABLE', N'SysSourceControl', 'COLUMN', N'DBName'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysSourceControl].[RepoName]', 'SCHEMA', N'flw', 'TABLE', N'SysSourceControl', 'COLUMN', N'RepoName'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysSourceControl].[SCAlias] column in the flw.SysSourceControl table is a non-nullable string column that stores the alias for the source control type. It is used as a foreign key to reference the flw.SysSourceControlType table, which contains additional metadata required for authentication and connection to the source control system. The SCAlias is typically used to identify the type of source control system being used, such as Git or Subversion.', 'SCHEMA', N'flw', 'TABLE', N'SysSourceControl', 'COLUMN', N'SCAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysSourceControl].[ScriptDataForTables]', 'SCHEMA', N'flw', 'TABLE', N'SysSourceControl', 'COLUMN', N'ScriptDataForTables'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The ScriptToPath column in the flw.SysSourceControl table stores the path to a local folder that can be used to store the scripted files for the current database during the synchronization process. ', 'SCHEMA', N'flw', 'TABLE', N'SysSourceControl', 'COLUMN', N'ScriptToPath'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The Server column in [flw].[SysSourceControl] table stores the name of the SQL Server instance where the source database resides.', 'SCHEMA', N'flw', 'TABLE', N'SysSourceControl', 'COLUMN', N'Server'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysSourceControl].[SourceControlID] is an auto-incrementing primary key column that uniquely identifies each record in the SysSourceControl table.



', 'SCHEMA', N'flw', 'TABLE', N'SysSourceControl', 'COLUMN', N'SourceControlID'
GO
