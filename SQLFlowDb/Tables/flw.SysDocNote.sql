CREATE TABLE [flw].[SysDocNote]
(
[DocNoteID] [int] NOT NULL IDENTITY(1, 1),
[ObjectName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[Title] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Description] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Created] [datetime] NULL CONSTRAINT [DF_SysDocNotes_Created] DEFAULT (getdate()),
[CreatedBy] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_SysDocNotes_CreatedBy] DEFAULT (user_name())
)
GO
ALTER TABLE [flw].[SysDocNote] ADD CONSTRAINT [PK_DocNoteID] PRIMARY KEY CLUSTERED ([DocNoteID])
GO
