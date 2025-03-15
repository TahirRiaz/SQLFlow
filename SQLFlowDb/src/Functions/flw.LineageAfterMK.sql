SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
--select * from [meta].[GetExecPlanTree](0)

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[LineageAfter]
  -- Date				:   10/12/2018
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:    "LineageAfter" that takes in two parameters: @StartObject, which is a string value representing the starting object for the lineage search, 
							 and @Expanded, which is an integer value indicating whether to return a simple or expanded result set.
  -- Summary			:	The function returns a table variable (@Return) that contains information about the lineage of the specified starting object. 
							
							The function first determines the starting object by checking the @StartObject parameter against various columns in the flw.LineageMap table. 
							It then uses a common table expression (CTE) to recursively build the lineage of the starting object by joining the flw.LineageMap table. 
							The resulting data is inserted into a table variable (@Holder), which is then used to populate the @Return table variable.

							The @Expanded parameter determines how the data in the @Return table variable is formatted. If @Expanded is 0, the table variable is returned with a simple result set. 
							If @Expanded is 1, the table variable is returned with an expanded result set. If @Expanded is 2, 
							the table variable is returned with an expanded result set that includes all levels of the lineage. 
							The specific formatting differences for each option are described in the code.
  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		10/12/2018		Initial
  ##################################################################################################################################################
