CREATE TABLE [flw].[SysLogMatchKey]
(
[SysLogMatchKey] [int] NOT NULL IDENTITY(1, 1),
[MatchKeyID] [int] NOT NULL,
[FlowID] [int] NOT NULL,
[BatchID] [int] NULL,
[SysAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Batch] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[StartTime] [datetime] NULL,
[EndTime] [datetime] NULL,
[Status] [bit] NULL CONSTRAINT [DF_SysLogMatchKey_Status] DEFAULT ((0)),
[DurationMatch] [int] NULL,
[DurationPre] [int] NULL,
[DurationPost] [int] NULL,
[SrcRowCount] [int] NULL,
[SrcDelRowCount] [int] NULL,
[TrgRowCount] [int] NULL,
[TrgDelRowCount] [int] NULL,
[TaggedRowCount] [int] NULL,
[ErrorMessage] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[TraceLog] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL
) ON [PRIMARY]
GO
ALTER TABLE [flw].[SysLogMatchKey] ADD CONSTRAINT [PK_SysLogMatchKey] PRIMARY KEY CLUSTERED ([SysLogMatchKey]) ON [PRIMARY]
GO
