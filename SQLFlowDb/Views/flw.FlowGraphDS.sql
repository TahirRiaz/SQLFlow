SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO



CREATE VIEW [flw].[FlowGraphDS]
AS
SELECT      TOP (100) PERCENT f.FlowID,
                              f.FlowType,
                              lp.Step,
                              lp.Sequence,
                              f.[Circular],
                              f.FromObject,
                              f.ToObject,
                              'exec [' + DB_NAME() + '].[flw].[ExecFlow] @FlowID=' + CAST(f.FlowID AS VARCHAR(255))
                              + ',@FlowType=''' + f.FlowType + ''',@dbg=1,@ExecMode=''MAN'' ' AS SQLExec,
                              f.Dependency,
                              'exec [' + DB_NAME() + '].[flw].[FlowGraph] @StartObject=' + CHAR(39)
                              + CAST(ISNULL(f.ToObject, f.FromObject) AS VARCHAR(255)) + CHAR(39)
                              + ', @Expanded=2, @Dir=''B'', @MonoColor=0, @ShowFlowID=0 , @ShowObjectType=1' AS GraphBefore,
                              'exec [' + DB_NAME() + '].[flw].[FlowGraph] @StartObject=' + CHAR(39)
                              + CAST(ISNULL(f.ToObject, f.FromObject) AS VARCHAR(255)) + CHAR(39)
                              + ', @Expanded=2, @Dir=''A'', @MonoColor=0, @ShowFlowID=0, @ShowObjectType=1' AS GraphAfter,
                              'select * from  [' + DB_NAME() + '].[flw].[LineageBefore] (' + CHAR(39)
                              + CAST(ISNULL(f.ToObject, f.FromObject) AS VARCHAR(255)) + CHAR(39) + ',0)' AS FlowBefore,
                              'select * from  [' + DB_NAME() + '].[flw].[LineageAfter] (' + CHAR(39)
                              + CAST(ISNULL(f.ToObject, f.FromObject) AS VARCHAR(255)) + CHAR(39) + ',0)' AS FlowAfter
  FROM      flw.LineageEdge f
 INNER JOIN [flw].[LineageMap] lp
    ON f.FlowID = lp.FlowID
   --AND f.Step   = lp.Step
 WHERE      lp.Virtual = 0
 --WHERE        (lp.FlowID = 427)
 --WHERE (ISNULL(f.Deactivate, 0) = 0)
 ORDER BY lp.Step,
          lp.Sequence;
GO
