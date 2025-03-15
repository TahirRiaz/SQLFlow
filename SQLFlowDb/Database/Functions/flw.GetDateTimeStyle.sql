SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO


/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetDateTimeStyle]
  -- Date				:   2022.07.05
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This user-defined function takes a date/time value in a string format as input and tries to determine the date/time style code that matches the format. 
							It does this by using a cursor to iterate through a table of date formats and attempting to convert the input value to a datetime, 
							date or time value using each format until a match is found. The function then returns the matching style code as an integer value. 
							If no matching style code is found, the function returns 0.
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022.07.05		Initial
  ##################################################################################################################################################
*/

CREATE FUNCTION [flw].[GetDateTimeStyle]
(
    @value NVARCHAR(1024)
)
RETURNS INT
BEGIN

    DECLARE @rValue INT;

    DECLARE @StyleCode INT,
            @Query NVARCHAR(255),
            @Type NVARCHAR(255),
            @Style INT = 0;

    DECLARE cRec CURSOR FOR
    SELECT StyleCode,
           Query,
           [Type]
    FROM [flw].[SysDateTimeStyle]
    --WHERE LEN([DateStyle]) >= LEN(@value)
    ORDER BY LEN([DateStyle]);


    OPEN cRec;
    FETCH NEXT FROM cRec
    INTO @StyleCode,
         @Query,
         @Type;


    WHILE @@FETCH_STATUS = 0
    BEGIN

        IF (
               @Type IN ( 'DateTime', 'Date' )
               AND CHARINDEX(':', @value) > 0
               AND LEN(@value) >= 8
           )
        BEGIN
            SELECT @rValue = CASE
                                 WHEN (TRY_CONVERT(DATETIME, @value, @StyleCode)) IS NOT NULL THEN
                                     @StyleCode
                                 ELSE
                                     0
                             END;
        END;

        IF (@Type = 'Date' AND LEN(@value) >= 8 AND @rValue = 0)
        BEGIN
            SELECT @rValue = CASE
                                 WHEN (TRY_CONVERT(DATE, @value, @StyleCode)) IS NOT NULL THEN
                                     @StyleCode
                                 ELSE
                                     0
                             END;
        END;

        IF (
               @Type = 'Time'
               AND LEN(@value) >= 8
               AND CHARINDEX(':', @value) > 0
               AND @rValue = 0
           )
        BEGIN
            SELECT @rValue = CASE
                                 WHEN (TRY_CONVERT(DATE, @value)) IS NOT NULL THEN
                                     @StyleCode
                                 ELSE
                                     0
                             END;
        END;

        IF @rValue <> 0
        BEGIN
            BREAK;
        END;

        FETCH NEXT FROM cRec
        INTO @StyleCode,
             @Query,
             @Type;
    END;



    CLOSE cRec;
    DEALLOCATE cRec;


    RETURN @rValue;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This user-defined function takes a date/time value in a string format as input and tries to determine the date/time style code that matches the format. 
							It does this by using a cursor to iterate through a table of date formats and attempting to convert the input value to a datetime, 
							date or time value using each format until a match is found. The function then returns the matching style code as an integer value. 
							If no matching style code is found, the function returns 0.', 'SCHEMA', N'flw', 'FUNCTION', N'GetDateTimeStyle', NULL, NULL
GO
