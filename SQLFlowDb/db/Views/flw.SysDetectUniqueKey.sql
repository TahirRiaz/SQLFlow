SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

--EarlyExitOnFound

CREATE VIEW [flw].[SysDetectUniqueKey]
AS 
SELECT 
    CAST(JSON_VALUE(value, '$.NumberOfRowsToSample') AS INT) AS NumberOfRowsToSample,
    CAST(JSON_VALUE(value, '$.TotalUniqueKeysSought') AS INT) TotalUniqueKeysSought ,
	CAST(JSON_VALUE(value, '$.MaxKeyCombinationSize') AS INT) MaxKeyCombinationSize ,
	TRY_PARSE(JSON_VALUE(value, '$.RedundantColSimilarityThreshold') AS  DECIMAL(12,2) USING 'nn-NO') RedundantColSimilarityThreshold ,
	TRY_PARSE(JSON_VALUE(value, '$.SelectRatioFromTopUniquenessScore') AS  DECIMAL(12,2) USING 'nn-NO') SelectRatioFromTopUniquenessScore ,
	CAST(JSON_VALUE(value, '$.MaxDegreeOfParallelism') AS INT) MaxDegreeOfParallelism ,
	CAST(JSON_VALUE(value, '$.AnalysisMode') AS NVARCHAR(255)) AnalysisMode,
	CAST(JSON_VALUE(value, '$.ExecuteProofQuery') AS BIT) ExecuteProofQuery,
	CAST(JSON_VALUE(value, '$.EarlyExitOnFound') AS BIT) EarlyExitOnFound
	
FROM [flw].[SysCFG]
CROSS APPLY OPENJSON([ParamJsonValue])
WHERE  ParamName = 'SysDetectUniqueKey'
GO
