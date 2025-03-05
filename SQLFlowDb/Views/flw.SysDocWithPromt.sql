SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

CREATE VIEW [flw].[SysDocWithPromt]

AS SELECT ObjectName,
           ObjectType,
           ObjectDef AS Code,
           [flw].[GetSysDocRelations]('[flw].[Ingestion]') Relations,
           [flw].[GetSysDocDependsOn]('[flw].[Ingestion]') AS DependsOn,
           [flw].[GetSysDocDependsOnBy]('[flw].[Ingestion]') AS DependsOnBy,
           ISNULL(DescriptionOld, '') AS OldDescription,
           ISNULL(AdditionalInfo, '') AS AdditionalInfo,
           [PromptName],
           [Temperature],
           [Prompt]
    FROM [flw].[SysDoc]
        CROSS APPLY
    (
        SELECT TOP 1
			PromptName,
			-- Extracting JSON values from PayLoadJson column
			JSON_VALUE(PayLoadJson, '$.model') AS Model,
			JSON_VALUE(PayLoadJson, '$.max_tokens') AS MaxTokens,
			JSON_VALUE(PayLoadJson, '$.temperature') AS Temperature,
			JSON_VALUE(PayLoadJson, '$.top_p') AS TopP,
			JSON_VALUE(PayLoadJson, '$.frequency_penalty') AS FrequencyPenalty,
			JSON_VALUE(PayLoadJson, '$.presence_penalty') AS PresencePenalty,
			JSON_VALUE(PayLoadJson, '$.prompt') AS Prompt
		FROM 
			[flw].[SysAIPrompt]
        WHERE [PromptName] LIKE 'SQLFlow-%'
              AND CHARINDEX(ObjectType, [PromptName]) > 0
       
    ) a
GO
