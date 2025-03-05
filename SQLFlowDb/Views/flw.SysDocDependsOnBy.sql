SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO



CREATE VIEW [flw].[SysDocDependsOnBy]
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
    OPENJSON(a.[DependsOnByJson]) 
    WITH (
        RootObject NVARCHAR(150) '$.RootObject',
        [Database] NVARCHAR(150) '$.Database',
        [Schema] NVARCHAR(150) '$.Schema',
        [Name] NVARCHAR(150) '$.Name',
		[Type] NVARCHAR(150) '$.Type'
    ) AS j
WHERE LEN([DependsOnByJson]) > 4
AND j.[Type] <> 'UnresolvedEntity'
GO
