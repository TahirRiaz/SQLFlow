CREATE TABLE [flw].[SysDoc]
(
[SysDocID] [int] NOT NULL IDENTITY(1, 1),
[ObjectName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ObjectType] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Description] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Summary] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Question] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Label] [nvarchar] (500) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[AdditionalInfo] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ObjectDef] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[RelationJson] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DependsOnJson] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DependsOnByJson] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DescriptionOld] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PayLoadJson] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ScriptDate] [datetime] NULL CONSTRAINT [DF_SysDoc_ScriptDate] DEFAULT (getdate()),
[PromptDescription] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PromptSummary] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PromptQuestion] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ScriptGenID] [bigint] NULL
) ON [PRIMARY]
GO
ALTER TABLE [flw].[SysDoc] ADD CONSTRAINT [PK_SysDocID] PRIMARY KEY CLUSTERED ([SysDocID]) ON [PRIMARY]
GO
CREATE UNIQUE NONCLUSTERED INDEX [NIC_UNIQUE_ObjectName] ON [flw].[SysDoc] ([ObjectName]) ON [PRIMARY]
GO
