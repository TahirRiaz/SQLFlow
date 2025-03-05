CREATE TABLE [flw].[HealthCheck]
(
[HealthCheckID] [int] NOT NULL IDENTITY(1, 1),
[FlowID] [int] NOT NULL,
[HealthCheckName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DBSchTbl] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DateColumn] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[BaseValueExp] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[FilterCriteria] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[MLMaxExperimentTimeInSeconds] [int] NULL CONSTRAINT [DF_HealthCheck_MLMaxExperimentTimeInSeconds] DEFAULT ((120)),
[MLModelSelection] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[MLModelName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[MLModelDate] [datetime] NULL CONSTRAINT [DF_HealthCheck_MLModelDate] DEFAULT (getdate()),
[MLModel] [varbinary] (max) NULL,
[Result] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ResultDate] [datetime] NULL CONSTRAINT [DF_HealthCheck_ResultDate] DEFAULT (getdate())
) ON [PRIMARY]
GO
ALTER TABLE [flw].[HealthCheck] ADD CONSTRAINT [PK_HealthCheck] PRIMARY KEY CLUSTERED ([HealthCheckID]) ON [PRIMARY]
GO
