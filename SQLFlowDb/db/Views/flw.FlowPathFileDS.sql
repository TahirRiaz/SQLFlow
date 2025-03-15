SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

CREATE VIEW [flw].[FlowPathFileDS]
AS
SELECT ds.FlowID,
       ds.FlowType,
       ds.SysAlias,
       ds.Batch,
       ds.[srcPath],
       ds.[srcPathMask],
       ds.[SearchSubDirectories],
       ds.[srcFile],
       ds.EnableEventExecution
FROM [flw].[PreIngestionCSV] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
WHERE ISNULL(ds.EnableEventExecution, 0) = 1
UNION
SELECT ds.FlowID,
       ds.FlowType,
       ds.SysAlias,
       ds.Batch,
       ds.[srcPath],
       ds.[srcPathMask],
       ds.[SearchSubDirectories],
       ds.[srcFile],
       ds.EnableEventExecution
FROM [flw].[PreIngestionJSN] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
WHERE ISNULL(ds.EnableEventExecution, 0) = 1
UNION
SELECT ds.FlowID,
       ds.FlowType,
       ds.SysAlias,
       ds.Batch,
       ds.[srcPath],
       ds.[srcPathMask],
       ds.[SearchSubDirectories],
       ds.[srcFile],
       ds.EnableEventExecution
FROM [flw].[PreIngestionPRQ] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
WHERE ISNULL(ds.EnableEventExecution, 0) = 1
UNION
SELECT ds.FlowID,
       ds.FlowType,
       ds.SysAlias,
       ds.Batch,
       ds.[srcPath],
       ds.[srcPathMask],
       ds.[SearchSubDirectories],
       ds.[srcFile],
       ds.EnableEventExecution
FROM [flw].[PreIngestionXLS] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
WHERE ISNULL(ds.EnableEventExecution, 0) = 1
UNION
SELECT ds.FlowID,
       ds.FlowType,
       ds.SysAlias,
       ds.Batch,
       ds.[srcPath],
       ds.[srcPathMask],
       ds.[SearchSubDirectories],
       ds.[srcFile],
       ds.EnableEventExecution
FROM [flw].[PreIngestionXML] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
WHERE ISNULL(ds.EnableEventExecution, 0) = 1;
GO
