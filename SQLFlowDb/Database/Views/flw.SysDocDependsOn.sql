SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO



CREATE VIEW [flw].[SysDocDependsOn]
AS

SELECT DISTINCT
    a.[ObjectName],
	a.ObjectType,
	j.RootObject,
	j.[Type],
    j.[Database],
    j.[Schema],
    j.[Name]
FROM 
    [flw].[SysDoc] a
CROSS APPLY 
    OPENJSON(    a.[DependsOnJson]) 
    WITH (
        RootObject NVARCHAR(100) '$.RootObject',
        [Database] NVARCHAR(100) '$.Database',
        [Schema] NVARCHAR(100) '$.Schema',
        [Name] NVARCHAR(100) '$.Name',
		[Type] NVARCHAR(100) '$.Type'
    ) AS j
WHERE LEN([DependsOnJson]) > 1
AND j.[Type] <> 'UnresolvedEntity'
GO