*/
CREATE FUNCTION [flw].[LineageAfterMK]
(
    @StartObject NVARCHAR(255)
)
RETURNS @Return TABLE
(
    RecID INT,
    Virtual BIT,
    FlowID INT,
    [FlowType] VARCHAR(4),
    FromObjectMK INT,
    [ToObjectMK] INT,
    FromObject VARCHAR(255),
    [ToObject] VARCHAR(255),
    [RootObjectMK] INT,
    [RootObject] VARCHAR(225),
    PathStr VARCHAR(8000),
    Circular BIT,
    PathNum VARCHAR(8000),
    Step INT,
    [Sequence] INT,
    [Level] INT,
    MaxObjectLevel INT,
    [Priority] INT,
    NoOfChildren INT,
    MaxLevel INT,
    MaxLevelValue INT,
    StepOrg INT,
    [StopOnError] INT,
    SQLcmd VARCHAR(MAX),
    SQLExec VARCHAR(MAX),
    Deactivate INT
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

    --  DECLARE @StartObject NVARCHAR(255);
    --SET @StartObject = 129;
    DECLARE @StartObjectMK INT;

    DECLARE @startObjects VARCHAR(4000) = '';

    IF LEN(@startObjects) <= 1
    BEGIN
        SELECT @startObjects = @startObjects + ',' + CAST([ToObjectMK] AS VARCHAR(255))
        FROM [flw].[LineageMap]
        WHERE ([FromObjectMK] = CASE
                                    WHEN ISNUMERIC(@StartObject) = 1 THEN
                                        @StartObject
                                    ELSE
                                        0
                                END
              )
        OR ([ToObjectMK] = CASE
                               WHEN ISNUMERIC(@StartObject) = 1 THEN
                                   @StartObject
                               ELSE
                                   0
                           END
        )
        ORDER BY [Step];

    END;

    IF LEN(@startObjects) > 1
    BEGIN
        SET @startObjects = SUBSTRING(@startObjects, 2, LEN(@startObjects));
    END;

    DECLARE @Holder TABLE
    (
        RecID INT,
        Virtual BIT,
        FlowID INT,
        [ToObjectMK] INT,
        FromObjectMK INT,
        [Object] VARCHAR(250),
        [FlowType] VARCHAR(4),
        [ObjectType] VARCHAR(50),
        FromObject VARCHAR(4000),
        ParentObjectType VARCHAR(100),
        [RootObjectMK] INT,
        [RootObject] VARCHAR(250),
        PathStr VARCHAR(8000),
        Circular BIT,
        PathNum VARCHAR(8000),
        [Level] INT,
        Step INT,
        [Sequence] INT,
        MaxObjectLevel INT,
        [Priority] INT,
        NoOfChildren INT,
        MaxLevel INT,
        MaxLevelValue INT,
        Commands VARCHAR(8000),
        StepOrg INT,
        [StopOnError] INT,
        SQLcmd VARCHAR(MAX),
        SQLExec VARCHAR(MAX),
        Deactivate INT
    );


    ;
    WITH ParentChildCTE (RecID, Virtual, FlowID, [ToObjectMK], FromObjectMK, [Object], [FromObject], [FlowType],
                         [RootObjectMK], [RootObject], PathStr, Circular, PathNum, [Level], [Priority]
                        )
    AS (
       --For Root Element : Merk at vi må snu på parent child verdiene.
       -- Convert(VARCHAR(4000), IsNull(Replace(Str(src.[FromObjectMK], 6), ' ', '0'), '') + '' + Replace(Str(src.ToObjectMK, 6), ' ', '0'))
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
              ToObject AS [Object],
              FromObject AS FromObject,
              [FlowType],
              CAST(src.ToObjectMK AS INT) AS [RootObjectMK],
              ToObject AS [RootObject],
              CAST(CONCAT(
                             ISNULL(src.ToObject, CAST(src.ToObjectMK AS VARCHAR(255))),
                             '',
                             ISNULL(src.FromObject, CAST(src.FromObjectMK AS VARCHAR(255)))
                         ) AS VARCHAR(MAX)) AS PathStr,
              0 AS Circular,
              CONVERT(VARCHAR(4000), ISNULL(CAST(src.FromObjectMK AS VARCHAR(255)), '')) AS PathNum,
              1 AS Level,
              0 AS [Priority]
       FROM flw.LineageMap src
       WHERE ToObjectMK IN
             (
                 SELECT DISTINCT Item FROM [flw].[StringSplit](@startObjects, ',')
             )
       UNION ALL
       --SQL For Children - Merk at vi må snu på parent child verdiene.
       SELECT child.RecID,
              CASE
                  WHEN LEN(ISNULL(child.[FlowType], '')) > 0 THEN
                      0
                  ELSE
                      1
              END AS Virtual, -- If [FlowType] for a child is empty then the row is virtual
              child.FlowID,
              child.ToObjectMK AS ToObjectMK,
              CASE
                  WHEN child.ToObjectMK = child.FromObjectMK THEN
                      NULL
                  ELSE
                      child.FromObjectMK
              END FromObjectMK,
              child.ToObject AS [Object],
              child.FromObject AS FromObject,
              child.[FlowType],
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
              parent.Level + 1,
              0 AS [Priority]
       FROM ParentChildCTE parent
           INNER JOIN flw.LineageMap child
               ON child.FromObjectMK = parent.[ToObjectMK]
                  AND parent.Circular = 0 -- Omit circular loops within the execution plan
    )
    INSERT INTO @Holder
    (
        RecID,
        Virtual,
        FlowID,
        ToObjectMK,
        FromObjectMK,
        [Object],
        FromObject,
        [FlowType],
        [RootObjectMK],
        [RootObject],
        PathStr,
        Circular,
        PathNum,
        [Level],
        MaxObjectLevel,
        [Priority],
        NoOfChildren,
        MaxLevel,
        StepOrg,
        StopOnError,
        SQLcmd,
        SQLExec,
        Deactivate
    )
    SELECT RecID,
           Virtual,
           FlowID,
           ToObjectMK,
           FromObjectMK,
           [Object],
           FromObject,
           [FlowType],
           [RootObjectMK],
           [RootObject],
           PathStr,
           Circular,
           PathNum,
           [Level],
           MaxObjectLevel,
           [Priority],
           NoOfChildren,
           MaxLevel,
           StepOrg,
           StopOnError,
           SQLcmd,
           SQLExec,
           
           Deactivate
    FROM
    (
        SELECT RecID,
               Virtual,
               c.FlowID,
               ToObjectMK,
               CASE
                   WHEN FromObjectMK = ToObjectMK THEN
                       NULL
                   ELSE
                       FromObjectMK
               END AS FromObjectMK,
               [Object],
               c.FromObject,
               c.[FlowType],
               [RootObjectMK],
               [RootObject],
               PathStr,
               Circular,
               PathNum,
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
               ) AS MaxLevel,
               0 AS StepOrg,     --F.Step AS StepOrg,
               0 AS StopOnError, --F.ContinueOnError AS StopOnError,
               '' AS SQLcmd,     --F.FromDatabase + '.' + E.ProcessSP + CAST(F.FlowID AS VARCHAR(255)) AS SQLcmd,
               '' AS SQLExec,    --'exec ' + F.FromDatabase + '.' + E.ProcessSP + CAST(F.FlowID AS VARCHAR(255)) AS SQLExec,
               
               0 AS Deactivate   --F.Deactivate
        FROM ParentChildCTE c


    --WHERE Circular = 1
    ) AS main;

    INSERT @Return
    (
        RecID,
        Virtual,
        FlowID,
        ToObjectMK,
        FromObjectMK,
        ToObject,
        FromObject,
        [FlowType],
        [RootObjectMK],
        [RootObject],
        PathStr,
        Circular,
        PathNum,
        Step,
        [Sequence],
        Level,
        MaxObjectLevel,
        Priority,
        NoOfChildren,
        MaxLevel,
        MaxLevelValue,
        StepOrg,
        StopOnError,
        SQLcmd,
        SQLExec,
        
        Deactivate
    )
    SELECT RecID,
           Virtual,
           FlowID,
           src.ToObjectMK,
           src.FromObjectMK,
           Object,
           FromObject,
           [FlowType],
           [RootObjectMK],
           [RootObject],
           PathStr,
           Circular,
           PathNum,
           ISNULL(a.Step, src.Step) Step,
           1 [Sequence],
           ISNULL(a.[Level], src.[Level]),
           MaxObjectLevel,
           Priority,
           NoOfChildren,
           MaxLevel,
           MaxLevelValue,
           StepOrg,
           StopOnError,
           SQLcmd,
           SQLExec,
           
           Deactivate
    FROM @Holder src
        LEFT OUTER JOIN
        (
            SELECT FromObjectMK,
                   ToObjectMK,
                   MAX(Step) AS Step,
                   MAX([Level]) AS [Level]
            FROM [flw].[LineageMap]
            GROUP BY FromObjectMK,
                     ToObjectMK
        ) a
            ON a.ToObjectMK = src.ToObjectMK
               AND a.FromObjectMK = src.FromObjectMK

    --LEFT OUTER JOIN sys.objects FObj
    --ON FObj.Object_id = OBJECT_ID(src.FromObject)
    --LEFT OUTER JOIN sys.objects TObj
    -- ON TObj.Object_id = OBJECT_ID(src.Object)

    ORDER BY ISNULL(a.[Level], src.[Level]);
    RETURN;

END;
GO
