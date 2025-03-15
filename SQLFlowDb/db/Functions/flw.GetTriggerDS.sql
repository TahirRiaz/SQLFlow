SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO



--

CREATE FUNCTION [flw].[GetTriggerDS]
(
    @FlowID INT
)
RETURNS @ObjNames TABLE
(
    [Name] NVARCHAR(255) NULL,
    [Link] NVARCHAR(255) NULL,
    [Trigger] NVARCHAR(255) NULL
)
AS
BEGIN

    --FETCH Target DATABASE Name
    --FETCH Target DATABASE Name

    DECLARE @Batch NVARCHAR(255),
            @Flowtype VARCHAR(25),
            @Alias VARCHAR(25);

    SELECT @Batch = Batch,
           @Flowtype = FlowType,
           @Alias = Alias
    FROM flw.FlowDS
    WHERE FlowID = @FlowID;

    DECLARE @SQLFlowCoreCmd NVARCHAR(255) = N'';
    SELECT @SQLFlowCoreCmd = ParamValue
    FROM flw.SysCFG
    WHERE (ParamName = N'SQLFlowCoreCmd');

    INSERT INTO @ObjNames
    (
        [Name],
        [Link],
        [Trigger]
    )
    SELECT 'Flow AF' AS [Name],
           '<A  href="javascript:triggerExecDialogFromClient(''' + 'Execute Flow' + ''',''' + [flw].[GetWebApiUrl]()
           + 'ExecFlowProcess?flowId=' + CAST(@FlowID AS VARCHAR(255)) + '&execmode=af&dbg=1' + ''''
           + ');">Execute</A>',
           [flw].[GetWebApiUrl]() + 'ExecFlowProcess?flowId=' + CAST(@FlowID AS VARCHAR(255)) + '&execmode=af&dbg=1' [Trigger]
    UNION ALL
    SELECT 'Node AF' AS [Name],
           '<A  href="javascript:triggerExecDialogFromClient(''' + 'Execute Node' + ''',''' + [flw].[GetWebApiUrl]()
           + 'ExecFlowNode?node=' + CAST(@FlowID AS VARCHAR(255)) + '&dir=A&exitOnError=1&dbg=1&execmode=af' + ''''
           + ');">Execute</A>',
           [flw].[GetWebApiUrl]() + 'ExecFlowNode?node=' + CAST(@FlowID AS VARCHAR(255))
           + '&dir=A&exitOnError=1&execmode=af&dbg=1'
    UNION ALL
    SELECT 'Batch AF' AS [Name],
           '<A  href="javascript:triggerExecDialogFromClient(''' + 'Execute Batch' + ''',''' + [flw].[GetWebApiUrl]()
           + 'ExecFlowBatch?batch=' + CAST(@Batch AS VARCHAR(255)) + '&flowType=' + '&execmode=af&dbg=1' + ''''
           + ');">Execute</A>',
           [flw].[GetWebApiUrl]() + 'ExecFlowBatch?batch=' + CAST(@Batch AS VARCHAR(255)) + '&flowType='
           + '&execmode=af&dbg=1'
    UNION ALL
    SELECT 'Edit Flow' AS [Name],
           '<A target="_blank" href="' + [flw].[GetFlowTypeUiUrl](@Flowtype) + +CAST(@FlowID AS VARCHAR(255))
           + '">Edit</A>',
           [flw].[GetWebApiUrl]() + 'FetchFlowInfo?search=' + CAST(@FlowID AS VARCHAR(255))
    UNION ALL
    SELECT 'Lineage' AS [Name],
           '<A target="_blank" href="/calculate-lineage/1/' + @Alias + '">Calculate</A>',
           '/calculate-lineage/1/' + @Alias
    UNION ALL
    SELECT 'Assertion' AS [Name],
           '<A  href="javascript:triggerExecDialogFromClient(''' + 'Execute Assertion' + ''','''
           + [flw].[GetWebApiUrl]() + 'ExecAssertion?FlowId=' + CAST(@FlowID AS VARCHAR(255)) + '&dbg=1' + ''''
           + ');">Execute</A>',
           [flw].[GetWebApiUrl]() + 'ExecAssertion?FlowId=' + CAST(@FlowID AS VARCHAR(255)) + +'&dbg=1'
    UNION ALL
    SELECT 'HealthCheck' AS [Name],
           '<A  href="javascript:triggerExecDialogFromClient(''' + 'Execute HealthCheck' + ''','''
           + [flw].[GetWebApiUrl]() + 'ExecHealthCheck?FlowId=' + CAST(@FlowID AS VARCHAR(255))
           + '&RunModelSelection=0&dbg=1' + '''' + ');">Execute</A>',
           [flw].[GetWebApiUrl]() + 'ExecHealthCheck?FlowId=' + CAST(@FlowID AS VARCHAR(255))
           + +'&RunModelSelection=0&dbg=1'
    UNION ALL
    SELECT 'Batch Status' AS [Name],
           '<A target="_blank" href="/report-batch/' + @Batch + '">Batch Status</A>',
           '/report-batch/' + @Batch
    UNION ALL
    SELECT 'Detect Unique Key' AS [Name],
           '<A target="_blank" href="/detect-unique-key/' + CAST(@FlowID AS VARCHAR(255)) + '">Detect Unique key</A>',
           '/detect-unique-key/' + CAST(@FlowID AS VARCHAR(255))
    UNION ALL
    SELECT 'Clone Flow' AS [Name],
           '<A href="javascript:OpenCloneAsyncJS(''' + CAST(@FlowID AS VARCHAR(255)) + ''');">Clone Flow</A>',
           ''
    UNION ALL
    SELECT 'Reset Flow' AS [Name],
           '<A href="javascript:OpenResetAsyncJS(''' + CAST(@FlowID AS VARCHAR(255)) + ''');">Reset Flow</A>',
           ''
    UNION ALL
    SELECT 'Initialize New Flow From' AS [Name],
           CASE
               WHEN @Flowtype NOT IN ( 'sp', 'inv', 'aut', 'cs', 'ps', 'adf' ) THEN
                   '<A href="javascript:triggerInitPipelineAsyncJs(''' + CAST(@FlowID AS VARCHAR(255))
                   + ''');">Initialize New Flow From</A>'
               ELSE
                   ''
           END,
           ''
    UNION ALL
    SELECT 'Flow Cmd' AS [Name],
           NULL AS LINK,
           @SQLFlowCoreCmd + N' --exec flow --flowid ' + CAST(@FlowID AS VARCHAR(255)) + N' --dbg 1 --mode MAN '
    UNION ALL
    SELECT 'Node Cmd' AS [Name],
           NULL AS LINK,
           @SQLFlowCoreCmd + N' --exec node --node ' + CAST(@FlowID AS VARCHAR(255)) + N' --dir A --dbg 1 --mode MAN '
    UNION ALL
    SELECT 'Batch Cmd' AS [Name],
           NULL AS LINK,
           +@SQLFlowCoreCmd + N' --exec batch --batch ' + CAST(@Batch AS VARCHAR(255))
           + N'--flowtype --dbg 1 --mode MAN '
    UNION ALL
    SELECT 'Lineage Cmd' AS [Name],
           NULL AS LINK,
           @SQLFlowCoreCmd + N' --exec lineage --all 1 --noofthreads 1';

    RETURN;
END;
GO
