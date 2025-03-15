CREATE TABLE [flw].[DataSubscriber]
(
[FlowID] [int] NOT NULL CONSTRAINT [DF_DataSubscriber_FlowID] DEFAULT (NEXT VALUE FOR [flw].[FlowID]),
[FlowType] [varchar] (25) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_DataSubscriber_FlowType] DEFAULT ('sub'),
[SubscriberType] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SubscriberName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Batch] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_DataSubscriber_Batch] DEFAULT (N'sub'),
[FromObjectMK] [int] NULL,
[ToObjectMK] [int] NULL,
[CreatedBy] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[CreatedDate] [datetime] NULL CONSTRAINT [DF_DataSubscriber_CreatedDate] DEFAULT (getdate())
) ON [PRIMARY]
GO
SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE TRIGGER [flw].[AddSubToLogTable]
ON [flw].[DataSubscriber]
FOR INSERT
AS
INSERT INTO flw.SysLog
(
    FlowID,
    [FlowType],
    [Process],
    [ProcessShort],
    [Batch],
    [SysAlias],
    Success
)
SELECT FlowID,
       FlowType,
       '-->' + [SubscriberName] AS [Process],
       '-->' + [SubscriberName] AS [ProcessShort],
       [Batch],
       [Batch],
       1
FROM inserted;
GO
ALTER TABLE [flw].[DataSubscriber] ADD CONSTRAINT [PK_DataSubscriber] PRIMARY KEY CLUSTERED ([FlowID]) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [NCI_SubscriberName] ON [flw].[DataSubscriber] ([SubscriberName]) ON [PRIMARY]
GO
