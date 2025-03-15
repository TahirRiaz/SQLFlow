SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO


/*
  ##################################################################################################################################################
  -- Name				:   [flw].[AddDepObject]
  -- Date				:   2022.05.12
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure is responsible for adding or updating dependency objects in the [flw].[LineageObjectMK] table.
  -- Summary			:
							It accepts the following input parameters:

							@ObjectMK (default value: 0)
							@ObjectName
							@ObjectType
							@SysAlias (default value: '')
							@dmlSQL (default value: '')
							@BeforeDependency (default value: '')
							@AfterDependency (default value: '')
							It starts by setting the NOCOUNT option to ON to prevent the sending of row count messages to the client.

							If the @ObjectMK is not equal to 0, the procedure updates the record with the specified ObjectMK in the [flw].[LineageObjectMK] table. 
							It sets the ObjectType, BeforeDependency, AfterDependency, and ObjectDef fields with the provided values from input parameters.

							If the @ObjectMK is equal to 0, the procedure checks for the existence of a record with the provided ObjectName in the [flw].[LineageObjectMK] table.

							If the record does not exist, the procedure inserts a new record into the [flw].[LineageObjectMK] table with the provided values from input parameters.

							If the record does exist, the procedure updates the existing record with the provided values from input parameters, similar to step 3.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2020.11.08		Initial
  ##################################################################################################################################################
  */


CREATE PROCEDURE [flw].[AddDepObject]
    -- Add the parameters for the stored procedure here
    @ObjectMK INT = 0,
    @ObjectName NVARCHAR(250),
    @ObjectType NVARCHAR(70),
    @SysAlias NVARCHAR(70) = '',
    @dmlSQL NVARCHAR(MAX) = '',
    @BeforeDependency NVARCHAR(MAX) = '',
    @AfterDependency NVARCHAR(MAX) = '',
    @relationJson NVARCHAR(MAX) = '',
    @CurrentIndexes NVARCHAR(MAX) = ''
AS
BEGIN

    SET NOCOUNT ON;

    IF (@ObjectMK <> 0)
    BEGIN
        UPDATE TOP (1)
            Trg
        SET Trg.ObjectType = CASE
                                 WHEN LEN(ISNULL(@ObjectType, '')) > 0 THEN
                                     @ObjectType
                                 ELSE
                                     Trg.ObjectType
                             END,
            Trg.[BeforeDependency] = @BeforeDependency,
            Trg.[AfterDependency] = @AfterDependency,
            Trg.[ObjectDef] = @dmlSQL,
            Trg.RelationJson = @relationJson,
            Trg.CurrentIndexes = @CurrentIndexes
        --Trg.[NotInUse] = CASE
        --                      WHEN LEN(@dmlSQL) > 0 THEN 0
        --                      ELSE 1 END
        FROM [flw].[LineageObjectMK] Trg
        WHERE ObjectMK = @ObjectMK;
    END;
    ELSE
    BEGIN

        DECLARE @dupe INT = 0;
        SELECT @dupe = COUNT(*)
        FROM [flw].[LineageObjectMK]
        WHERE [ObjectName] = @ObjectName;
        IF (ISNULL(@dupe, 0) = 0)
        BEGIN
            INSERT INTO [flw].[LineageObjectMK]
            (
                [ObjectName],
                [ObjectType],
                [ObjectSource],
                SysAlias,
                [IsFlowObject],
                [IsDependencyObject],
                [BeforeDependency],
                [AfterDependency],
                [ObjectDef],
                RelationJson,
                [CurrentIndexes]
            )
            SELECT @ObjectName AS [ObjectName],
                   @ObjectType AS [ObjectType],
                   'Dependency' AS [ObjectSource],
                   @SysAlias AS SysAlias,
                   0 AS [IsFlowObject],
                   1 [IsDependencyObject],
                   @BeforeDependency,
                   @AfterDependency,
                   @dmlSQL,
                   @relationJson,
                   @CurrentIndexes;
        END;
        ELSE
        BEGIN
            UPDATE TOP (1)
                Trg
            SET Trg.ObjectType = CASE
                                     WHEN LEN(ISNULL(@ObjectType, '')) > 0 THEN
                                         @ObjectType
                                     ELSE
                                         Trg.ObjectType
                                 END,
                Trg.[BeforeDependency] = @BeforeDependency,
                Trg.[AfterDependency] = @AfterDependency,
                Trg.[ObjectDef] = @dmlSQL,
                Trg.RelationJson = @relationJson,
                Trg.CurrentIndexes = @CurrentIndexes
            --Trg.[NotInUse] = CASE
            --			 WHEN LEN(@dmlSQL) > 0 THEN 0
            --			 ELSE 1 END
            FROM [flw].[LineageObjectMK] Trg
            WHERE [ObjectName] = @ObjectName;
        END;
    END;

END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedure is responsible for adding or updating dependency objects in the [flw].[LineageObjectMK] table.
  -- Summary			:
							It accepts the following input parameters:

							@ObjectMK (default value: 0)
							@ObjectName
							@ObjectType
							@SysAlias (default value: '''')
							@dmlSQL (default value: '''')
							@BeforeDependency (default value: '''')
							@AfterDependency (default value: '''')
							It starts by setting the NOCOUNT option to ON to prevent the sending of row count messages to the client.

							If the @ObjectMK is not equal to 0, the procedure updates the record with the specified ObjectMK in the [flw].[LineageObjectMK] table. 
							It sets the ObjectType, BeforeDependency, AfterDependency, and ObjectDef fields with the provided values from input parameters.

							If the @ObjectMK is equal to 0, the procedure checks for the existence of a record with the provided ObjectName in the [flw].[LineageObjectMK] table.

							If the record does not exist, the procedure inserts a new record into the [flw].[LineageObjectMK] table with the provided values from input parameters.

							If the record does exist, the procedure updates the existing record with the provided values from input parameters, similar to step 3.', 'SCHEMA', N'flw', 'PROCEDURE', N'AddDepObject', NULL, NULL
GO
