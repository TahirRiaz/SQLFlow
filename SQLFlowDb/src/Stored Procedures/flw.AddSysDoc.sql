SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[AddSysDoc]
  -- Date				:   2024.01.17
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   
  -- Summary			:	
  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2024.01.17		Initial
  ##################################################################################################################################################
*/

CREATE PROCEDURE [flw].[AddSysDoc]
    -- Add the parameters for the stored procedure here

    @ObjectName NVARCHAR(255),
    @ObjectType NVARCHAR(255) = '',
    @ObjectDef NVARCHAR(MAX) = '',
    @RelationJson NVARCHAR(MAX) = '',
    @DependsOnJson NVARCHAR(MAX) = '',
    @DependsOnByJson NVARCHAR(MAX) = '',
    @Description NVARCHAR(MAX) = '',
    @Summary NVARCHAR(MAX) = '',
    @Question NVARCHAR(MAX) = '',
    @Label NVARCHAR(500) = '',
    @PromptDescription NVARCHAR(MAX) = '',
    @PromptQuestion NVARCHAR(MAX) = '',
    @PromptSummary NVARCHAR(MAX) = '',
    @ScriptGenId BIGINT = 0
AS
BEGIN

    SET NOCOUNT ON;


    IF NOT EXISTS
    (
        SELECT [ObjectName]
        FROM [flw].[SysDoc] WITH (NOLOCK)
        WHERE ObjectName = @ObjectName
    )
    BEGIN
        --Move current log version to Stats
        INSERT INTO [flw].[SysDoc]
        (
            [ObjectName],
            [ObjectType],
            [ObjectDef],
            [RelationJson],
            DependsOnJson,
            DependsOnByJson,
            [Description],
            [ScriptDate],
            ScriptGenID,
            [Label],
            PromptDescription,
            PromptQuestion
        )
        SELECT TOP (1)
               @ObjectName,
               @ObjectType,
               @ObjectDef,
               @RelationJson,
               @DependsOnJson,
               @DependsOnByJson,
               @Description,
               GETDATE(),
               @ScriptGenId,
               @Label,
               @PromptDescription,
               @PromptQuestion;
    END;
    ELSE
    BEGIN
        --Log Exec Start Time
        UPDATE TOP (1)
            trg
        SET [ObjectType] = CASE
                               WHEN LEN(@ObjectType) > 0 THEN
                                   @ObjectType
                               ELSE
                                   [ObjectType]
                           END,
            [ObjectDef] = CASE
                              WHEN LEN(@ObjectDef) > 0 THEN
                                  @ObjectDef
                              ELSE
                                  [ObjectDef]
                          END,
            [RelationJson] = CASE
                                 WHEN LEN(@RelationJson) > 0 THEN
                                     @RelationJson
                                 ELSE
                                     [RelationJson]
                             END,
            DependsOnJson = CASE
                                WHEN LEN(@DependsOnJson) > 0 THEN
                                    @DependsOnJson
                                ELSE
                                    DependsOnJson
                            END,
            DependsOnByJson = CASE
                                  WHEN LEN(@DependsOnByJson) > 0 THEN
                                      @DependsOnByJson
                                  ELSE
                                      DependsOnByJson
                              END,
            [Description] = CASE
                                WHEN LEN(@Description) > 0 THEN -- AND LEN(ISNULL([Description], '')) = 0
                                    @Description
                                ELSE
                                    [Description]
                            END,
            [Summary] = CASE
                            WHEN LEN(@Summary) > 0 THEN -- AND LEN(ISNULL([Description], '')) = 0
                                @Summary
                            ELSE
                                [Summary]
                        END,
            Question = CASE
                           WHEN LEN(@Question) > 0 --AND LEN(ISNULL([Question], '')) = 0 
                                THEN
                               @Question
                           ELSE
                               [Question]
                       END,
            [PromptDescription] = CASE
                                      WHEN LEN(@PromptDescription) > 0 THEN
                                          @PromptDescription
                                      ELSE
                                          [PromptDescription]
                                  END,
            [PromptQuestion] = CASE
                                   WHEN LEN(@PromptQuestion) > 0 THEN
                                       @PromptQuestion
                                   ELSE
                                       [PromptQuestion]
                               END,
            [PromptSummary] = CASE
                                  WHEN LEN(@PromptSummary) > 0 THEN
                                      @PromptSummary
                                  ELSE
                                      [PromptSummary]
                              END,
            [Label] = CASE
                          WHEN LEN(@Label) > 0 THEN
                              @Label
                          ELSE
                              [Label]
                      END,
            [ScriptDate] = GETDATE(),
            ScriptGenID = CASE
                              WHEN @ScriptGenId > 0 THEN
                                  @ScriptGenId
                              ELSE
                                  ScriptGenID
                          END
        FROM [flw].[SysDoc] trg WITH (NOLOCK)
        WHERE ObjectName = @ObjectName;
    END;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedures purpose is to update or add extended properties for columns and tables (or views) in the database, using the information stored in the flw.SysDoc table.
  -- Summary			:	The "AddSysDoc" stored procedure is designed to synchronize the extended properties for columns, tables, and views with the descriptions stored in the "flw.SysDoc" table. 
  
							The procedure iterates through two cursors, one for columns and another for tables and views, and uses dynamic SQL to either add or update the "MS_Description" extended property
							with the corresponding description from the "flw.SysDoc" table at the appropriate level (i.e., column, table, or view). 
							The purpose of this procedure is to make it easy to view and manage the descriptions stored in the "flw.SysDoc" 
							table within SQL Server Management Studio or other tools that support extended properties.', 'SCHEMA', N'flw', 'PROCEDURE', N'AddSysDoc', NULL, NULL
GO
