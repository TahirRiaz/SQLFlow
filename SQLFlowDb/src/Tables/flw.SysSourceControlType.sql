CREATE TABLE [flw].[SysSourceControlType]
(
[SourceControlTypeID] [int] NOT NULL IDENTITY(1, 1),
[SourceControlType] [nvarchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ServicePrincipalAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SCAlias] [nvarchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Username] [nvarchar] (255) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[AccessToken] [nvarchar] (255) COLLATE SQL_Latin1_General_CP1_CI_AS MASKED WITH (FUNCTION = 'default()') NULL,
[AccessTokenSecretName] [nvarchar] (255) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ConsumerKey] [nvarchar] (255) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ConsumerSecret] [nvarchar] (255) COLLATE SQL_Latin1_General_CP1_CI_AS MASKED WITH (FUNCTION = 'default()') NULL,
[ConsumerSecretName] [nvarchar] (255) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[WorkSpaceName] [nvarchar] (255) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ProjectName] [nvarchar] (255) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ProjectKey] [nvarchar] (25) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[CreateWrkProjRepo] [bit] NULL CONSTRAINT [DF_SysSourceControlType_CreateWrkProjRepo] DEFAULT ((0))
) ON [PRIMARY]
GO
ALTER TABLE [flw].[SysSourceControlType] ADD CONSTRAINT [PK_GitHublID] PRIMARY KEY CLUSTERED ([SourceControlTypeID]) ON [PRIMARY]
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysSourceControlType] table is a lookup table that stores various attributes required for the authentication and connection to a source control system. ', 'SCHEMA', N'flw', 'TABLE', N'SysSourceControlType', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysSourceControlType].[AccessToken]', 'SCHEMA', N'flw', 'TABLE', N'SysSourceControlType', 'COLUMN', N'AccessToken'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The ConsumerKey column in the [flw].[SysSourceControlType] table stores the consumer key required for authentication and connection to the source control system. This column may be used with certain source control types that require the use of a consumer key, such as OAuth-based systems.', 'SCHEMA', N'flw', 'TABLE', N'SysSourceControlType', 'COLUMN', N'ConsumerKey'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysSourceControlType].[ConsumerSecret] column in the flw.SysSourceControlType table stores a consumer secret required for authentication and connection to a source control service.', 'SCHEMA', N'flw', 'TABLE', N'SysSourceControlType', 'COLUMN', N'ConsumerSecret'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysSourceControlType].[CreateWrkProjRepo] column is a bit field that specifies whether a new workspace, project, and repository should be created automatically when connecting to the source control. If this column is set to 1, the SQLFlow system will create the workspace, project, and repository as needed during the connection process.', 'SCHEMA', N'flw', 'TABLE', N'SysSourceControlType', 'COLUMN', N'CreateWrkProjRepo'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The ProjectKey column in the flw.SysSourceControlType table stores the unique identifier for a project in a source control system, such as GitHub or Bitbucket. It is used in conjunction with the Username and AccessToken or ConsumerKey and ConsumerSecret columns to authenticate and authorize access to the specified project. The ProjectKey value is typically obtained from the URL of the project in the source control system, and is used by the SQLFlow process to identify and retrieve the relevant source code files.', 'SCHEMA', N'flw', 'TABLE', N'SysSourceControlType', 'COLUMN', N'ProjectKey'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysSourceControlType].[ProjectName] column stores the name of the project associated with the source control type.', 'SCHEMA', N'flw', 'TABLE', N'SysSourceControlType', 'COLUMN', N'ProjectName'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysSourceControlType].[SCAlias] column stores a user-defined alias for the source control type, which can be used for easier identification and reference in the SQLFlow process.', 'SCHEMA', N'flw', 'TABLE', N'SysSourceControlType', 'COLUMN', N'SCAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysSourceControlType].[SourceControlType] column is a string column that stores the type of source control system being used, e.g. BitBucket, GitHub', 'SCHEMA', N'flw', 'TABLE', N'SysSourceControlType', 'COLUMN', N'SourceControlType'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysSourceControlType].[SourceControlTypeID] is an auto-incrementing integer column and the primary key of the table. It uniquely identifies each row in the table.', 'SCHEMA', N'flw', 'TABLE', N'SysSourceControlType', 'COLUMN', N'SourceControlTypeID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The Username column in the [flw].[SysSourceControlType] table stores the username required for authentication to the source control system. For example, this could be the username required for connecting to a GitHub repository.', 'SCHEMA', N'flw', 'TABLE', N'SysSourceControlType', 'COLUMN', N'Username'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysSourceControlType].[WorkSpaceName]', 'SCHEMA', N'flw', 'TABLE', N'SysSourceControlType', 'COLUMN', N'WorkSpaceName'
GO
