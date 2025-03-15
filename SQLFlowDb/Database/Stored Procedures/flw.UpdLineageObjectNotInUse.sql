SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
/*
  ##################################################################################################################################################
  -- Name				:   [flw].[UpdLineageObjectNotInUse]
  -- Date				:   2023-01-03
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This is a SQL stored procedure that updates the "NotInUse" column of the "LineageObjectMK" table based on two input parameters. 
  -- Summary			:	It sets "NotInUse" to 1 for rows where "ObjectMK" matches a value in the @InvalidMKList parameter, and to 0 where it matches 
							a value in the @ValidMKList parameter. The parameters are comma-separated strings and are split into tables using the "StringSplit" function.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2023-01-03		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[UpdLineageObjectNotInUse]
(
    -- Add the parameters for the stored procedure here
    @InvalidMKList NVARCHAR(MAX),
	@ValidMKList NVARCHAR(MAX)
)
AS
BEGIN
    -- SET NOCOUNT ON added to prevent extra result sets from
    -- interfering with SELECT statements.
    SET NOCOUNT ON;

    -- Insert statements for procedure here
    UPDATE trg
    SET trg.[NotInUse] = 1
    FROM [flw].[LineageObjectMK] trg
        INNER JOIN
        (SELECT  Item FROM [flw].[StringSplit](@InvalidMKList, ',') ) sub
            ON trg.ObjectMK = sub.Item;
			

    UPDATE trg
    SET trg.[NotInUse] = 0
    FROM [flw].[LineageObjectMK] trg
        INNER JOIN
        (SELECT  Item FROM [flw].[StringSplit](@ValidMKList, ',') ) sub
            ON trg.ObjectMK = sub.Item;
			
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This is a SQL stored procedure that updates the "NotInUse" column of the "LineageObjectMK" table based on two input parameters. 
  -- Summary			:	It sets "NotInUse" to 1 for rows where "ObjectMK" matches a value in the @InvalidMKList parameter, and to 0 where it matches 
							a value in the @ValidMKList parameter. The parameters are comma-separated strings and are split into tables using the "StringSplit" function.', 'SCHEMA', N'flw', 'PROCEDURE', N'UpdLineageObjectNotInUse', NULL, NULL
GO
