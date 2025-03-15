SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO


/*
  ##################################################################################################################################################
  -- Name				:   [flw].[WasNextStepSuccessfulOnLastRun]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The purpose of the provided stored procedure, "WasNextStepSuccessfulOnLastRun", is to determine whether the next step in 
							a given flow was successful on the last run. It takes a single input parameter @FlowID which is the ID of the flow to be evaluated.
  -- Summary			:	The stored procedure begins by initializing some variables and creating a temporary table to store the lineage of the flow being evaluated. 
							It then retrieves the minimum step number from the lineage table for flows other than the flow being evaluated.

							Next, it retrieves the end time of the flow being evaluated from the system log table. It then calculates the sum of the success 
							values and the count of the success values from the system log table for the step obtained from the lineage table for all flows other than 
							the flow being evaluated whose end time is greater than the end time of the flow being evaluated.

							Finally, it compares the sum of the success values and the count of the success values and sets the value of @NextStepStatusSum accordingly. 
							If the sum of the success values is greater than or equal to the count of the success values, then @NextStepStatusSum is set to 1, otherwise, 
							it is set to 0. If there is no lineage for the flow being evaluated, the stored procedure returns 1.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/

CREATE PROCEDURE [flw].[WasNextStepSuccessfulOnLastRun]
(
    -- Add the parameters for the stored procedure here
    @FlowID int
)
AS
BEGIN
    -- SET NOCOUNT ON added to prevent extra result sets from
    -- interfering with SELECT statements.
    SET NOCOUNT ON

    -- Insert statements for procedure here
    --DECLARE @FlowID int = 664
	DECLARE @Step int 
	DECLARE @NextStepStatusSum int  = 1
	DECLARE @NextStepCount int  = 0


	--IF OBJECT_ID('tempdb..#LineageAfter') IS NOT NULL DROP TABLE #LineageAfter
	--SELECT * into #LineageAfter
	--From  [flw].[LineageAfter] (@FlowID,0)

	--Select @Step= min(Step)
	--From  #LineageAfter
	--WHERE FlowID <> @FlowID

	--DECLARe @dateTime datetime
	--SELECT @dateTime = Endtime
	--From [flw].[SysLog]
	--Where FlowId = @FlowID

	--Select  @NextStepStatusSum = sum(CAST(IsNull(Success,0) as int)) ,
	--		@NextStepCount = count(IsNull(Success,0))
	--From [flw].[SysLog] l
	--inner join #LineageAfter ln
	--on l.flowid = ln.flowid
	--and l.flowid <> @FlowID
	--and Step = @Step
	--and l.Endtime > @dateTime

	
	Select  @NextStepStatusSum = sum(CAST(IsNull(Success,0) as int)) ,
			@NextStepCount = count(IsNull(Success,0))
	From [flw].[SysLog] l
	 WHERE FlowID IN
          (
              SELECT item
			  FROM [flw].[LineageMap]
			  CROSS APPLY (
				SELECT Item FROM [flw].[StringSplit] ([NextStepFlows],',')
			  ) a
			  WHERE LEN([NextStepFlows]) > 0
			  AND FlowID IN (@FlowID)
          )

	--Compare number of related steps and total sucess sum
	SET @NextStepStatusSum = CASE WHEN @NextStepStatusSum >= @NextStepCount THEN 1 ELSE 0 END

	-- NULL is an option for flows with no lineage
	select Isnull(@NextStepStatusSum,1)  as NextStepStatus
END
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The purpose of the provided stored procedure, "WasNextStepSuccessfulOnLastRun", is to determine whether the next step in 
							a given flow was successful on the last run. It takes a single input parameter @FlowID which is the ID of the flow to be evaluated.
  -- Summary			:	The stored procedure begins by initializing some variables and creating a temporary table to store the lineage of the flow being evaluated. 
							It then retrieves the minimum step number from the lineage table for flows other than the flow being evaluated.

							Next, it retrieves the end time of the flow being evaluated from the system log table. It then calculates the sum of the success 
							values and the count of the success values from the system log table for the step obtained from the lineage table for all flows other than 
							the flow being evaluated whose end time is greater than the end time of the flow being evaluated.

							Finally, it compares the sum of the success values and the count of the success values and sets the value of @NextStepStatusSum accordingly. 
							If the sum of the success values is greater than or equal to the count of the success values, then @NextStepStatusSum is set to 1, otherwise, 
							it is set to 0. If there is no lineage for the flow being evaluated, the stored procedure returns 1.', 'SCHEMA', N'flw', 'PROCEDURE', N'WasNextStepSuccessfulOnLastRun', NULL, NULL
GO
