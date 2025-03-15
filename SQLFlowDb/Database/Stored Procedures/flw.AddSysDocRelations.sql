SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE PROCEDURE [flw].[AddSysDocRelations]
AS
BEGIN

    SET NOCOUNT ON;

    DELETE FROM [flw].[SysDocRelation]
    WHERE ISNULL([ManualEntry], 0) = 0;

    ;WITH UnpackJson
    AS (SELECT DISTINCT
               ParsedObjectName,
               RelationshipCounter,
               j.CompareOp,
               j.[Database] + '.' + j.[Schema] + '.' + j.[Table] ObjectName,
               j.[Column]
        FROM [flw].[SysDoc] t
            CROSS APPLY
            OPENJSON(t.RelationJson)
            WITH
            (
                ParsedObjectName NVARCHAR(255),
                RelationshipCounter INT,
                CompareOp NVARCHAR(50),
                [Database] NVARCHAR(255),
                [Schema] NVARCHAR(255),
                [Table] NVARCHAR(255),
                [Alias] NVARCHAR(255),
                [Column] NVARCHAR(255)
            ) AS j
        WHERE LEN(t.RelationJson) > 0),
          BASE
    AS (SELECT DISTINCT
               [ParsedObjectName],
               [RelationshipCounter],
               [ObjectName] AS [RootObjectName]
        FROM UnpackJson
          --WHERE [ObjectName] = '[dw-sqlflow-prod].[flw].[SysLogBatch]'
          ),
          FindRelation
    AS (SELECT s.*,
               [RootObjectName],
               CASE
                   WHEN [RootObjectName] = s.ObjectName THEN
                       0
                   ELSE
                       1
               END IsRelation
        FROM BASE b
            INNER JOIN UnpackJson s
                ON b.[ParsedObjectName] = s.[ParsedObjectName]
                   AND b.[RelationshipCounter] = s.[RelationshipCounter]),
          Rel
    AS (SELECT DISTINCT
               [RootObjectName] AS LeftObject,
               STRING_AGG(   (CASE
                                  WHEN [ObjectName] = [RootObjectName] THEN
                                      [Column]
                                  ELSE
                                      ''
                              END
                             ),
                             ''
                         )WITHIN GROUP(ORDER BY IsRelation ASC) LeftObjectCol,
               STRING_AGG(   (CASE
                                  WHEN [ObjectName] = [RootObjectName] THEN
                                      ''
                                  ELSE
                                      [ObjectName]
                              END
                             ),
                             ''
                         )WITHIN GROUP(ORDER BY IsRelation ASC) AS RightObject,
               STRING_AGG(   (CASE
                                  WHEN [ObjectName] = [RootObjectName] THEN
                                      ''
                                  ELSE
                                      [Column]
                              END
                             ),
                             ''
                         )WITHIN GROUP(ORDER BY IsRelation ASC) AS RightObjectCol,
               0 AS [ManualEntry]
        FROM FindRelation
        GROUP BY [ParsedObjectName],
                 [RelationshipCounter],
                 [RootObjectName])
    INSERT INTO [flw].[SysDocRelation]
    (
       [LeftObject] ,
        [LeftObjectCol],
        [RightObject],
        [RightObjectCol],
        [ManualEntry]
    )
    SELECT  QUOTENAME(PARSENAME([LeftObject], 2)) + '.'+ QUOTENAME(PARSENAME([LeftObject], 1))  [LeftObject],
           [LeftObjectCol],
            QUOTENAME(PARSENAME([RightObject], 2)) + '.'+ QUOTENAME(PARSENAME([RightObject], 1))  [RightObject],
           [RightObjectCol],
           [ManualEntry]
    FROM Rel
    WHERE LEN([RightObjectCol]) > 0;

END;
GO
