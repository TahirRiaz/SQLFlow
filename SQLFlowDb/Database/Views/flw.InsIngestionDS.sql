SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

CREATE VIEW [flw].[InsIngestionDS]
AS
SELECT        SysAlias, srcServer, srcDBSchTbl, trgServer, trgDBSchTbl, KeyColumns, DateColumn, Batch
FROM            flw.Ingestion
GO
