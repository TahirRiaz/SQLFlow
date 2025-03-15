SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[LineageParse]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedures purpose is to parse data lineage and resolve execution order
  -- Summary			:	The function uses a common table expression (CTE) named "ParentChildCTE" to recursively retrieve information about lineage relationships between objects in a database.

							The results from the CTE are stored in a table variable named "@Holder". 
							The final results are then inserted into the table variable "@Return" based on the value of "@Expanded" passed into the function.

							If "@Expanded" is 0, then the results are filtered to only include the highest level object for each unique "ToObjectMK" value. 
							If "@Expanded" is 1, then the results are filtered to only include the highest level object for each unique "ToObjectMK" value 
							and all objects with the same "Level" value as the highest level object. 
							
							If "@Expanded" is 2, then the results are not filtered and all objects retrieved by the CTE are included in the final results.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/

CREATE FUNCTION [flw].[LineageParse]
(
    @Expanded INT
)
RETURNS @Return TABLE
(
    RecID INT,
    Virtual BIT,
    FlowID INT,
    FlowType VARCHAR(25),
    FromObjectMK INT,
    [ToObjectMK] INT,
    FromObject VARCHAR(255),
    [ToObject] VARCHAR(255),
    [RootObjectMK] INT,
    [RootObject] VARCHAR(225),
    PathStr VARCHAR(8000),
    Circular BIT,
    PathNum VARCHAR(8000),
    FlowExecDep VARCHAR(8000),
    Step INT,
    [Sequence] INT,
    [Level] INT,
    MaxObjectLevel INT,
    [Priority] INT,
    NoOfChildren INT,
    MaxLevel INT,
    MaxLevelValue INT
)
AS
BEGIN

    /*
DECLARE @RootObjectMK VARCHAR(255);
	SET @RootObjectMK = 'dbo.Konto_Basis';

DECLARE @CollapsRoot BIT;
	SET @CollapsRoot = 1;

DECLARE @Expanded BIT;
	SET @Expanded = 0;
*/

    DECLARE @Holder TABLE
    (
        RecID INT,
        Virtual BIT,
        FlowID INT,
        [ToObjectMK] INT,
        FromObjectMK INT,
        ToObject VARCHAR(250),
        FlowType VARCHAR(25),
        [ObjectType] VARCHAR(50),
        FromObject VARCHAR(4000),
        ParentObjectType VARCHAR(100),
        [RootObjectMK] INT,
        [RootObject] VARCHAR(250),
        PathStr VARCHAR(8000),
        Circular BIT,
        PathNum VARCHAR(8000),
        FlowExecDep VARCHAR(8000),
        [Level] INT,
        Step INT,
        [Sequence] INT,
        MaxObjectLevel INT,
        [Priority] INT,
        NoOfChildren INT,
        MaxLevel INT,
        MaxLevelValue INT,
        Commands VARCHAR(8000)
    );


    ;
    WITH ParentChildCTE (RecID, Virtual, FlowID, [ToObjectMK], FromObjectMK, [ToObject], [FromObject], FlowType,
                         [RootObjectMK], [RootObject], PathStr, Circular, PathNum, FlowExecDep, [Level], [Priority]
                        )
    AS (
       --For Root Element : Merk at vi må snu på parent child verdiene.
       -- Convert(VARCHAR(4000), IsNull(Replace(Str(src.[FromObjectMK], 6), ' ', '0'), '') + '' + Replace(Str(src.ToObjectMK, 6), ' ', '0'))
       SELECT *
       FROM
       (
           SELECT DISTINCT
                  src.RecID,
                  0 AS Virtual,
                  src.FlowID,
                  src.ToObjectMK AS ToObjectMK,
                  CASE
                      WHEN src.ToObjectMK = src.FromObjectMK THEN
                          NULL
                      ELSE
                          src.FromObjectMK
                  END AS FromObjectMK,
                  ToObject AS ToObject,
                  FromObject AS FromObject,
                  FlowType,
                  CAST(src.ToObjectMK AS INT) AS [RootObjectMK],
                  ToObject AS [RootObject],
                  CAST(CONCAT(
                                 ISNULL(src.FromObject, CAST(src.FromObjectMK AS VARCHAR(255))),
                                 IIF(LEN(ISNULL(ISNULL(src.FromObject, CAST(src.FromObjectMK AS VARCHAR(255))), '')) > 0,
                                  '-->',
                                  ''),
                                 ISNULL(src.ToObject, CAST(src.ToObjectMK AS VARCHAR(255)))
                             ) AS VARCHAR(MAX)) AS PathStr,
                  0 AS Circular,
                  CONVERT(
                             VARCHAR(4000),
                             ISNULL(CAST(src.FromObjectMK AS VARCHAR(255)), '')
                             + IIF(LEN(ISNULL(ISNULL(src.FromObject, CAST(src.FromObjectMK AS VARCHAR(255))), '')) > 0,
                                '-',
                                '') + CAST(src.ToObjectMK AS VARCHAR(255))
                         ) AS PathStrNum,
                  CONVERT(VARCHAR(4000), src.FlowID) AS FlowExecDep,
                  1 AS Level,
                  0 AS [Priority]
           FROM flw.LineageEdge src
           WHERE ToObjectMK IN
                 (
                     --Fetch Objects without parents (Used for Stored Procedures)
                     SELECT ToObjectMK AS ToObjectMK
                     FROM flw.LineageEdge
                     WHERE FromObjectMK IS NULL
                 )
           UNION ALL
           SELECT DISTINCT
                  src.RecID,
                  1 AS Virtual,
                  src.FlowID,
                  src.FromObjectMK AS [ToObjectMK],
                  NULL AS [FromObjectMK],
                  FromObject ToObject,
                  NULL AS FromObject,
                  FlowType,
                  CAST(src.FromObjectMK AS INT) AS [RootObjectMK],
                  ISNULL(FromObject, CAST(src.FromObjectMK AS NVARCHAR(255))) AS [RootObject],
                  CAST(CONCAT(ISNULL(src.FromObject, CAST(src.FromObjectMK AS VARCHAR(255))), '') AS VARCHAR(MAX)) AS PathStr,
                  0 AS Circular,
                  CONVERT(VARCHAR(4000), ISNULL(CAST(src.FromObjectMK AS VARCHAR(255)), '') + '') AS PathStrNum,
                  CONVERT(VARCHAR(4000), src.FlowID) AS FlowExecDep, --src.FlowType +
                  0 AS Level,
                  0 AS [Priority]
           FROM flw.LineageEdge src
           WHERE FromObjectMK IN (
                 (
                     --Fetch Parent Items that done appear as Children. Gives Root Objects 
                     SELECT FromObjectMK AS FromObjectMK
                     FROM flw.LineageEdge
                     WHERE FromObjectMK NOT IN
                           (
                               SELECT ToObjectMK AS ToObjectMK
                               FROM flw.LineageEdge
                               WHERE ToObjectMK IS NOT NULL
                           )
                 )
                                 )
       ) a
       UNION ALL
       --SQL For Children - Merk at vi må snu på parent child verdiene.
       SELECT child.RecID,
              CASE
                  WHEN LEN(ISNULL(child.FlowType, '')) > 0 THEN
                      0
                  ELSE
                      1
              END AS Virtual,          -- If FlowType for a child is empty then the row is virtual
              child.FlowID,
              child.ToObjectMK AS ToObjectMK,
              CASE
                  WHEN child.ToObjectMK = child.FromObjectMK THEN
                      NULL
                  ELSE
                      child.FromObjectMK
              END FromObjectMK,
              child.ToObject AS ToObject,
              child.FromObject AS FromObject,
              child.FlowType,
              parent.[RootObjectMK] AS [RootObjectMK],
              parent.[RootObject] AS [RootObject],
              CAST(CONCAT(
                             ISNULL(parent.PathStr, ''),
                             '-->',
                             ISNULL(child.ToObject, CAST(child.ToObjectMK AS VARCHAR(255)))
                         ) AS VARCHAR(MAX)) AS PathStr,
              CASE
                  WHEN PATINDEX('%' + CAST(child.ToObjectMK AS VARCHAR(255)) + '%', parent.PathNum) = 0 THEN
                      0
                  ELSE
                      1
              END AS Circular,
              CONVERT(VARCHAR(4000), parent.PathNum + '-' + CAST(child.ToObjectMK AS VARCHAR(255))) AS PathNum,
              CONVERT(
                         VARCHAR(4000),
                         parent.FlowExecDep
                         + IIF(
                               parent.FlowExecDep <> child.FlowType
                               AND LEN(ISNULL(child.FlowType, '')) > 0,
                               '',
                               '-' + CAST(child.FlowID AS VARCHAR(255)))
                     ) AS FlowExecDep, --+ child.FlowType 
              parent.Level + 1,
              0 AS [Priority]
       FROM ParentChildCTE parent
           INNER JOIN flw.LineageEdge child
               ON child.FromObjectMK = parent.[ToObjectMK]
                  --AND  child.PathNum <> parent.PathNum 
                  --and child.FlowID = parent.FlowId
                  --AND child.[ToObjectMK] = parent.FromObjectMK
                  AND parent.Circular = 0 -- Omit circular loops within the execution plan


    )
    INSERT INTO @Holder
    (
        RecID,
        Virtual,
        FlowID,
        ToObjectMK,
        FromObjectMK,
        ToObject,
        FromObject,
        FlowType,
        [RootObjectMK],
        [RootObject],
        PathStr,
        Circular,
        PathNum,
        FlowExecDep,
        [Level],
        MaxObjectLevel,
        [Priority],
        NoOfChildren,
        MaxLevel
    )
    SELECT RecID,
           Virtual,
           FlowID,
           ToObjectMK,
           FromObjectMK,
           ToObject,
           FromObject,
           FlowType,
           [RootObjectMK],
           [RootObject],
           PathStr,
           Circular,
           PathNum,
           FlowExecDep,
           [Level],
           MaxObjectLevel,
           [Priority],
           NoOfChildren,
           MaxLevel
    FROM
    (
        SELECT RecID,
               Virtual,
               FlowID,
               ToObjectMK,
               CASE
                   WHEN FromObjectMK = ToObjectMK THEN
                       NULL
                   ELSE
                       FromObjectMK
               END AS FromObjectMK,
               ToObject,
               FromObject,
               FlowType,
               [RootObjectMK],
               [RootObject],
               PathStr,
               Circular,
               PathNum,
               FlowExecDep,
               [Level],
               (
                   SELECT MAX([Level])
                   FROM ParentChildCTE l
                   WHERE l.ToObjectMK = c.ToObjectMK
               ) AS MaxObjectLevel,
               [Priority],
               (
                   SELECT COUNT(*)
                   FROM ParentChildCTE s
                   WHERE s.FromObjectMK = c.FromObjectMK
               ) AS NoOfChildren,
               (
                   SELECT MAX([Level])FROM ParentChildCTE
               ) AS MaxLevel
        FROM ParentChildCTE c
    --WHERE Circular = 1
    ) AS main;

    IF @Expanded = 0
    BEGIN
        INSERT @Return
        (
            RecID,
            Virtual,
            FlowID,
            ToObjectMK,
            FromObjectMK,
            ToObject,
            FromObject,
            FlowType,
            [RootObjectMK],
            [RootObject],
            PathStr,
            Circular,
            PathNum,
            FlowExecDep,
            Step,
            [Sequence],
            Level,
            MaxObjectLevel,
            Priority,
            NoOfChildren,
            MaxLevel,
            MaxLevelValue
        )
        SELECT RecID,
               Virtual,
               FlowID,
               ToObjectMK,
               FromObjectMK,
               ToObject,
               FromObject,
               FlowType,
               [RootObjectMK],
               [RootObject],
               PathStr,
               Circular,
               PathNum,
               FlowExecDep,
               StepCalculated AS Step,
               1 [Sequence],
               LevelCalculated AS [Level],
               MaxObjectLevel,
               Priority,
               NoOfChildren,
               MaxLevel,
               MaxLevelValue
        FROM
        (
            SELECT src.*,
                   (ISNULL(Trg.[Level], src.[Level]) * 100) StepCalculated,
                   ISNULL(Trg.[Level], src.[Level]) AS LevelCalculated,
                   ROW_NUMBER() OVER (PARTITION BY src.ToObjectMK,
                                                   src.FromObjectMK
                                      ORDER BY ISNULL(Trg.[Level], src.[Level]) DESC
                                     ) AS LevelPri
            FROM @Holder src
                INNER JOIN
                (
                    SELECT ToObjectMK,
                           MAX([Level]) AS [Level]
                    FROM @Holder
                    GROUP BY ToObjectMK
                ) AS Trg
                    ON src.ToObjectMK = Trg.ToObjectMK
        ) a
        WHERE a.LevelPri = 1
              AND
              (
                  a.Virtual = 0
                  OR FlowType = 'SP'
              )
        ORDER BY MaxObjectLevel;

    END;
    ELSE IF @Expanded = 1
    BEGIN

        INSERT @Return
        (
            RecID,
            Virtual,
            FlowID,
            ToObjectMK,
            FromObjectMK,
            ToObject,
            FromObject,
            FlowType,
            [RootObjectMK],
            [RootObject],
            PathStr,
            Circular,
            PathNum,
            FlowExecDep,
            Step,
            [Sequence],
            Level,
            MaxObjectLevel,
            Priority,
            NoOfChildren,
            MaxLevel,
            MaxLevelValue
        )
        SELECT RecID,
               Virtual,
               src.FlowID,
               src.ToObjectMK,
               FromObjectMK,
               ToObject,
               FromObject,
               FlowType,
               [RootObjectMK],
               [RootObject],
               PathStr,
               Circular,
               PathNum,
               FlowExecDep,
               (ISNULL(Trg.[Level], src.[Level]) * 100) Step,
               1 [Sequence],
               ISNULL(Trg.[Level], src.[Level]),
               MaxObjectLevel,
               Priority,
               NoOfChildren,
               MaxLevel,
               MaxLevelValue
        FROM @Holder src
            INNER JOIN
            (
                SELECT ToObjectMK,
                       MAX([Level]) AS [Level]
                FROM @Holder
                GROUP BY ToObjectMK
            ) AS Trg
                ON src.ToObjectMK = Trg.ToObjectMK
                   AND src.[Level] = Trg.[Level]
        ORDER BY MaxObjectLevel;
    END;
    ELSE IF @Expanded = 2
    BEGIN

        INSERT @Return
        (
            RecID,
            Virtual,
            FlowID,
            ToObjectMK,
            FromObjectMK,
            ToObject,
            FromObject,
            FlowType,
            [RootObjectMK],
            [RootObject],
            PathStr,
            Circular,
            PathNum,
            FlowExecDep,
            Step,
            [Sequence],
            Level,
            MaxObjectLevel,
            Priority,
            NoOfChildren,
            MaxLevel,
            MaxLevelValue
        )
        SELECT RecID,
               Virtual,
               FlowID,
               src.ToObjectMK,
               FromObjectMK,
               ToObject,
               FromObject,
               FlowType,
               [RootObjectMK],
               [RootObject],
               PathStr,
               Circular,
               PathNum,
               FlowExecDep,
               (ISNULL(Trg.Level, src.Level) * 100) Step,
               1 [Sequence],
               ISNULL(Trg.Level, src.Level),
               MaxObjectLevel,
               Priority,
               NoOfChildren,
               MaxLevel,
               MaxLevelValue
        FROM @Holder src
            INNER JOIN
            (
                SELECT ToObjectMK,
                       MAX([Level]) AS [Level]
                FROM @Holder
                GROUP BY ToObjectMK
            ) AS Trg
                ON src.ToObjectMK = Trg.ToObjectMK
        ORDER BY MaxObjectLevel;
    END;
    RETURN;

END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedures purpose is to parse data lineage and resolve execution order
  -- Summary			:	The function uses a common table expression (CTE) named "ParentChildCTE" to recursively retrieve information about lineage relationships between objects in a database.

							The results from the CTE are stored in a table variable named "@Holder". 
							The final results are then inserted into the table variable "@Return" based on the value of "@Expanded" passed into the function.

							If "@Expanded" is 0, then the results are filtered to only include the highest level object for each unique "ToObjectMK" value. 
							If "@Expanded" is 1, then the results are filtered to only include the highest level object for each unique "ToObjectMK" value 
							and all objects with the same "Level" value as the highest level object. 
							
							If "@Expanded" is 2, then the results are not filtered and all objects retrieved by the CTE are included in the final results.', 'SCHEMA', N'flw', 'FUNCTION', N'LineageParse', NULL, NULL
GO
