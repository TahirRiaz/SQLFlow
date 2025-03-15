SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

CREATE VIEW [flw].[ReportAssertion]
AS

SELECT sla.RecID,
       sla.FlowID,
       a.AssertionName,
       sla.AssertionID,
       sla.AssertionDate,
       a.AssertionExp,
       sla.AssertionSqlCmd,
       sla.Result,
	   sla.AssertedValue,
       sla.TraceLog
FROM flw.SysLogAssertion sla
    INNER JOIN flw.Assertion a
        ON sla.AssertionID = a.AssertionID;
GO
