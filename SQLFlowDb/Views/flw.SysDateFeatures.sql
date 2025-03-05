SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

CREATE VIEW [flw].[SysDateFeatures]
AS
SELECT 
       [Date]
      ,CAST([Year] AS FLOAT) AS [Year] 
      ,CAST(REPLACE([Quarter],'Q','') AS FLOAT)  AS [Quarter]
      ,CAST([WeekOfYear] AS FLOAT) [WeekOfYear]
	  ,CAST([MonthNumber] AS FLOAT) [MonthNumber]
      ,CAST([DayOfWeekNumber] AS FLOAT) [DayOfWeekNumber]
      ,CAST([IsWeekend] AS FLOAT) [IsWeekend]
      ,CAST(ISNULL([IsHoliday],0) AS FLOAT)  AS [IsHoliday]
FROM [flw].[SysPeriod]
GO
