CREATE TABLE [flw].[LineageObjectRelation]
(
[ObjectRelationID] [int] NOT NULL IDENTITY(1, 1),
[LeftObject] [nvarchar] (255) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[LeftObjectCol] [nvarchar] (255) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[RightObject] [nvarchar] (255) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[RightObjectCol] [nvarchar] (255) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ManualEntry] [bit] NULL CONSTRAINT [DF_LineageObjectRelation_ManualEntry] DEFAULT ((0))
)
GO
ALTER TABLE [flw].[LineageObjectRelation] ADD CONSTRAINT [PK_LineageObjectRelation] PRIMARY KEY CLUSTERED ([ObjectRelationID])
GO
CREATE NONCLUSTERED INDEX [NCI_LeftObject] ON [flw].[LineageObjectRelation] ([LeftObject])
GO
CREATE NONCLUSTERED INDEX [NCI_RightObject] ON [flw].[LineageObjectRelation] ([RightObject])
GO
