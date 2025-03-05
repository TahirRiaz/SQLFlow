SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[AddPreIngTransfromExp]
  -- Date				:   2022-11-07
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure is responsible for updating the pre-ingestion transformation expressions in the [flw].[PreIngestionTransfrom] table.
  -- Summary			:	It accepts the following input parameters:
							@FlowID
							@FlowType
							@Virtual
							@ColName
							@SelectExp
							@ColAlias
							@SortOrder
							@ExcludeColFromView
							@SkipUpdatesOnSelectExp (default value: 0)
							
							It updates the first record in the [flw].[PreIngestionTransfrom] table where the FlowID and ColName match the input parameters. The fields to be updated include:
							FlowID
							FlowType
							Virtual
							ColName
							SelectExp (updated if @SelectExp is not NULL or empty)
							ColAlias
							SortOrder
							ExcludeColFromView

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-11-07		Initial
  ##################################################################################################################################################
*/

CREATE PROCEDURE [flw].[AddPreIngTransfromExp]
    @FlowID [INT],
    @FlowType [NVARCHAR](25),
    @Virtual [BIT],
    @ColName [NVARCHAR](250),
    @SelectExp [NVARCHAR](max),
    @ColAlias [NVARCHAR](250),
    @SortOrder [INT],
    @ExcludeColFromView [BIT],
	@SkipUpdatesOnSelectExp BIT = 0
AS
BEGIN
    UPDATE TOP(1) [flw].[PreIngestionTransfrom]
       SET FlowID = @FlowID,
           FlowType = @FlowType,
           Virtual = @Virtual,
           ColName = @ColName,
           SelectExp = CASE WHEN LEN(ISNULL(@SelectExp, '')) > 2 THEN @SelectExp ELSE SelectExp END,
           ColAlias = @ColAlias,
           SortOrder = @SortOrder,
           ExcludeColFromView = @ExcludeColFromView
     WHERE [FlowID]                   = @FlowID
       AND @ColName                   = @ColName
       AND LEN(ISNULL(SelectExp, '')) > 0;
END;


GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedure is responsible for updating the pre-ingestion transformation expressions in the [flw].[PreIngestionTransfrom] table.
  -- Summary			:	It accepts the following input parameters:
							@FlowID
							@FlowType
							@Virtual
							@ColName
							@SelectExp
							@ColAlias
							@SortOrder
							@ExcludeColFromView
							@SkipUpdatesOnSelectExp (default value: 0)
							
							It updates the first record in the [flw].[PreIngestionTransfrom] table where the FlowID and ColName match the input parameters. The fields to be updated include:
							FlowID
							FlowType
							Virtual
							ColName
							SelectExp (updated if @SelectExp is not NULL or empty)
							ColAlias
							SortOrder
							ExcludeColFromView', 'SCHEMA', N'flw', 'PROCEDURE', N'AddPreIngTransfromExp', NULL, NULL
GO
