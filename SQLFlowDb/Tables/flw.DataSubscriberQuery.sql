CREATE TABLE [flw].[DataSubscriberQuery]
(
[QueryID] [int] NOT NULL IDENTITY(1, 1),
[FlowID] [int] NOT NULL,
[srcServer] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[QueryName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FullyQualifiedQuery] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL
)
GO
ALTER TABLE [flw].[DataSubscriberQuery] ADD CONSTRAINT [PK_DataSubscriberQuery] PRIMARY KEY CLUSTERED ([QueryID])
GO
