CREATE TABLE [flw].[SysFlowNote]
(
[FlowNoteID] [int] NOT NULL IDENTITY(1, 1),
[FlowID] [int] NOT NULL,
[FlowNoteType] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Title] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Description] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Resolved] [bit] NULL CONSTRAINT [DF_SysFlowNote_Resolved] DEFAULT ((0)),
[Created] [datetime] NULL CONSTRAINT [DF_SysFlowNotes_Created] DEFAULT (getdate()),
[CreatedBy] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_SysFlowNotes_CreatedBy] DEFAULT (user_name())
) ON [PRIMARY]
GO
ALTER TABLE [flw].[SysFlowNote] ADD CONSTRAINT [PK_FlowNoteID] PRIMARY KEY CLUSTERED ([FlowNoteID]) ON [PRIMARY]
GO
