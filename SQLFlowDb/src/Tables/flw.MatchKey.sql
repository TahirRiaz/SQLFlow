CREATE TABLE [flw].[MatchKey]
(
[MatchKeyID] [int] NOT NULL IDENTITY(1, 1),
[FlowID] [int] NULL CONSTRAINT [DF_MatchKey_FlowID] DEFAULT ((0)),
[Batch] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SysAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[srcServer] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[srcDatabase] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[srcSchema] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcObject] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[trgServer] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[trgDBSchTbl] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[DeactivateFromBatch] [bit] NULL,
[KeyColumns] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DateColumn] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ActionType] [nvarchar] (20) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ActionThresholdPercent] [int] NULL CONSTRAINT [DF_MatchKey_ActionThresholdPercent] DEFAULT ((20)),
[IgnoreDeletedRowsAfter] [int] NULL,
[srcFilter] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[trgFilter] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[OnErrorResume] [bit] NULL,
[PreProcessOnTrg] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PostProcessOnTrg] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Description] [nvarchar] (2048) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ToObjectMK] [int] NULL,
[CreatedBy] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[CreatedDate] [datetime] NULL
) ON [PRIMARY]
GO
ALTER TABLE [flw].[MatchKey] ADD CONSTRAINT [PK_MatchKey] PRIMARY KEY CLUSTERED ([MatchKeyID]) ON [PRIMARY]
GO
