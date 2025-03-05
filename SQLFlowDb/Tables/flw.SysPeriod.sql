CREATE TABLE [flw].[SysPeriod]
(
[PeriodID] [int] NOT NULL,
[Date] [date] NULL,
[DayOfMonth] [int] NULL,
[DayOfWeekName] [nvarchar] (25) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DayOfWeekNameShort] [nvarchar] (5) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DayOfWeekNumber] [int] NULL,
[WeekOfYear] [int] NULL,
[MonthNumber] [int] NULL,
[MonthName] [nvarchar] (25) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[MonthNameShort] [nvarchar] (5) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[MonthNumName] [nvarchar] (10) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Quarter] [nvarchar] (5) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Year] [int] NULL,
[IsWeekend] [bit] NULL,
[IsLeapYear] [bit] NULL,
[IsLastDayOfMonth] [bit] NULL,
[FiscalWeekOfYear] [int] NULL,
[FiscalMonth] [int] NULL,
[FiscalQuarter] [int] NULL,
[FiscalYear] [int] NULL,
[IsHoliday] [bit] NULL,
[HolidayName] [nvarchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Season] [nvarchar] (25) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DaylightSavingTime] [bit] NULL,
[ISOWeekNumber] [int] NULL
)
GO
ALTER TABLE [flw].[SysPeriod] ADD CONSTRAINT [PK_SysDimPeriod] PRIMARY KEY CLUSTERED ([PeriodID])
GO
