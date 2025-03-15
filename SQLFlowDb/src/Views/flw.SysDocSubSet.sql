SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE VIEW [flw].[SysDocSubSet]
AS

SELECT [SysDocID] AS  [SysDocSubsetID]
      ,[ObjectName]
      ,[ObjectType]
      ,ISNULL([Question],[ObjectName]) AS [Question]
      ,ISNULL([Label],[Label])  AS [Label]
  FROM [flw].[SysDoc]
GO
