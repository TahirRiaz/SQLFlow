SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
/*
  ##################################################################################################################################################
  -- Name				:   [flw].[UpdPreIngTransfromExp]
  -- Date				:   2022-11-07
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure updates the trasnformation expresion of a column. The fetch data types logic utilizes this.
  -- Summary			:	The procedure updates the "PreIngestionTransfrom" table in the "flw" schema based on the input parameters. 
							Special treatment for ADO objects.
  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-11-07		Initial
  ##################################################################################################################################################
*/

CREATE PROCEDURE [flw].[UpdPreIngTransfromExp]
    @FlowID [INT],
    @FlowType [NVARCHAR](25),
    @ColName [NVARCHAR](250),
    @Virtual [BIT] = 0,
    @SelectExp [NVARCHAR](MAX) = NULL,
    @ColAlias [NVARCHAR](250) = NULL,
    @SortOrder [INT] = NULL,
    @ExcludeColFromView [BIT] = 0
AS
BEGIN

    IF (@FlowType = 'ado')
    BEGIN

        UPDATE TOP (1) [flw].[PreIngestionTransfrom]
           SET         ColAlias = @ColAlias,
                       Virtual = @Virtual,
                       SelectExp = CASE WHEN LEN(ISNULL(@SelectExp,'')) > 2 THEN @SelectExp ELSE SelectExp END
         --SortOrder = @SortOrder,
         --ExcludeColFromView = @ExcludeColFromView
         WHERE         [FlowID]       = @FlowID
           AND         ColName                = @ColName
           AND         FlowType               = @FlowType
           ;
        ;WITH base
           AS (SELECT @FlowID FlowID,
                      @FlowType FlowType,
                      @Virtual Virtual,
                      @ColName ColName,
                      @SelectExp SelectExp,
                      @ColAlias ColAlias,
                      @SortOrder SortOrder,
                      @ExcludeColFromView ExcludeColFromView
                      )
        INSERT INTO [flw].[PreIngestionTransfrom] ([FlowID],
                                                   [FlowType],
                                                   [Virtual],
                                                   [ColName],
                                                   [SelectExp],
                                                   [ColAlias],
                                                   [SortOrder],
                                                   [ExcludeColFromView]
                                                   )
        SELECT            src.[FlowID],
                          src.[FlowType],
                          src.[Virtual],
                          src.[ColName],
                          src.[SelectExp],
                          src.[ColAlias],
                          src.[SortOrder],
                          src.[ExcludeColFromView]
          FROM            base src
          LEFT OUTER JOIN [flw].[PreIngestionTransfrom] trg
            ON src.[FlowID] = trg.[FlowID]
           AND src.FlowType = trg.FlowType
           AND src.ColName  = trg.ColName
         WHERE            trg.ColName IS NULL;

    END;
    ELSE
    BEGIN
        UPDATE TOP (1) [flw].[PreIngestionTransfrom]
           SET         Virtual = @Virtual,
                       SelectExp = CASE WHEN LEN(ISNULL(@SelectExp,'')) > 2 THEN @SelectExp ELSE SelectExp END,
                       ColAlias = @ColAlias
         --SortOrder = @SortOrder,
         --ExcludeColFromView = @ExcludeColFromView
         WHERE         [FlowID]           = @FlowID
           AND         ColName                    = @ColName
           AND         FlowType                   = @FlowType
           AND         LEN(ISNULL(SelectExp, '')) = 0;

    END;



    --DECLARE @ViewName       NVARCHAR(255)  = N'',
    --        @ViewNameFull   NVARCHAR(255)  = N'',
    --        @ViewCMD        NVARCHAR(MAX)  = N'',
    --        @ViewColumnList NVARCHAR(MAX)  = N'',
    --        @ViewSelect     NVARCHAR(MAX)  = N'',
    --        @preFilter      NVARCHAR(1024) = N'';

--SELECT @ViewCMD = ViewCMD,
--       @ViewSelect = ViewSelect,
--       @ViewName = ViewName,
--       @ViewNameFull = @ViewNameFull,
--       @preFilter = @preFilter
-- FROM [flw].[GetPreViewCmd](@FlowID);


END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedure updates the trasnformation expresion of a column. The fetch data types logic utilizes this.
  -- Summary			:	The procedure updates the "PreIngestionTransfrom" table in the "flw" schema based on the input parameters. 
							Special treatment for ADO objects.', 'SCHEMA', N'flw', 'PROCEDURE', N'UpdPreIngTransfromExp', NULL, NULL
GO
