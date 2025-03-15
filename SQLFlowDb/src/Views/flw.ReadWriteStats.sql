SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE View [flw].[ReadWriteStats]
as
WITH reads_and_writes AS (
	SELECT db.name AS DBName,
		ob.name as ObjectName,
		ob.type_desc ObjectType,
		Sum(user_seeks + user_scans + user_lookups) AS Reads,
		Sum(user_updates) AS Writes,
		sum(user_seeks + user_scans + user_lookups + user_updates) AS AllActivity
	FROM sys.dm_db_index_usage_stats us
	INNER JOIN sys.databases db ON us.database_id = db.database_id
	INNER JOIN sys.objects ob ON ob.object_id = us.object_id
	Where db.name = db_name()
GROUP BY db.name, ob.name, ob.type_desc
)
SELECT ObjectName,ObjectType, Reads, 
		Writes,
		ReadsPercent = CAST(IIF(AllActivity <> 0, ((reads * 1.0) / AllActivity), 0) * 100  as decimal(12,1)),
		WritesPercent = CAST(IIF(AllActivity <> 0, ((writes * 1.0) / AllActivity), 0) * 100 as decimal(12,1))
	FROM reads_and_writes rw
	--ORDER BY ObjectName;
GO
