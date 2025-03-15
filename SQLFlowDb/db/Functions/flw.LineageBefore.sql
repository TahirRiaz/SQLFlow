SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[LineageBefore]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   "LineageBefore" takes in two parameters: the name of the starting object (represented as a string) and an integer flag that determines the level of detail of the output.
  -- Summary			:	The purpose of this function is to return a table of lineage information for objects that come before the specified starting object. 
							The specific information returned depends on the level of detail chosen by the user with the @Expanded parameter.
  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/

CREATE FUNCTION [flw].[LineageBefore]
(
    @StartObject NVARCHAR(255),
    @Expanded INT
)
RETURNS @Return TABLE
(
    RecID INT IDENTITY(1, 1),
    Virtual BIT,
    FlowID INT,
    FromObjectMK INT,
    [ToObjectMK] INT,
    FromObject VARCHAR(1000),
    [ToObject] VARCHAR(250),
    FlowType VARCHAR(4),
    Step INT,
    [Sequence] INT,
    [Level] INT,
    [StopOnError] INT,
    SQLcmd VARCHAR(MAX),
    SQLExec VARCHAR(MAX),
    [StepOrg] INT,
    Deactivate INT
)
AS
BEGIN

    --  DECLARE @StartObject NVARCHAR(255);
    --SET @StartObject = 129;
    DECLARE @StartObjectMK INT;

    DECLARE @Holder TABLE
    (
        RecID INT,
        Virtual BIT,
        FlowID INT,
        FromObjectMK INT,
        [ToObjectMK] INT,
        FromObject VARCHAR(1000),
        [ToObject] VARCHAR(250),
        FlowType VARCHAR(4),
        Step INT,
        [Sequence] INT,
        [Level] INT,
        [StopOnError] INT,
        SQLcmd VARCHAR(MAX),
        SQLExec VARCHAR(MAX),
        [StepOrg] INT,
        Deactivate INT
    );


    SET @StartObjectMK
        = COALESCE(
          (
              SELECT TOP 1
                     [ToObjectMK]
              FROM flw.LineageMap
              WHERE Virtual = 0
                    AND ([FlowID] = CASE
                                        WHEN ISNUMERIC(@StartObject) = 1 THEN
                                            @StartObject
                                        ELSE
                                            0
                                    END
                        )
              ORDER BY [Step]
          ),
          (
              SELECT TOP 1
                     [ToObjectMK]
              FROM flw.LineageMap
              WHERE Virtual = 0
                    AND ([FromObjectMK] = CASE
                                              WHEN ISNUMERIC(@StartObject) = 1 THEN
                                                  @StartObject
                                              ELSE
                                                  0
                                          END
                        )
              ORDER BY [Step]
          ),
          (
              SELECT TOP 1
                     [ToObjectMK]
              FROM flw.LineageMap
              WHERE Virtual = 0
                    AND ([flw].[StrRemRegex]([ToObject], '%[^a-zA-Z0-9ÆæÅåØø/_&#.-]%') = CASE
                                                                                             WHEN ISNUMERIC(@StartObject) = 0 THEN
                                                                                                 [flw].[StrRemRegex](
                                                                                                                        @StartObject,
                                                                                                                        '%[^a-zA-Z0-9ÆæÅåØø/_&#.-]%'
                                                                                                                    )
                                                                                             ELSE
                                                                                                 ''
                                                                                         END
                        )
              ORDER BY [Step]
          ),
          --If not found use ToObjectMK based on the from object
          (
              SELECT TOP 1
                     [ToObjectMK]
              FROM flw.LineageMap
              WHERE Virtual = 0
                    AND ([flw].[StrRemRegex]([FromObject], '%[^a-zA-Z0-9ÆæÅåØø/_&#.-]%') = CASE
                                                                                               WHEN ISNUMERIC(@StartObject) = 0 THEN
                                                                                                   [flw].[StrRemRegex](
                                                                                                                          @StartObject,
                                                                                                                          '%[^a-zA-Z0-9ÆæÅåØø/_&#.-]%'
                                                                                                                      )
                                                                                               ELSE
                                                                                                   ''
                                                                                           END
                        )
              ORDER BY [Step]
          )
                  );




    ;
    WITH EdgeParsedWithTranspose
    AS (
       --Remove FromObjectMK Null Objects and duplicate path objects with distinct
       SELECT DISTINCT
              RecID,
              Virtual,
              FlowID,
              ToObjectMK AS ID,
              FromObjectMK AS PID,
              ToObject AS [Object],
              FromObject AS ObjectParent,
              FlowType
       FROM flw.LineageMap
       WHERE FromObjectMK IS NOT NULL),
         FindAllParents
    AS (SELECT RecID,
               FlowID,
               ID,
               PID,
               [Object],
               ObjectParent,
               Virtual,
               FlowType,
               1 AS LEVEL,
               0 AS Circular,
               CONVERT(VARCHAR(4000), ISNULL(CAST(ID AS VARCHAR(255)), '')) AS PathNum
        FROM EdgeParsedWithTranspose
        WHERE ID = @StartObjectMK -- Sart Object
              AND ([FlowID] = CASE
                                  WHEN ISNUMERIC(@StartObject) = 1 THEN
                                      @StartObject
                                  ELSE
                                      0
                              END
                  )
        UNION ALL
        SELECT C.RecID,
               C.FlowID,
               C.ID,
               C.PID,
               C.[Object],
               C.ObjectParent,
               C.Virtual,
               C.FlowType,
               p.LEVEL + 1 AS Level,
               CASE
                   WHEN PATINDEX('%' + CAST(C.ID AS VARCHAR(255)) + '%', p.PathNum) = 0 THEN
                       0
                   ELSE
                       1
               END AS Circular,
               CONVERT(VARCHAR(4000), p.PathNum + '-' + CAST(C.ID AS VARCHAR(255))) AS PathNum
        FROM EdgeParsedWithTranspose C
            JOIN FindAllParents p
                ON C.ID = p.PID -- the recursion
                   AND p.Circular = 0 -- Omit circular loops 
         )
    INSERT @Holder
    (
        RecID,
        Virtual,
        FlowID,
        FromObjectMK,
        [ToObjectMK],
        FromObject,
        [ToObject],
        FlowType,
        Step,
        [Sequence],
        [Level],
        [StopOnError],
        SQLcmd,
        SQLExec,
        [StepOrg],
        Deactivate
    )
    SELECT *
    FROM
    (
        SELECT DISTINCT
               A.RecID,
               A.Virtual,
               A.FlowID,
               A.PID,
               A.ID,
               A.ObjectParent,
               A.[Object],
               A.FlowType,
               ((MAX(A.[LEVEL]) OVER (ORDER BY A.[LEVEL] DESC)) - ((A.[LEVEL]) - 1)) * 100 Step,
               1 AS [Sequence],
               A.[LEVEL],
               0 AS StopOnError, --F.ContinueOnError AS StopOnError,
               '' AS SQLcmd,     --F.FromDatabase + '.' + E.ProcessSP + CAST(F.FlowID AS VARCHAR(255)) AS SQLcmd,
               '' AS SQLExec,    --'exec ' + F.FromDatabase + '.' + E.ProcessSP + CAST(F.FlowID AS VARCHAR(255)) AS SQLExec,
               0 AS StepOrg,     --f.Step as [StepOrg],
               0 AS Deactivate   --f.Deactivate
        FROM FindAllParents A
    ) s
    ORDER BY Step DESC
    --OPTION (MAXRECURSION 100)
    ;

    IF @Expanded = 0
    BEGIN
        INSERT @Return
        (
            Virtual,
            FlowID,
            FromObjectMK,
            [ToObjectMK],
            FromObject,
            [ToObject],
            FlowType,
            Step,
            [Sequence],
            [Level],
            [StopOnError],
            SQLcmd,
            SQLExec,
            [StepOrg],
            Deactivate
        )
        SELECT Virtual,
               FlowID,
               FromObjectMK,
               [ToObjectMK],
               FromObject,
               [ToObject],
               FlowType,
               StepCalculated AS Step, -- IIF ([StepOrg] = 100, 100,Step) as Step,
               [Sequence],
               LevelCalculated AS [Level],
               [StopOnError],
               SQLcmd,
               SQLExec,
               [StepOrg],
               Deactivate
        FROM
        (
            SELECT src.*,
                   ROW_NUMBER() OVER (PARTITION BY src.ToObjectMK,
                                                   src.FromObjectMK
                                      ORDER BY ISNULL(a.[Level], src.[Level]) DESC
                                     ) AS LevelPri,
                   (ISNULL(a.[Level], src.[Level]) * 100) StepCalculated,
                   ISNULL(a.[Level], src.[Level]) AS LevelCalculated


            FROM @Holder src
                LEFT OUTER JOIN
                (
                    SELECT FromObjectMK,
                           ToObjectMK,
                           MAX(Step) AS Step,
                           MAX([Level]) AS [Level]
                    FROM [flw].[LineageMap] trg
                    GROUP BY FromObjectMK,
                             ToObjectMK
                ) a
                    ON a.ToObjectMK = src.ToObjectMK
                       AND a.FromObjectMK = src.FromObjectMK
        ) a
        WHERE a.LevelPri = 1
              AND
              (
                  a.Virtual = 0
                  OR FlowType = 'SP'
              )
        ORDER BY [Level];

    END;
    ELSE IF @Expanded = 1
    BEGIN

        INSERT @Return
        (
            Virtual,
            FlowID,
            FromObjectMK,
            [ToObjectMK],
            FromObject,
            [ToObject],
            FlowType,
            Step,
            [Sequence],
            [Level],
            [StopOnError],
            SQLcmd,
            SQLExec,
            [StepOrg],
            Deactivate
        )
        SELECT Virtual,
               src.FlowID,
               src.FromObjectMK,
               src.[ToObjectMK],
               FromObject,
               [ToObject],
               FlowType,
               ISNULL(Trg.Step, src.Step), -- IIF ([StepOrg] = 100, 100,Step) as Step,
               [Sequence],
               ISNULL(Trg.[Level], src.[Level]),
               [StopOnError],
               SQLcmd,
               SQLExec,
               [StepOrg],
               Deactivate
        FROM @Holder src
            INNER JOIN
            (
                SELECT FromObjectMK,
                       ToObjectMK,
                       MAX(Step) AS Step,
                       MAX([Level]) AS [Level]
                FROM [flw].[LineageMap]
                GROUP BY FromObjectMK,
                         ToObjectMK
            ) AS Trg
                ON src.ToObjectMK = Trg.ToObjectMK
                   AND src.[Level] = Trg.[Level]
        ORDER BY ISNULL(Trg.[Level], src.[Level]);
    END;
    ELSE IF @Expanded = 2
    BEGIN

        INSERT @Return
        (
            Virtual,
            FlowID,
            FromObjectMK,
            [ToObjectMK],
            FromObject,
            [ToObject],
            FlowType,
            Step,
            [Sequence],
            [Level],
            [StopOnError],
            SQLcmd,
            SQLExec,
            [StepOrg],
            Deactivate
        )
        SELECT Virtual,
               src.FlowID,
               src.FromObjectMK,
               src.[ToObjectMK],
               FromObject,
               [ToObject],
               FlowType,
               ISNULL(a.Step, src.Step), -- IIF ([StepOrg] = 100, 100,Step) as Step,
               [Sequence],
               ISNULL(a.[Level], src.[Level]),
               [StopOnError],
               SQLcmd,
               SQLExec,
               [StepOrg],
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
        ORDER BY ISNULL(a.[Level], src.[Level]);
    END;
    RETURN;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   "LineageBefore" takes in two parameters: the name of the starting object (represented as a string) and an integer flag that determines the level of detail of the output.
  -- Summary			:	The purpose of this function is to return a table of lineage information for objects that come before the specified starting object. 
							The specific information returned depends on the level of detail chosen by the user with the @Expanded parameter.', 'SCHEMA', N'flw', 'FUNCTION', N'LineageBefore', NULL, NULL
GO
