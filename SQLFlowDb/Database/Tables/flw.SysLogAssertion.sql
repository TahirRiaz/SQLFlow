CREATE TABLE [flw].[SysLogAssertion]
(
[RecID] [int] NOT NULL IDENTITY(1, 1),
[FlowID] [int] NULL,
[AssertionID] [int] NULL,
[AssertionDate] [datetime] NULL,
[AssertionSqlCmd] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Result] [varchar] (255) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[AssertedValue] [varchar] (255) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[TraceLog] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL
) ON [PRIMARY]
GO
ALTER TABLE [flw].[SysLogAssertion] ADD CONSTRAINT [PK_SysLogAssertion] PRIMARY KEY CLUSTERED ([RecID]) ON [PRIMARY]
GO
