CREATE TABLE [dbo].[ReportFlowHealthCheck]
(
[RecID] [int] NOT NULL IDENTITY(1, 1),
[HealthCheckID] [int] NOT NULL,
[DateColumn] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[BaseValueExp] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[FilterCriteria] [nvarchar] (4000) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[MLModelName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[MLModelSelection] [nvarchar] (4000) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[MLModelDate] [datetime] NULL,
[ResultDate] [datetime] NULL,
[FlowID] [int] NOT NULL,
[Date] [date] NULL,
[BaseValue] [int] NULL,
[BaseValueAdjusted] [int] NULL,
[PredictedValue] [int] NULL,
[IsNoData] [bit] NULL,
[Year] [int] NULL,
[Quarter] [int] NULL,
[WeekOfYear] [int] NULL,
[MonthNumber] [int] NULL,
[DayOfWeekNumber] [int] NULL,
[IsWeekend] [bit] NULL,
[IsHoliday] [bit] NULL
)
GO
ALTER TABLE [dbo].[ReportFlowHealthCheck] ADD CONSTRAINT [PK_ReportFlowHealthCheck] PRIMARY KEY CLUSTERED ([RecID])
GO
