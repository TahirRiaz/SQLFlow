CREATE TABLE [flw].[SysDocRelation]
(
[RelationID] [int] NOT NULL IDENTITY(1, 1),
[LeftObject] [nvarchar] (255) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[LeftObjectCol] [nvarchar] (255) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[RightObject] [nvarchar] (255) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[RightObjectCol] [nvarchar] (255) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ManualEntry] [bit] NULL CONSTRAINT [DF_SysDocRelation_ManualEntry] DEFAULT ((0))
) ON [PRIMARY]
GO
ALTER TABLE [flw].[SysDocRelation] ADD CONSTRAINT [PK_SysDocRelation] PRIMARY KEY CLUSTERED ([RelationID]) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [NCI_LeftObject] ON [flw].[SysDocRelation] ([LeftObject]) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [NCI_ManualEntry] ON [flw].[SysDocRelation] ([ManualEntry]) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [NCI_RightObject] ON [flw].[SysDocRelation] ([RightObject]) ON [PRIMARY]
GO
