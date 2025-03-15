SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
/*
  ##################################################################################################################################################
  -- Name				:   [flw].[FetchDataTypes]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This is a template stored procedures which assess data in a table and recommends optimal data types for each column. 
							This can be vital to structure data warehouse entities in the most optimal value. 
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[FetchDataTypes]
AS
BEGIN
    SET NOCOUNT ON; 

    DECLARE @FlowID INT = 547;
    DECLARE @FlowType VARCHAR(50) = 'csv';
    DECLARE @Schema VARCHAR(255) = 'arc';
    DECLARE @Table VARCHAR(255) = 'Baatbooking_Trans';
    DECLARE @DateMinLength INT = 8;
    DECLARE @ColName VARCHAR(255) = '@ColName'; --'CASE WHEN ''null'' =  @ColName THEN NULL ELSE @ColName END'

    DECLARE @ColumnName VARCHAR(255) = '';
    DECLARE @cmdSQL VARCHAR(MAX) = '';
    DECLARE @Virtual BIT = 0;

    BEGIN
        IF OBJECT_ID(N'tempdb..#SysDateFormats') IS NOT NULL
        BEGIN
            DROP TABLE #SysDateFormats;
        END;

        CREATE TABLE #SysDateFormats ([StyleCode] [INT] NULL,
                                      [Query] [NVARCHAR](250) NULL,
                                      [DateStyle] [NVARCHAR](50) NULL,
                                      [DateSample] [NVARCHAR](50) NULL,
                                      [Type] [NVARCHAR](50) NULL) ON [PRIMARY];

        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (1, N'select convert(varchar, getdate(), 1)', N'mm/dd/yy', N'12/30/06', N'Date');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (2, N'select convert(varchar, getdate(), 2)', N'yy.mm.dd', N'06.12.30', N'Date');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (3, N'select convert(varchar, getdate(), 3)', N'dd/mm/yy', N'30/12/06', N'Date');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (4, N'select convert(varchar, getdate(), 4)', N'dd.mm.yy', N'30.12.06', N'Date');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (5, N'select convert(varchar, getdate(), 5)', N'dd-mm-yy', N'30-12-06', N'Date');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (6, N'select convert(varchar, getdate(), 6)', N'dd-Mon-yy', N'30 Dec 06', N'Date');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (7, N'select convert(varchar, getdate(), 7)', N'Mon dd, yy', N'Dec 30, 06', N'Date');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (10, N'select convert(varchar, getdate(), 10)', N'mm-dd-yy', N'12-30-06', N'Date');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (11, N'select convert(varchar, getdate(), 11)', N'yy/mm/dd', N'06/12/30', N'Date');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (12, N'select convert(varchar, getdate(), 12)', N'yymmdd', N'061230', N'Date');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (23, N'select convert(varchar, getdate(), 23)', N'yyyy-mm-dd', N'2006-12-30', N'Date');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (101, N'select convert(varchar, getdate(), 101)', N'mm/dd/yyyy', N'12/30/2006', N'Date');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (102, N'select convert(varchar, getdate(), 102)', N'yyyy.mm.dd', N'2006.12.30', N'Date');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (103, N'select convert(varchar, getdate(), 103)', N'dd/mm/yyyy', N'30/12/2006', N'Date');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (104, N'select convert(varchar, getdate(), 104)', N'dd.mm.yyyy', N'30.12.2006', N'Date');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (105, N'select convert(varchar, getdate(), 105)', N'dd-mm-yyyy', N'30-12-2006', N'Date');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (106, N'select convert(varchar, getdate(), 106)', N'dd Mon yyyy', N'30 Dec 2006', N'Date');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (107, N'select convert(varchar, getdate(), 107)', N'Mon dd, yyyy', N'Dec 30, 2006', N'Date');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (110, N'select convert(varchar, getdate(), 110)', N'mm-dd-yyyy', N'12-30-2006', N'Date');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (111, N'select convert(varchar, getdate(), 111)', N'yyyy/mm/dd', N'2006/12/30', N'Date');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (112, N'select convert(varchar, getdate(), 112)', N'yyyymmdd', N'20061230', N'Date');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (8, N'select convert(varchar, getdate(), 8)', N'hh:mm:ss', N'00:38:54', N'Time');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (14, N'select convert(varchar, getdate(), 14)', N'hh:mm:ss:nnn', N'00:38:54:840', N'Time');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (24, N'select convert(varchar, getdate(), 24)', N'hh:mm:ss', N'00:38:54', N'Time');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (108, N'select convert(varchar, getdate(), 108)', N'hh:mm:ss', N'00:38:54', N'Time');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (114, N'select convert(varchar, getdate(), 114)', N'hh:mm:ss:nnn', N'00:38:54:840', N'Time');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (0, N'select convert(varchar, getdate(), 0)', N'Mon dd yyyy hh:mm AM/PM', N'Dec 30 2006 12:38AM',
                N'DateTime');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (9, N'select convert(varchar, getdate(), 9)', N'Mon dd yyyy hh:mm:ss:nnn AM/PM',
                N'Dec 30 2006 12:38:54:840AM', N'DateTime');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (13, N'select convert(varchar, getdate(), 13)', N'dd Mon yyyy hh:mm:ss:nnn AM/PM',
                N'30 Dec 2006 00:38:54:840AM', N'DateTime');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (20, N'select convert(varchar, getdate(), 20)', N'yyyy-mm-dd hh:mm:ss', N'2006-12-30 00:38:54',
                N'DateTime');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (21, N'select convert(varchar, getdate(), 21)', N'yyyy-mm-dd hh:mm:ss:nnn', N'2006-12-30 00:38:54.840',
                N'DateTime');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (22, N'select convert(varchar, getdate(), 22)', N'mm/dd/yy hh:mm:ss AM/PM', N'12/30/06 12:38:54 AM',
                N'DateTime');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (25, N'select convert(varchar, getdate(), 25)', N'yyyy-mm-dd hh:mm:ss:nnn', N'2006-12-30 00:38:54.840',
                N'DateTime');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (100, N'select convert(varchar, getdate(), 100)', N'Mon dd yyyy hh:mm AM/PM', N'Dec 30 2006 12:38AM',
                N'DateTime');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (109, N'select convert(varchar, getdate(), 109)', N'Mon dd yyyy hh:mm:ss:nnn AM/PM',
                N'Dec 30 2006 12:38:54:840AM', N'DateTime');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (113, N'select convert(varchar, getdate(), 113)', N'dd Mon yyyy hh:mm:ss:nnn',
                N'30 Dec 2006 00:38:54:840', N'DateTime');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (120, N'select convert(varchar, getdate(), 120)', N'yyyy-mm-dd hh:mm:ss', N'2006-12-30 00:38:54',
                N'DateTime');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (121, N'select convert(varchar, getdate(), 121)', N'yyyy-mm-dd hh:mm:ss:nnn',
                N'2006-12-30 00:38:54.840', N'DateTime');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (126, N'select convert(varchar, getdate(), 126)', N'yyyy-mm-dd T hh:mm:ss:nnn',
                N'2006-12-30T00:38:54.840', N'DateTime');
        INSERT #SysDateFormats ([StyleCode],
                                [Query],
                                [DateStyle],
                                [DateSample],
                                [Type])
        VALUES (127, N'select convert(varchar, getdate(), 127)', N'yyyy-mm-dd T hh:mm:ss:nnn',
                N'2006-12-30T00:38:54.840', N'DateTime');
    --INSERT #SysDateFormats ([StyleCode],
    --                        [Query],
    --                        [DateStyle],
    --                        [DateSample],
    --                        [Type])
    --VALUES (130, N'select convert(nvarchar, getdate(), 130)', N'dd mmm yyyy hh:mi:ss:nnn AM/PM', N'date output',
    --        N'Islamic');
    --INSERT #SysDateFormats ([StyleCode],
    --                        [Query],
    --                        [DateStyle],
    --                        [DateSample],
    --                        [Type])
    --VALUES (131, N'select convert(nvarchar, getdate(), 131)', N'dd mmm yyyy hh:mi:ss:nnn AM/PM',
    --        N'10/12/1427 12:38:54:840AM', N'Islamic');
    END;

    IF OBJECT_ID(N'tempdb..#Describe') IS NOT NULL
    BEGIN
        DROP TABLE #Describe;
    END;
    SELECT TABLE_SCHEMA,
           TABLE_NAME,
           COLUMN_NAME,
           DATA_TYPE,
           CAST('' AS NVARCHAR(MAX)) AS MinValue,
           CAST('' AS NVARCHAR(MAX)) AS MaxValue,
           0 AS MinLength,
           0 AS MaxLength,
           CAST('' AS NVARCHAR(MAX)) AS SelectExp,
           CAST(0 AS BIT) AS [IsDate],
           CAST(0 AS BIT) AS [IsDateTime],
           CAST(0 AS BIT) AS [IsTime],
           CAST(0 AS INT) AS DateLocal,
           CAST(NULL AS DATETIME) AS ValAsDate,
           CAST(0 AS BIT) AS [IsNumeric],
           CAST(0 AS INT) AS DecimalPoints,
           CAST(0 AS BIT) AS ContainsComma,
           'WITH base AS (SELECT MAX(LEN([' + COLUMN_NAME + '])) AS [MaxLength], MIN(LEN([' + COLUMN_NAME
           + '])) AS [MinLength], MAX([' + COLUMN_NAME + ']) AS MaxValue, MIN([' + COLUMN_NAME + ']) AS [MinValue], '''
           + COLUMN_NAME + ''' as COLUMN_NAME FROM [' + TABLE_SCHEMA + '].[' + TABLE_NAME
           + '] ) UPDATE Trg SET trg.MinLength = ISNull(src.MinLength,0),trg.MaxLength = Isnull(src.MaxLength,0),trg.MinValue = src.MinValue,trg.MaxValue = src.MaxValue FROM #Describe trg INNER JOIN base src ON trg.COLUMN_NAME = src.COLUMN_NAME' cmdSQL,
           Ordinal_Position
      INTO #Describe
      FROM INFORMATION_SCHEMA.COLUMNS
     WHERE TABLE_SCHEMA = @Schema
       AND TABLE_NAME   = @Table
	   --and Column_Name = 'TRANS_DATE'
    DECLARE cRec CURSOR FOR SELECT COLUMN_NAME, cmdSQL FROM #Describe;

    OPEN cRec;
    FETCH NEXT FROM cRec
     INTO @ColumnName,
          @cmdSQL;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        --PRINT @ColumnName;
        EXEC (@cmdSQL);
        FETCH NEXT FROM cRec
         INTO @ColumnName,
              @cmdSQL;
    END;

    CLOSE cRec;
    DEALLOCATE cRec;


    --Lets loop the values and figure optimal datatypes
    DECLARE @value NVARCHAR(MAX);
    DECLARE valRec CURSOR FOR
    SELECT COLUMN_NAME,
           CASE
                WHEN LEN(NULLIF('null', MaxValue)) > 0 THEN MaxValue
                ELSE MinValue END --, MaxValue,MinValue, LEN(MaxValue)
      FROM #Describe;
    OPEN valRec;

    FETCH NEXT FROM valRec
     INTO @ColumnName,
          @value;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        
		
		--Check if value is date
        DECLARE @cDate      DATETIME=null,
                @IsDateTime BIT = 0,
                @IsDate     BIT = 0,
                @IsTime     BIT = 0,
                @DateLocal  INT = 0;

        IF (@cDate IS NULL)
        BEGIN

            DECLARE @StyleCode INT,
                    @Query     NVARCHAR(255),
                    @Type      NVARCHAR(255);

            DECLARE cRec CURSOR FOR
            SELECT StyleCode,
                   Query,
                   [Type]
              FROM #SysDateFormats
               --WHERE LEN([DateStyle]) >= LEN(@value)
              -- AND LEN([DateStyle]) >= @DateMinLength
             ORDER BY LEN([DateStyle]);


            OPEN cRec;
            FETCH NEXT FROM cRec
             INTO @StyleCode,
                  @Query,
                  @Type;

            WHILE @@FETCH_STATUS = 0
            BEGIN
                IF (@Type in ('DateTime','Date') AND charindex(':', @value) > 0 and len(@value) >= 10 and @DateLocal = 0)
                BEGIN
					SELECT @DateLocal = CASE
                                             WHEN (TRY_CONVERT(DATETIME, @value,@StyleCode)) IS NOT NULL
                                              AND ISNUMERIC(@value) = 0
                                              AND LEN(@value) > 5 THEN @StyleCode
                                             ELSE 0 END,
                           @cDate = CASE
                                         WHEN (TRY_CONVERT(DATETIME, @value,@StyleCode)) IS NOT NULL
                                          AND ISNUMERIC(@value) = 0
                                          AND LEN(@value) > 5 THEN TRY_CONVERT(DATETIME, @value)
                                         ELSE '1900-01-01' END,
                           @IsDateTime = CASE
                                              WHEN (TRY_CONVERT(DATETIME, @value,@StyleCode)) IS NOT NULL
                                               AND ISNUMERIC(@value) = 0
                                               AND LEN(@value) > 5 THEN 1
                                              ELSE 0 END;
                END;

                IF (@Type = 'Date' and len(@value) >= 6 and @DateLocal = 0)
                BEGIN
                    SELECT @DateLocal = CASE
                                             WHEN (TRY_CONVERT(DATE, @value,@StyleCode)) IS NOT NULL
                                              AND ISNUMERIC(@value) = 0
                                              AND LEN(@value) > 5 THEN @StyleCode
                                             ELSE 0 END,
                           @cDate = CASE
                                         WHEN (TRY_CONVERT(DATE, @value,@StyleCode)) IS NOT NULL
                                          AND ISNUMERIC(@value) = 0
                                          AND LEN(@value) > 5 THEN TRY_CONVERT(DATE, @value)
                                         ELSE '1900-01-01' END,
                           @IsDate = CASE
                                          WHEN (TRY_CONVERT(DATE, @value,@StyleCode)) IS NOT NULL
                                           AND ISNUMERIC(@value) = 0
                                           AND LEN(@value) > 5 THEN 1
                                          ELSE 0 END;

                END;

                IF (@Type = 'Time' and len(@value) > 8 and CharIndex(':',@value) > 0 and @DateLocal = 0)
                BEGIN
                    SELECT @DateLocal = CASE
                                             WHEN (TRY_CONVERT(TIME, @value)) IS NOT NULL THEN @StyleCode
                                             ELSE 0 END,
                           @cDate = CASE
                                         WHEN (TRY_CONVERT(TIME, @value,@StyleCode)) IS NOT NULL THEN TRY_CONVERT(TIME, @value)
                                         ELSE '12:00:00' END,
                           @IsTime = CASE
                                          WHEN (TRY_CONVERT(TIME, @value,@StyleCode)) IS NOT NULL THEN 1
                                          ELSE 0 END;
                END;

                IF @DateLocal <> 0
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
        END;

        --PRINT @value
        --PRINT @cDate
        --PRINT @DateLocal
        --PRINT '*********************'
        --@DateMinLength
        --@DateMinLength
        IF (@IsDate = 1 OR @IsDateTime = 1 OR @IsTime = 1)
        BEGIN
            UPDATE trg
               SET trg.[IsDate] = @IsDate,
                   trg.[IsDateTime] = @IsDateTime,
                   trg.[IsTime] = @IsTime,
                   trg.DateLocal = @DateLocal,
                   trg.ValAsDate = @cDate
              FROM #Describe trg
             WHERE trg.COLUMN_NAME = @ColumnName;
        END;

        SET @cDate = NULL;
        SET @IsDate = 0;
        SET @IsDateTime = 0;
        SET @DateLocal = 0;
        SET @IsTime = 0;

        --Lets figure if the value is numeric
        DECLARE @ContainsComma BIT = 0,
                @IsNumeric     BIT = 0,
                @DecimalPoints INT = 0;
        DECLARE @numValue NVARCHAR(MAX);

        IF (CHARINDEX(',', @value) > 0)
        BEGIN
            SET @ContainsComma = 1;
            SET @numValue = REPLACE(@value, ',', '.');
        END;
        ELSE
        BEGIN
            SET @numValue = @value;
        END;

        IF (ISNUMERIC(@numValue) = 1)
        BEGIN
            SET @IsNumeric = 1;
        END;
        ELSE
        BEGIN
            SET @IsNumeric = 0;
            SET @ContainsComma = 0;
        END;

        IF (@IsNumeric = 1)
        BEGIN
            IF (PATINDEX('%[.][0-9]%', @numValue) > 0)
            BEGIN
                SET @DecimalPoints = 1;
            END;
            IF (PATINDEX('%[.][0-9][0-9]%', @numValue) > 0)
            BEGIN
                SET @DecimalPoints = 2;
            END;
            IF (PATINDEX('%[.][0-9][0-9][0-9]%', @numValue) > 0)
            BEGIN
                SET @DecimalPoints = 3;
            END;
            IF (PATINDEX('%[.][0-9][0-9][0-9][0-9]%', @numValue) > 0)
            BEGIN
                SET @DecimalPoints = 4;
            END;
            IF (PATINDEX('%[.][0-9][0-9][0-9][0-9][0-9]%', @numValue) > 0)
            BEGIN
                SET @DecimalPoints = 5;
            END;
            IF (PATINDEX('%[.][0-9][0-9][0-9][0-9][0-9][0-9]%', @numValue) > 0)
            BEGIN
                SET @DecimalPoints = 6;
            END;
            IF (PATINDEX('%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%', @numValue) > 0)
            BEGIN
                SET @DecimalPoints = 7;
            END;
            IF (PATINDEX('%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%', @numValue) > 0)
            BEGIN
                SET @DecimalPoints = 8;
            END;
            IF (PATINDEX('%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%', @numValue) > 0)
            BEGIN
                SET @DecimalPoints = 9;
            END;
            IF (PATINDEX('%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%', @numValue) > 0)
            BEGIN
                SET @DecimalPoints = 10;
            END;
            IF (PATINDEX('%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%', @numValue) > 0)
            BEGIN
                SET @DecimalPoints = 11;
            END;
            IF (PATINDEX('%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%', @numValue) > 0)
            BEGIN
                SET @DecimalPoints = 12;
            END;
            IF (PATINDEX('%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%', @numValue) > 0)
            BEGIN
                SET @DecimalPoints = 13;
            END;
            IF (PATINDEX('%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%', @numValue) > 0)
            BEGIN
                SET @DecimalPoints = 14;
            END;
            IF (PATINDEX('%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%', @numValue) > 0)
            BEGIN
                SET @DecimalPoints = 15;
            END;
            IF (PATINDEX(
                    '%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%', @numValue) > 0)
            BEGIN
                SET @DecimalPoints = 16;
            END;
            IF (PATINDEX(
                    '%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%',
                    @numValue) > 0)
            BEGIN
                SET @DecimalPoints = 17;
            END;
            IF (PATINDEX(
                    '%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%',
                    @numValue) > 0)
            BEGIN
                SET @DecimalPoints = 18;
            END;
            IF (PATINDEX(
                    '%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%',
                    @numValue) > 0)
            BEGIN
                SET @DecimalPoints = 19;
            END;
            IF (PATINDEX(
                    '%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%',
                    @numValue) > 0)
            BEGIN
                SET @DecimalPoints = 20;
            END;


        END;


        --PRINT @numValue;
        --PRINT @IsNumeric;
        --PRINT @ContainsComma;
        IF (@IsNumeric = 1)
        BEGIN
            UPDATE trg
               SET trg.[IsNumeric] = @IsNumeric,
                   trg.[ContainsComma] = @ContainsComma,
                   trg.DecimalPoints = @DecimalPoints
              FROM #Describe trg
             WHERE trg.COLUMN_NAME = @ColumnName;
        END;

        FETCH NEXT FROM valRec
         INTO @ColumnName,
              @value;
    END;
    CLOSE valRec;
    DEALLOCATE valRec;
	

    ;
    IF OBJECT_ID(N'tempdb..#PreIngestionTransfrom') IS NOT NULL
    BEGIN
        DROP TABLE #PreIngestionTransfrom;
    END;
    SELECT @FlowID AS [FlowID],
           @FlowType AS [FlowType],
           @Virtual AS [Virtual],
           QUOTENAME(COLUMN_NAME) AS [ColName],
           CASE
                WHEN QUOTENAME(COLUMN_NAME) = '[FileDate_DW]' THEN 'CAST(@ColName as decimal(14,0))'
				WHEN QUOTENAME(COLUMN_NAME) = '[DataSet_DW]' THEN 'CAST(@ColName as decimal(14,0))'
                WHEN QUOTENAME(COLUMN_NAME) = '[FileSize_DW]' THEN 'CAST(@ColName as decimal(18,0))'
                WHEN QUOTENAME(COLUMN_NAME) = '[FileName_DW]' THEN 'CAST(@ColName as varchar (255))'
				WHEN QUOTENAME(COLUMN_NAME) = '[FileLineNumber_DW]' THEN 'CAST(@ColName as int)'
                WHEN [IsDate] = 0
                 AND [IsDateTime] = 0
                 AND [IsTime] = 0
                 AND [IsNumeric] = 0 THEN
                    CASE
                         WHEN [MaxLength] BETWEEN 0 AND 50 THEN 'CAST(' + @ColName + ' as varchar (50))'
                         WHEN [MaxLength] BETWEEN 51 AND 255 THEN 'CAST(' + @ColName + ' as varchar (255))'
                         WHEN [MaxLength] BETWEEN 256 AND 1024 THEN 'CAST(' + @ColName + ' as varchar (1024))'
                         ELSE 'CAST(' + @ColName + ' as varchar (max))' END
                WHEN [IsDate] = 1 THEN 'CONVERT(date,' + @ColName + ',' + CAST(DateLocal AS VARCHAR(255)) + ')'
                WHEN [IsDateTime] = 1 THEN 'CONVERT(datetime,' + @ColName + ',' + CAST(DateLocal AS VARCHAR(255)) + ')'
                WHEN [IsTime] = 1 THEN 'CONVERT(time,' + @ColName + ',' + CAST(DateLocal AS VARCHAR(255)) + ')'
                WHEN [IsNumeric] = 1 THEN
                    CASE
                         WHEN DecimalPoints > 0
                          AND ContainsComma = 0 THEN
                             CASE
                                  WHEN LEN([MaxValue]) = 0
                                    OR LEN([MinValue]) = 0 THEN
                                      'CASE WHEN LEN(' + @ColName + ' ) > 0 THEN CAST(' + @ColName + ' AS DECIMAL('
                                      + CAST(MaxLength + 1 AS VARCHAR(255)) + ',' + CAST(DecimalPoints AS VARCHAR(255))
                                      + '))  ELSE NULL END'
                                  ELSE
                                      'CAST(' + @ColName + ' AS DECIMAL(' + CAST(MaxLength + 1 AS VARCHAR(255)) + ','
                                      + CAST(DecimalPoints AS VARCHAR(255)) + '))' END
                         WHEN DecimalPoints > 0
                          AND ContainsComma = 1 THEN
                             CASE
                                  WHEN LEN([MaxValue]) = 0
                                    OR LEN([MinValue]) = 0 THEN
                                      'CASE WHEN LEN(' + @ColName + ' ) > 0 THEN CAST(REPLACE(' + @ColName
                                      + ','','',''.'') AS DECIMAL(' + CAST(MaxLength + 1 AS VARCHAR(255)) + ','
                                      + CAST(DecimalPoints AS VARCHAR(255)) + ')) ELSE NULL END'
                                  ELSE
                                      'CAST(REPLACE(' + @ColName + ','','',''.'') AS DECIMAL('
                                      + CAST(MaxLength + 1 AS VARCHAR(255)) + ',' + CAST(DecimalPoints AS VARCHAR(255))
                                      + '))' END
                         WHEN [MaxLength] < 8 THEN
                             CASE
                                  WHEN LEN([MaxValue]) = 0
                                    OR LEN([MinValue]) = 0 THEN
                                      'CASE WHEN LEN(' + @ColName + ' ) > 0 THEN CAST(' + @ColName
                                      + ' AS int) ELSE NULL END'
                                  ELSE ' CAST(' + @ColName + ' AS int) ' END
                         ELSE
                             CASE
                                  WHEN LEN([MaxValue]) = 0
                                    OR LEN([MinValue]) = 0 THEN
                                      'CASE WHEN LEN(' + @ColName + ' ) > 0 THEN CAST(' + @ColName + ' AS DECIMAL('
                                      + CAST(LEN(MaxValue) + 4 AS VARCHAR(255)) + ',0)) ELSE NULL END '
                                  ELSE
                                      ' CAST(' + @ColName + ' AS DECIMAL(' + CAST(LEN(MaxValue) + 4 AS VARCHAR(255))
                                      + ',0))' END END END AS SelectExp,
           QUOTENAME(COLUMN_NAME) AS [ColAlias],
           NULL AS [SortOrder],
           0 AS [ExcludeColFromView],
           ROW_NUMBER() OVER (ORDER BY Ordinal_Position) AS Ordinal_Position
      INTO #PreIngestionTransfrom
      FROM #Describe;

    UPDATE      trg
       SET      trg.SelectExp = src.SelectExp
      FROM      #Describe trg
     INNER JOIN #PreIngestionTransfrom src
        ON QUOTENAME(trg.COLUMN_NAME) = src.ColName;


    SELECT FlowID,
           FlowType,
           Virtual,
           ColName,
           SelectExp,
           ColAlias,
           SortOrder,
           ExcludeColFromView
      FROM #PreIngestionTransfrom
     ORDER BY CASE
                   WHEN [ColName] = '[FileDate_DW]' THEN 2090
                   WHEN [ColName] = '[FileSize_DW]' THEN 2091
                   WHEN [ColName] = '[FileName_DW]' THEN 2092
				   WHEN [ColName] = '[FileLineNumber_DW]' THEN 2093
                   ELSE Ordinal_Position END ASC;

END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This is a template stored procedures which assess data in a table and recommends optimal data types for each column. 
							This can be vital to structure data warehouse entities in the most optimal value. ', 'SCHEMA', N'flw', 'PROCEDURE', N'FetchDataTypes', NULL, NULL
GO
