SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO






CREATE VIEW [flw].[ReportFlowHealthCheck]
AS
SELECT CAST(ROW_NUMBER() OVER (ORDER BY a.[HealthCheckID],test.[Date]) AS INT) AS RowNum,
a.[HealthCheckID],
       a.HealthCheckName,
	   trgObject,
       a.DateColumn,
       a.BaseValueExp,
       a.FilterCriteria,
       a.MLModelName,
       a.MLModelSelection,
       a.MLModelDate,
       a.ResultDate,
       a.FlowID,
       test.[Date],
       AnomalyDetected,
       [BaseValue],
       CAST([PredictedValue] AS INT) AS [PredictedValue],
       CAST([BaseValueAdjusted] AS INT) AS [BaseValueAdjusted],
       [IsNoData],
       [Year],
       [Quarter],
       [WeekOfYear],
       [MonthNumber],
       [DayOfWeekNumber],
       [IsWeekend],
       [IsHoliday]
FROM [flw].[HealthCheck] a
    CROSS APPLY
(
    SELECT *
    FROM
        OPENJSON([Result])
        WITH
        (
            trgObject  NVARCHAR(255) '$.trgObject',
			[Date] DATE '$.date',
            BaseValue INT '$.baseValue',
            BaseValueAdjusted FLOAT '$.baseValueAdjusted',
            PredictedValue FLOAT '$.predictedValue',
            IsNoData BIT '$.isNoData',
            AnomalyDetected BIT '$.anomalyDetected',
            Year INT '$.year',
            Quarter INT '$.quarter',
            WeekOfYear INT '$.weekOfYear',
            MonthNumber INT '$.monthNumber',
            DayOfWeekNumber INT '$.dayOfWeekNumber',
            IsWeekend BIT '$.isWeekend',
            IsHoliday BIT '$.isHoliday'
        )
) AS test;
GO
