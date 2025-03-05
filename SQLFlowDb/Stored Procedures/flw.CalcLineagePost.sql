SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO


/*
  ##################################################################################################################################################
  -- Name				:   [flw].[CalcLineagePost]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored proceduresThis stored procedure is executed after SQLFlow engine has evaluated all relevant objects and extracted dependency information from them. 
							The results are populated to [flw].[LineageObjectMK]. 
							This table generates graph edges from the parsed information and populates [flw].[LineageEdge]. 
							Final results of the Linage calculation is populated to [flw].[LineageMap] and [flw].[SysFlowDep] are also 
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[CalcLineagePost]
AS
BEGIN

    SET NOCOUNT ON;

    --*** Populate Edge Table ***--
    TRUNCATE TABLE flw.LineageEdge;
    INSERT INTO flw.LineageEdge
    (
        DataSet,
        FlowID,
        FlowType,
        FromObjectMK,
        ToObjectMK,
        FromObject,
        ToObject,
        Dependency,
        IsAfterDependency
    )
    SELECT DataSet,
           FlowID,
           FlowType,
           FromObjectMK,
           ToObjectMK,
           FromObject,
           ToObject,
           Dependency,
           IsAfterDependency
    FROM flw.LineageEdgeBase
	 --  WHERE FlowID = 1401

    --#[flw].[Ingestion] 
    --Regular FromObject and ToObject


    --Fetch All Parents that dont appear as children. Cross match that to check if we have some dep on these objects. 
    --IF yes then add them as edges to the parent flowID
    ;
    WITH RootNodes
    AS (SELECT DataSet,
               FlowID,
               FlowType,
               FromObjectMK,
               ToObjectMK,
               FromObject,
               ToObject,
               Dependency,
               IsAfterDependency
        FROM flw.LineageEdge
        WHERE FromObjectMK NOT IN
              (
                  SELECT ToObjectMK AS ToObjectMK
                  FROM flw.LineageEdge
                  WHERE ToObjectMK IS NOT NULL
              )
    --AND FromObjectMK = 103
    --AND flowid = 1211
    )
    INSERT INTO flw.LineageEdge
    (
        DataSet,
        FlowID,
        FlowType,
        FromObjectMK,
        ToObjectMK,
        FromObject,
        ToObject,
        Dependency,
        IsAfterDependency
    )
    SELECT 100 DataSet,
           b.FlowID,
           b.FlowType,
           [flw].[GetObjectMK](a2.Item, '') AS FromObjectMK,
           b.FromObjectMK AS ToObjectMK,
           a2.Item AS FromObject,
           FromObject AS ToObject,
           NULL AS Dependency,
           0 IsAfterDependency
    FROM RootNodes b
        INNER JOIN [flw].[LineageObjectMK] mk
            ON mk.ObjectMK = b.FromObjectMK
        CROSS APPLY
    (SELECT Item FROM [flw].[StringSplit](mk.[BeforeDependency], ',') ) a2;

    --Special case when before dependcy of an SP is used in another flow. 
    --Adding an extra dataset to ensure that the sp is connected to the ToObject of the new flow.
    --This enforces correct level calculation for the sp.
    ;
    WITH base
    AS (SELECT *
        FROM [flw].[LineageEdge]
        WHERE FlowType = 'sp'),
         FromAsParent
    AS (SELECT 200 AS [DataSet],
               b.[FlowID],
               b.[FlowType],
               b.[ToObjectMK] AS FromObjectMK,
               dupe.ToObjectMK,
               b.[ToObject] AS FromObject,
               dupe.[ToObject]
        FROM base b
            INNER JOIN [flw].[LineageEdge] dupe
                ON b.FromObjectMK = dupe.FromObjectMK
                   AND dupe.FlowType NOT IN ( 'sp' )),
         DupeDS
    AS (SELECT src.*
        FROM FromAsParent src
            LEFT OUTER JOIN [flw].[LineageEdge] b
                ON src.FromObjectMK = b.FromObjectMK
                   AND src.ToObjectMK = b.ToObjectMK
        WHERE b.FromObjectMK IS NULL)
    INSERT INTO [flw].[LineageEdge]
    (
        [DataSet],
        [FlowID],
        [FlowType],
        [FromObjectMK],
        [ToObjectMK],
        [FromObject],
        [ToObject]
    )
    SELECT MAX([DataSet]) AS [DataSet],
           MAX([FlowID]) AS [FlowID],
           MAX([FlowType]) AS [FlowType],
           [FromObjectMK],
           [ToObjectMK],
           [FromObject],
           [ToObject]
    FROM DupeDS
    GROUP BY [FromObjectMK],
             [ToObjectMK],
             [FromObject],
             [ToObject];




    --Check for virtual nodes that can cause circular refrence. 
    --Find Circular Refrence
    IF (OBJECT_ID('tempdb..#CircularRefrence') IS NOT NULL)
        DROP TABLE #CircularRefrence;
        ;WITH FindRoot
        AS (SELECT [RecID],
                   FlowID,
                   FlowType,
                   FromObjectMK,
                   ToObjectMK,
                   CAST(FromObjectMK AS NVARCHAR(MAX)) [Path],
                   0 Distance,
                   0 Circular
            FROM flw.LineageEdge
            UNION ALL
            SELECT C.[RecID],
                   C.FlowID,
                   C.FlowType,
                   C.FromObjectMK,
                   P.ToObjectMK,
                   C.Path + N' > ' + CAST(P.FromObjectMK AS NVARCHAR(MAX)),
                   C.Distance + 1,
                   CASE
                       WHEN PATINDEX('%' + CAST(C.ToObjectMK AS VARCHAR(255)) + '%', C.[Path]) = 0 THEN
                           0
                       ELSE
                           1
                   END AS Circular
            FROM flw.LineageEdge P
                JOIN FindRoot C
                    ON C.ToObjectMK = P.FromObjectMK
                       AND P.ToObjectMK <> P.FromObjectMK
                       AND C.ToObjectMK <> C.FromObjectMK
                       AND C.Circular = 0 -- Omit circular loops within the execution plan
        ),
              CircularRefrence
        AS (SELECT *
            FROM FindRoot R
            WHERE R.FromObjectMK = R.ToObjectMK
                  -- AND R.ParentId <> 0
                  AND R.Distance > 0)
    SELECT *
    INTO #CircularRefrence
    FROM CircularRefrence;

    --Tag circular virtual nodes.
    UPDATE trg
    SET trg.[Circular] = 1
    FROM flw.LineageEdge trg
    WHERE trg.RecID IN
          (
              SELECT RecID FROM #CircularRefrence
          ); -- Find Virtual Nodes that cause circular refrence)
             --WHERE len(FlowType) = 0

    -- Delete Virtual Nodes that cause circular refrence
    --DELETE FROM flw.LineageEdge
    --WHERE RecID IN
    --      (
    --          SELECT RecID FROM #CircularRefrence
    --      ); --Virtual nodes dont have a FlowType
    --WHERE len(FlowType) = 0


    --Update SysLog with ObjectDefs
    UPDATE trg
    SET trg.FromObjectDef = CASE
                                WHEN fObj.ObjectDef IS NULL THEN
                                    trg.FromObjectDef
                                ELSE
                                    fObj.ObjectDef
                            END,
        trg.ToObjectDef = CASE
                              WHEN tObj.ObjectDef IS NULL THEN
                                  trg.ToObjectDef
                              ELSE
                                  tObj.ObjectDef
                          END
    FROM flw.FlowDS
        INNER JOIN flw.LineageObjectMK AS fObj
            ON flw.FlowDS.FromObjectMK = fObj.ObjectMK
        INNER JOIN flw.LineageObjectMK AS tObj
            ON flw.FlowDS.ToObjectMK = tObj.ObjectMK
        INNER JOIN flw.SysLog AS trg
            ON flw.FlowDS.FlowID = trg.FlowID;



    DELETE FROM [flw].[LineageObjectRelation]
    WHERE ISNULL([ManualEntry], 0) = 0;

    ;WITH UnpackJson
    AS (SELECT DISTINCT
               ParsedObjectName,
               RelationshipCounter,
               j.CompareOp,
               j.[Database] + '.' + j.[Schema] + '.' + j.[Table] ObjectName,
               j.[Column]
        FROM [flw].[LineageObjectMK] t
            CROSS APPLY
            OPENJSON(t.[RelationJson])
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
        WHERE LEN(t.[RelationJson]) > 0),
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
          FinalRows
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
    INSERT INTO [flw].[LineageObjectRelation]
    (
        [LeftObject],
        [LeftObjectCol],
        [RightObject],
        [RightObjectCol],
        [ManualEntry]
    )
    SELECT [LeftObject],
           [LeftObjectCol],
           [RightObject],
           [RightObjectCol],
           [ManualEntry]
    FROM FinalRows
    WHERE LEN([RightObjectCol]) > 0;


END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored proceduresThis stored procedure is executed after SQLFlow engine has evaluated all relevant objects and extracted dependency information from them. 
							The results are populated to [flw].[LineageObjectMK]. 
							This table generates graph edges from the parsed information and populates [flw].[LineageEdge]. 
							Final results of the Linage calculation is populated to [flw].[LineageParsed] and [flw].[SysFlowDep] are also ', 'SCHEMA', N'flw', 'PROCEDURE', N'CalcLineagePost', NULL, NULL
GO
