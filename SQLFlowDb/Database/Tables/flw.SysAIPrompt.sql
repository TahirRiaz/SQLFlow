CREATE TABLE [flw].[SysAIPrompt]
(
[PromptID] [int] NOT NULL IDENTITY(1, 1),
[ApiKeyAlias] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PromptName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PayLoadJson] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[RunOrder] [int] NULL
) ON [PRIMARY]
GO
ALTER TABLE [flw].[SysAIPrompt] ADD CONSTRAINT [PK_PromptID] PRIMARY KEY CLUSTERED ([PromptID]) ON [PRIMARY]
GO
