namespace SQLFlowCore.Services.Schema
{
    internal class InferDataTypes
    {
        internal static string GetDataTypeSQL(int FlowID, string FlowType, string Schema, string Table)
        {
            string rValue = $@"IF OBJECT_ID(N'tempdb..#DecimalPoints') IS NOT NULL
BEGIN
    DROP TABLE #DecimalPoints;
END;
CREATE TABLE #DecimalPoints
(
    Pattern VARCHAR(255),
    Points INT
);
INSERT INTO #DecimalPoints
VALUES
('%[.][0-9]%', 1),
('%[.][0-9][0-9]%', 2),
('%[.][0-9][0-9][0-9]%', 3),
('%[.][0-9][0-9][0-9][0-9]%', 4),
('%[.][0-9][0-9][0-9][0-9][0-9]%', 5),
('%[.][0-9][0-9][0-9][0-9][0-9][0-9]%', 6),
('%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%', 7),
('%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%', 8),
('%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%', 9),
('%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%', 10),
('%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%', 11),
('%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%', 12),
('%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%', 13),
('%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%', 14),
('%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%', 15),
('%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%', 16),
('%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%', 17),
('%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%', 18),
('%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%', 19),
('%[.][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]%', 20);

IF OBJECT_ID(N'tempdb..#SysDateFormats') IS NOT NULL
BEGIN
    DROP TABLE #SysDateFormats;
END;

CREATE TABLE #SysDateFormats
(
    [StyleCode] [INT] NULL,
    [Query] [NVARCHAR](250) NULL,
    [DateStyle] [NVARCHAR](50) NULL,
    [DateSample] [NVARCHAR](50) NULL,
    [Type] [NVARCHAR](50) NULL
) ON [PRIMARY];

INSERT #SysDateFormats
(
    [StyleCode],
    [Query],
    [DateStyle],
    [DateSample],
    [Type]
)
VALUES
(1, N'select convert(varchar, getdate(), 1)', N'mm/dd/yy', N'12/30/06', N'Date'),
(2, N'select convert(varchar, getdate(), 2)', N'yy.mm.dd', N'06.12.30', N'Date'),
(3, N'select convert(varchar, getdate(), 3)', N'dd/mm/yy', N'30/12/06', N'Date'),
(4, N'select convert(varchar, getdate(), 4)', N'dd.mm.yy', N'30.12.06', N'Date'),
(5, N'select convert(varchar, getdate(), 5)', N'dd-mm-yy', N'30-12-06', N'Date'),
(6, N'select convert(varchar, getdate(), 6)', N'dd-Mon-yy', N'30 Dec 06', N'Date'),
(7, N'select convert(varchar, getdate(), 7)', N'Mon dd, yy', N'Dec 30, 06', N'Date'),
(10, N'select convert(varchar, getdate(), 10)', N'mm-dd-yy', N'12-30-06', N'Date'),
(11, N'select convert(varchar, getdate(), 11)', N'yy/mm/dd', N'06/12/30', N'Date'),
(12, N'select convert(varchar, getdate(), 12)', N'yymmdd', N'061230', N'Date'),
(23, N'select convert(varchar, getdate(), 23)', N'yyyy-mm-dd', N'2006-12-30', N'Date'),
(101, N'select convert(varchar, getdate(), 101)', N'mm/dd/yyyy', N'12/30/2006', N'Date'),
(102, N'select convert(varchar, getdate(), 102)', N'yyyy.mm.dd', N'2006.12.30', N'Date'),
(103, N'select convert(varchar, getdate(), 103)', N'dd/mm/yyyy', N'30/12/2006', N'Date'),
(104, N'select convert(varchar, getdate(), 104)', N'dd.mm.yyyy', N'30.12.2006', N'Date'),
(105, N'select convert(varchar, getdate(), 105)', N'dd-mm-yyyy', N'30-12-2006', N'Date'),
(106, N'select convert(varchar, getdate(), 106)', N'dd Mon yyyy', N'30 Dec 2006', N'Date'),
(107, N'select convert(varchar, getdate(), 107)', N'Mon dd, yyyy', N'Dec 30, 2006', N'Date'),
(110, N'select convert(varchar, getdate(), 110)', N'mm-dd-yyyy', N'12-30-2006', N'Date'),
(111, N'select convert(varchar, getdate(), 111)', N'yyyy/mm/dd', N'2006/12/30', N'Date'),
(112, N'select convert(varchar, getdate(), 112)', N'yyyymmdd', N'20061230', N'Date'),
(8, N'select convert(varchar, getdate(), 8)', N'hh:mm:ss', N'00:38:54', N'Time'),
(14, N'select convert(varchar, getdate(), 14)', N'hh:mm:ss:nnn', N'00:38:54:840', N'Time'),
(24, N'select convert(varchar, getdate(), 24)', N'hh:mm:ss', N'00:38:54', N'Time'),
(108, N'select convert(varchar, getdate(), 108)', N'hh:mm:ss', N'00:38:54', N'Time'),
(114, N'select convert(varchar, getdate(), 114)', N'hh:mm:ss:nnn', N'00:38:54:840', N'Time'),
(0, N'select convert(varchar, getdate(), 0)', N'Mon dd yyyy hh:mm AM/PM', N'Dec 30 2006 12:38AM', N'DateTime'),
(9, N'select convert(varchar, getdate(), 9)', N'Mon dd yyyy hh:mm:ss:nnn AM/PM', N'Dec 30 2006 12:38:54:840AM',
 N'DateTime'),
(13, N'select convert(varchar, getdate(), 13)', N'dd Mon yyyy hh:mm:ss:nnn AM/PM', N'30 Dec 2006 00:38:54:840AM',
 N'DateTime'),
(20, N'select convert(varchar, getdate(), 20)', N'yyyy-mm-dd hh:mm:ss', N'2006-12-30 00:38:54', N'DateTime'),
(21, N'select convert(varchar, getdate(), 21)', N'yyyy-mm-dd hh:mm:ss:nnn', N'2006-12-30 00:38:54.840', N'DateTime'),
(22, N'select convert(varchar, getdate(), 22)', N'mm/dd/yy hh:mm:ss AM/PM', N'12/30/06 12:38:54 AM', N'DateTime'),
(25, N'select convert(varchar, getdate(), 25)', N'yyyy-mm-dd hh:mm:ss:nnn', N'2006-12-30 00:38:54.840', N'DateTime'),
(100, N'select convert(varchar, getdate(), 100)', N'Mon dd yyyy hh:mm AM/PM', N'Dec 30 2006 12:38AM', N'DateTime'),
(109, N'select convert(varchar, getdate(), 109)', N'Mon dd yyyy hh:mm:ss:nnn AM/PM', N'Dec 30 2006 12:38:54:840AM',
 N'DateTime'),
(113, N'select convert(varchar, getdate(), 113)', N'dd Mon yyyy hh:mm:ss:nnn', N'30 Dec 2006 00:38:54:840', N'DateTime'),
(120, N'select convert(varchar, getdate(), 120)', N'yyyy-mm-dd hh:mm:ss', N'2006-12-30 00:38:54', N'DateTime'),
(121, N'select convert(varchar, getdate(), 121)', N'yyyy-mm-dd hh:mm:ss:nnn', N'2006-12-30 00:38:54.840', N'DateTime'),
(126, N'select convert(varchar, getdate(), 126)', N'yyyy-mm-dd T hh:mm:ss:nnn', N'2006-12-30T00:38:54.840', N'DateTime'),
(127, N'select convert(varchar, getdate(), 127)', N'yyyy-mm-dd T hh:mm:ss:nnn', N'2006-12-30T00:38:54.840', N'DateTime');


IF OBJECT_ID(N'tempdb..#CultureTable') IS NOT NULL
BEGIN
    DROP TABLE #CultureTable;
END;

CREATE TABLE #CultureTable
(
    [CultureID] [INT] IDENTITY(1, 1) NOT NULL,
    Alias VARCHAR(255),
    LCID INT,
    SpecificCulture VARCHAR(255)
);

-- Insert data into the table
INSERT INTO #CultureTable
(
    Alias,
    LCID,
    SpecificCulture
)
VALUES
('Norwegian', 2068, 'nn-NO'),
('English', 1033, 'en-US'),
('Swedish', 1053, 'sv-SE'),
('German', 1031, 'de-DE'),
('French', 1036, 'fr-FR'),
('Japanese', 1041, 'ja-JP'),
('Danish', 1030, 'da-DK'),
('Spanish', 3082, 'es-ES'),
('Italian', 1040, 'it-IT'),
('Dutch', 1043, 'nl-NL'),
('Portuguese', 2070, 'pt-PT'),
('Finnish', 1035, 'fi-FI'),
('Czech', 1029, 'cs-CZ'),
('Hungarian', 1038, 'hu-HU'),
('Polish', 1045, 'pl-PL'),
('Romanian', 1048, 'ro-RO'),
('Croatian', 1050, 'hr-HR'),
('Slovak', 1051, 'sk-SK'),
('Slovenian', 1060, 'sl-SI'),
('Greek', 1032, 'el-GR'),
('Bulgarian', 1026, 'bg-BG'),
('Russian', 1049, 'ru-RU'),
('Turkish', 1055, 'tr-TR'),
('British English', 2057, 'en-GB'),
('Estonian', 1061, 'et-EE'),
('Latvian', 1062, 'lv-LV'),
('Lithuanian', 1063, 'lt-LT'),
('Brazilian', 1046, 'pt-BR'),
('Traditional Chinese', 1028, 'zh-TW'),
('Korean', 1042, 'ko-KR'),
('Simplified Chinese', 2052, 'zh-CN'),
('Arabic', 1025, 'ar-SA'),
('Thai', 1054, 'th-TH');

IF OBJECT_ID(N'tempdb..#Describe') IS NOT NULL
BEGIN
    DROP TABLE #Describe;
END;

CREATE TABLE #Describe
(
    [RecID] [INT] IDENTITY(1, 1) NOT NULL,
    [TableSchema] [NVARCHAR](128) NULL,
    [TableName] [sysname] NOT NULL,
    [ColumnName] [sysname] NULL,
    [Ordinal] [INT] NULL,
    [DataType] [NVARCHAR](128) NULL,
    [MinValue] [NVARCHAR](MAX) NULL,
    [MaxValue] [NVARCHAR](MAX) NULL,
    [RandValue] [NVARCHAR](MAX) NULL,
    [ValueWeight] INT NULL,
    [MinLength] [INT] NOT NULL,
    [MaxLength] [INT] NOT NULL,
    [SelectExp] [NVARCHAR](MAX) NULL,
    [CommaCount] INT NULL,
    [DotCount] INT NULL,
    [ColonCount] INT NULL,
    [IsDate] [BIT] NULL,
    [IsDateTime] [BIT] NULL,
    [IsTime] [BIT] NULL,
    [DateLocal] [INT] NULL,
    [ValAsDate] [DATETIME] NULL,
    [IsNumeric] [BIT] NULL,
    [DecimalPoints] [INT] NULL,
    [cmdSQL] [NVARCHAR](2484) NULL,
    [SQLFlowExp] [NVARCHAR](2484) NULL
);
INSERT INTO #Describe
(
    [TableSchema],
    [TableName],
    [ColumnName],
    [Ordinal],
    [DataType],
    [MinValue],
    [MaxValue],
    [RandValue],
    [MinLength],
    [MaxLength],
    [SelectExp],
    [CommaCount],
    [DotCount],
    [ColonCount],
    [IsDate],
    [IsDateTime],
    [IsTime],
    [DateLocal],
    [ValAsDate],
    [IsNumeric],
    [DecimalPoints],
    [cmdSQL],
    [SQLFlowExp]
)
SELECT TABLE_SCHEMA,
       TABLE_NAME,
       COLUMN_NAME,
       ORDINAL_POSITION,
       DATA_TYPE,
       CAST('' AS NVARCHAR(MAX)) AS MinValue,
       CAST('' AS NVARCHAR(MAX)) AS MaxValue,
       CAST('' AS NVARCHAR(MAX)) AS RandValue,
       0 AS MinLength,
       0 AS [MaxLength],
       CAST('' AS NVARCHAR(MAX)) AS SelectExp,
       CAST(0 AS BIT) AS CommaCount,
       CAST(0 AS BIT) AS DotCount,
       CAST(0 AS BIT) AS ColonCount,
       CAST(0 AS BIT) AS [IsDate],
       CAST(0 AS BIT) AS [IsDateTime],
       CAST(0 AS BIT) AS [IsTime],
       CAST(0 AS INT) AS DateLocal,
       CAST(NULL AS DATETIME) AS ValAsDate,
       CAST(0 AS BIT) AS [IsNumeric],
       CAST(0 AS INT) AS DecimalPoints,
       'WITH base AS (SELECT MAX(LEN([' + COLUMN_NAME + '])) AS [MaxLength], MIN(LEN([' + COLUMN_NAME
       + '])) AS [MinLength], MAX([' + COLUMN_NAME + ']) AS MaxValue, MIN([' + COLUMN_NAME + ']) AS [MinValue],'
       + ' (SELECT TOP 1  [' + COLUMN_NAME + '] FROM ( SELECT ROW_NUMBER() OVER (ORDER BY NEWID()) RowNumber, ['
       + COLUMN_NAME + '] FROM [' + TABLE_SCHEMA + '].[' + TABLE_NAME
       + '] ) sub WHERE RowNumber < 500 Order by RowNumber) AS RandValue, ''' + COLUMN_NAME + ''' as ColumnName FROM ['
       + TABLE_SCHEMA + '].[' + TABLE_NAME
       + ']
		   ) UPDATE Trg SET trg.MinLength = ISNull(src.MinLength,0),trg.MaxLength = Isnull(src.MaxLength,0),trg.MinValue = src.MinValue,trg.MaxValue = src.MaxValue, trg.RandValue = src.RandValue FROM #Describe trg INNER JOIN base src ON trg.ColumnName = src.ColumnName' cmdSQL,
       CAST('' AS NVARCHAR(500)) SQLFlowExp
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = '{Schema}'
      AND TABLE_NAME = '{Table}';

-- Ignore columns that dont support max operation
DELETE FROM #Describe
WHERE ColumnName IN (
    SELECT c.COLUMN_NAME
    FROM INFORMATION_SCHEMA.COLUMNS c 
    WHERE c.TABLE_SCHEMA = '{Schema}'
      AND c.TABLE_NAME = '{Table}'
      AND (c.DATA_TYPE IN ('bit', 'xml', 'hierarchyid', 'geography', 'geometry')
          OR c.DATA_TYPE LIKE '%image')
);

DECLARE @DateMinLength INT = 14;
DECLARE @ColName VARCHAR(255) = '@ColName'; --'CASE WHEN ''null'' =  @ColName THEN NULL ELSE @ColName END'

DECLARE @ColumnName VARCHAR(255) = '';
DECLARE @cmdSQL VARCHAR(MAX) = '';
DECLARE @Virtual BIT = 0;

--Populate #Describe Table
DECLARE cRec CURSOR FOR SELECT [ColumnName], cmdSQL FROM #Describe;
OPEN cRec;
FETCH NEXT FROM cRec
INTO @ColumnName,
     @cmdSQL;

WHILE @@FETCH_STATUS = 0
BEGIN
    EXEC (@cmdSQL);
    FETCH NEXT FROM cRec
    INTO @ColumnName,
         @cmdSQL;
END;
CLOSE cRec;
DEALLOCATE cRec;

--Count Dots
;
WITH BASE
AS (SELECT RecID,
           MAX(Value) MaxValue
    FROM
    (
        SELECT RecID,
               SUM(LEN([MinValue]) - LEN(REPLACE([MinValue], '.', ''))) MinValueCount,
               SUM(LEN([MaxValue]) - LEN(REPLACE([MaxValue], '.', ''))) MaxValueCount,
               SUM(LEN([RandValue]) - LEN(REPLACE([RandValue], '.', ''))) RandValueCount
        FROM #Describe
        GROUP BY RecID
    ) AS SourceTable
        UNPIVOT
        (
            Value
            FOR Column_Name IN (MinValueCount, MaxValueCount, RandValueCount)
        ) AS UnpivotedTable
    GROUP BY RecID)
UPDATE trg
SET trg.DotCount = BASE.MaxValue
FROM #Describe trg
    INNER JOIN BASE
        ON trg.RecID = BASE.RecID

--Count Commas
;
WITH BASE
AS (SELECT RecID,
           MAX(Value) MaxValue
    FROM
    (
        SELECT RecID,
               SUM(LEN([MinValue]) - LEN(REPLACE([MinValue], ',', ''))) MinValueCount,
               SUM(LEN([MaxValue]) - LEN(REPLACE([MaxValue], ',', ''))) MaxValueCount,
               SUM(LEN([RandValue]) - LEN(REPLACE([RandValue], ',', ''))) RandValueCount
        FROM #Describe
        GROUP BY RecID
    ) AS SourceTable
        UNPIVOT
        (
            Value
            FOR Column_Name IN (MinValueCount, MaxValueCount, RandValueCount)
        ) AS UnpivotedTable
    GROUP BY RecID)
UPDATE trg
SET trg.CommaCount = BASE.MaxValue
FROM #Describe trg
    INNER JOIN BASE
        ON trg.RecID = BASE.RecID


--Count Colons
;
WITH BASE
AS (SELECT RecID,
           MAX(Value) MaxValue
    FROM
    (
        SELECT RecID,
               SUM(LEN([MinValue]) - LEN(REPLACE([MinValue], ':', ''))) MinValueCount,
               SUM(LEN([MaxValue]) - LEN(REPLACE([MaxValue], ':', ''))) MaxValueCount,
               SUM(LEN([RandValue]) - LEN(REPLACE([RandValue], ':', ''))) RandValueCount
        FROM #Describe
        GROUP BY RecID
    ) AS SourceTable
        UNPIVOT
        (
            Value
            FOR Column_Name IN (MinValueCount, MaxValueCount, RandValueCount)
        ) AS UnpivotedTable
    GROUP BY RecID)
UPDATE trg
SET trg.ColonCount = BASE.MaxValue
FROM #Describe trg
    INNER JOIN BASE
        ON trg.RecID = BASE.RecID



--Calculate decimal points based on dots
;
WITH base
AS (SELECT RecID,
           MAX(Value) MaxValue
    FROM
    (
        SELECT RecID,
               CASE
                   WHEN (PATINDEX(Pattern, [MinValue]) > 0) THEN
                       Points
                   ELSE
                       0
               END [MinValuePoints],
               CASE
                   WHEN (PATINDEX(Pattern, [MaxValue]) > 0) THEN
                       Points
                   ELSE
                       0
               END [MaxValuePoints],
               CASE
                   WHEN (PATINDEX(Pattern, [RandValue]) > 0) THEN
                       Points
                   ELSE
                       0
               END [RandValuePoints]
        FROM #Describe trg
            CROSS APPLY
        (SELECT Pattern, Points FROM #DecimalPoints) a
        WHERE trg.DotCount = 1
    ) AS SourceTable
        UNPIVOT
        (
            Value
            FOR Column_Name IN ([MinValuePoints], [MaxValuePoints], [RandValuePoints])
        ) AS UnpivotedTable
    GROUP BY RecID)
UPDATE trg
SET trg.[DecimalPoints] = base.MaxValue
FROM #Describe trg
    INNER JOIN base
        ON trg.RecID = base.RecID


--Calculate decimal points based on commma
;
WITH base
AS (SELECT RecID,
           MAX(Value) MaxValue
    FROM
    (
        SELECT RecID,
               CASE
                   WHEN (PATINDEX(Pattern, REPLACE([MinValue], ',', '.')) > 0) THEN
                       Points
                   ELSE
                       0
               END [MinValuePoints],
               CASE
                   WHEN (PATINDEX(Pattern, REPLACE([MaxValue], ',', '.')) > 0) THEN
                       Points
                   ELSE
                       0
               END [MaxValuePoints],
               CASE
                   WHEN (PATINDEX(Pattern, REPLACE([RandValue], ',', '.')) > 0) THEN
                       Points
                   ELSE
                       0
               END [RandValuePoints]
        FROM #Describe trg
            CROSS APPLY
        (SELECT Pattern, Points FROM #DecimalPoints) a
        WHERE trg.CommaCount = 1
    ) AS SourceTable
        UNPIVOT
        (
            Value
            FOR Column_Name IN ([MinValuePoints], [MaxValuePoints], [RandValuePoints])
        ) AS UnpivotedTable
    GROUP BY RecID)
UPDATE trg
SET trg.[DecimalPoints] = base.MaxValue
FROM #Describe trg
    INNER JOIN base
        ON trg.RecID = base.RecID

--CheckForError for DateTime Value
;
WITH BASe
AS (SELECT RecID,
           [CultureID],
           [ValueWeight],
           ROW_NUMBER() OVER (PARTITION BY RecID ORDER BY RecID, [ValueWeight] DESC, [CultureID]) AS RowRank,
           Alias,
           SpecificCulture,
           [MinLength],
           [MaxLength]
    FROM
    (
        SELECT RecID,
               [CultureID],
                CASE WHEN LEN([MaxValue]) > 30 THEN 0 ELSE    
               ISDATE(TRY_PARSE(SUBSTRING([MinValue],1,30)  AS DATETIME USING SpecificCulture))
               + ISDATE(TRY_PARSE(SUBSTRING([MaxValue],1,30)  AS DATETIME USING SpecificCulture))
               + ISDATE(TRY_PARSE(SUBSTRING([RandValue],1,30)  AS DATETIME USING SpecificCulture)) END AS [ValueWeight],
               Alias,
               SpecificCulture,
               [MinLength],
               [MaxLength]
        FROM #Describe
            CROSS APPLY
        (SELECT [CultureID], Alias, SpecificCulture FROM #CultureTable) web
        WHERE [ColonCount] >= 1
    ) ab )
UPDATE trg
SET trg.IsDateTime = 1,
    trg.SQLFlowExp = 'CASE WHEN len(@ColName) > 0 THEN TRY_PARSE(@ColName AS DATETIME USING ''' + SpecificCulture
                     + ''') ELSE NULL END'
FROM #Describe trg
    INNER JOIN BASe src
        ON trg.RecID = src.RecID
WHERE src.RowRank = 1
      AND src.[ValueWeight]  >= 2;


--CheckForError for Date Value
;
WITH BASe
AS (SELECT RecID,
           [CultureID],
           [ValueWeight],
           ROW_NUMBER() OVER (PARTITION BY RecID ORDER BY RecID, [ValueWeight] DESC, [CultureID]) AS RowRank,
           Alias,
           SpecificCulture,
           [MinLength],
           [MaxLength]
    FROM
    (
        SELECT RecID,
               [CultureID],
                CASE WHEN LEN([MaxValue]) > 30 THEN 0 ELSE
                CASE
                   WHEN (TRY_PARSE(Substring([MinValue] ,1,30) AS DATE USING SpecificCulture)) IS NOT NULL THEN
                       1
                   ELSE
                       0
               END + CASE
                         WHEN (TRY_PARSE(Substring([MaxValue],1,30) AS DATE USING SpecificCulture)) IS NOT NULL THEN
                             1
                         ELSE
                             0
                     END + CASE
                               WHEN (TRY_PARSE(Substring(RandValue ,1,30) AS DATE USING SpecificCulture)) IS NOT NULL THEN
                                   1
                               ELSE
                                   0
                           END END AS [ValueWeight],
               Alias,
               SpecificCulture,
               [MinLength],
               [MaxLength]
        FROM #Describe
            CROSS APPLY
        (SELECT [CultureID], Alias, SpecificCulture FROM #CultureTable) web
        WHERE IsDateTime <> 1
    ) ab )
UPDATE trg
SET trg.[IsDate] = 1,
    trg.SQLFlowExp = 'CASE WHEN len(@ColName) > 0 THEN TRY_PARSE(@ColName AS DATE USING ''' + SpecificCulture
                     + ''') ELSE NULL END'
FROM #Describe trg
    INNER JOIN BASe src
        ON trg.RecID = src.RecID
WHERE src.RowRank = 1
      AND src.[ValueWeight]  >= 2;

--CheckForError for Time Value
;
WITH BASe
AS (SELECT RecID,
           [CultureID],
           [ValueWeight],
           ROW_NUMBER() OVER (PARTITION BY RecID ORDER BY RecID, [ValueWeight] DESC, [CultureID]) AS RowRank,
           Alias,
           SpecificCulture,
           [MinLength],
           [MaxLength]
    FROM
    (
        SELECT RecID,
               [CultureID],
               ROW_NUMBER() OVER (PARTITION BY RecID ORDER BY RecID, [ValueWeight] DESC, [CultureID]) AS RowRank,
                CASE WHEN LEN([MaxValue]) > 30 THEN 0 ELSE
                CASE
                   WHEN (TRY_PARSE(Substring([MinValue],1,8) AS TIME USING SpecificCulture)) IS NOT NULL THEN
                       1
                   ELSE
                       0
               END + CASE
                         WHEN (TRY_PARSE(Substring([MaxValue],1,8) AS TIME USING SpecificCulture)) IS NOT NULL THEN
                             1
                         ELSE
                             0
                     END + CASE
                               WHEN (TRY_PARSE(Substring(RandValue,1,8) AS TIME USING SpecificCulture)) IS NOT NULL THEN
                                   1
                               ELSE
                                   0
                           END END AS [ValueWeight],
               Alias,
               SpecificCulture,
               [MinLength],
               [MaxLength]
        FROM #Describe
            CROSS APPLY
        (SELECT [CultureID], Alias, SpecificCulture FROM #CultureTable) web
        WHERE IsDateTime <> 1
              AND [IsDate] <> 1
              AND ColonCount >= 1
    ) ab )
UPDATE trg
SET trg.IsTime = 1,
    trg.SQLFlowExp = 'CASE WHEN len(@ColName) > 0 THEN TRY_PARSE(@ColName AS TIME USING ''' + SpecificCulture
                     + ''') ELSE NULL END'
FROM #Describe trg
    INNER JOIN BASe src
        ON trg.RecID = src.RecID
WHERE src.RowRank = 1
      AND src.[ValueWeight]  >= 2;


--CheckForError for decimal values
WITH BASe
AS (SELECT RecID,
           [CultureID],
           [ValueWeight],
           ROW_NUMBER() OVER (PARTITION BY RecID ORDER BY RecID, [ValueWeight] DESC, [CultureID]) AS RowRank,
           Alias,
           SpecificCulture,
           [MinLength],
           [MaxLength]
    FROM
    (
        SELECT RecID,
               [CultureID],
               CASE WHEN LEN([MaxValue]) > 255 THEN 0 ELSE
                CASE
                   WHEN (TRY_PARSE(Substring([MinValue] ,1,53) AS FLOAT USING SpecificCulture)) IS NOT NULL THEN
                       1
                   ELSE
                       0
               END + CASE
                         WHEN (TRY_PARSE(Substring([MaxValue] ,1,53) AS FLOAT USING SpecificCulture)) IS NOT NULL THEN
                             1
                         ELSE
                             0
                     END + CASE
                               WHEN (TRY_PARSE(Substring([RandValue] ,1,53) AS FLOAT USING SpecificCulture)) IS NOT NULL THEN
                                   1
                               ELSE
                                   0
                           END END AS [ValueWeight],
               Alias,
               SpecificCulture,
               [MinLength],
               [MaxLength]
        FROM #Describe
            CROSS APPLY
        (SELECT [CultureID], Alias, SpecificCulture FROM #CultureTable) web
        WHERE DecimalPoints >= 1
    --AND recID = 21
    ) ab )
UPDATE trg
SET trg.[IsNumeric] = 1,
    trg.SQLFlowExp = 'CASE WHEN len(@ColName) > 0 THEN TRY_PARSE(@ColName AS DECIMAL('
                     + CAST(trg.[MaxLength] AS VARCHAR(255)) + ',' + CAST(trg.[DecimalPoints] AS VARCHAR(255))
                     + ') USING ''' + SpecificCulture + ''') ELSE NULL END'
FROM #Describe trg
    INNER JOIN BASe src
        ON trg.RecID = src.RecID
WHERE src.RowRank = 1
      AND src.[ValueWeight]  >= 2;


--CheckForError for smallint values
WITH BASe
AS (SELECT RecID,
           [CultureID],
           [ValueWeight],
           ROW_NUMBER() OVER (PARTITION BY RecID ORDER BY RecID, [ValueWeight] DESC, [CultureID]) AS RowRank,
           Alias,
           SpecificCulture,
           [MinLength],
           [MaxLength]
    FROM
    (
        SELECT RecID,
               [CultureID],
               CASE WHEN LEN([MaxValue]) > 255 THEN 0 ELSE
                CASE
                   WHEN (TRY_PARSE(substring([MinValue],1,6) AS SMALLINT USING SpecificCulture)) IS NOT NULL THEN
                       1
                   ELSE
                       0
               END + CASE
                         WHEN (TRY_PARSE(Substring([MaxValue],1,6) AS SMALLINT USING SpecificCulture)) IS NOT NULL THEN
                             1
                         ELSE
                             0
                     END + CASE
                               WHEN (TRY_PARSE(Substring([RandValue],1,6) AS SMALLINT USING SpecificCulture)) IS NOT NULL THEN
                                   1
                               ELSE
                                   0
                           END END AS [ValueWeight],
               Alias,
               SpecificCulture,
               [MinLength],
               [MaxLength]
        FROM #Describe
            CROSS APPLY
        (SELECT [CultureID], Alias, SpecificCulture FROM #CultureTable) web
        WHERE DecimalPoints = 0
              AND [IsDateTime] = 0
              AND [IsDate] = 0
              AND [IsTime] = 0
    --AND recID = 21 = 1
    ) ab )
UPDATE trg
SET trg.[IsNumeric] = 1,
    trg.SQLFlowExp = 'CASE WHEN len(@ColName) > 0 THEN TRY_PARSE(@ColName AS smallint USING ''' + SpecificCulture
                     + ''') ELSE NULL END'
FROM #Describe trg
    INNER JOIN BASe src
        ON trg.RecID = src.RecID
WHERE src.RowRank = 1
      AND src.[ValueWeight]  >= 2;

--CheckForError for int values
WITH BASe
AS (SELECT RecID,
           [CultureID],
           [ValueWeight],
           ROW_NUMBER() OVER (PARTITION BY RecID ORDER BY RecID, [ValueWeight] DESC, [CultureID]) AS RowRank,
           Alias,
           SpecificCulture,
           [MinLength],
           [MaxLength]
    FROM
    (
        SELECT RecID,
               [CultureID],
               CASE WHEN LEN([MaxValue]) > 255 THEN 0 ELSE
                CASE
                   WHEN (TRY_PARSE(Substring([MinValue] ,1,10) AS INT USING SpecificCulture)) IS NOT NULL THEN
                       1
                   ELSE
                       0
               END + CASE
                         WHEN (TRY_PARSE(Substring([MaxValue],1,10) AS INT USING SpecificCulture)) IS NOT NULL THEN
                             1
                         ELSE
                             0
                     END + CASE
                               WHEN (TRY_PARSE(Substring([RandValue],1,10) AS INT USING SpecificCulture)) IS NOT NULL THEN
                                   1
                               ELSE
                                   0
                           END END AS [ValueWeight],
               Alias,
               SpecificCulture,
               [MinLength],
               [MaxLength]
        FROM #Describe
            CROSS APPLY
        (SELECT [CultureID], Alias, SpecificCulture FROM #CultureTable) web
        WHERE DecimalPoints = 0
              AND [IsDateTime] = 0
              AND [IsDate] = 0
              AND [IsTime] = 0
    --AND recID = 21 = 1
    ) ab )
UPDATE trg
SET trg.[IsNumeric] = 1,
    trg.SQLFlowExp = 'CASE WHEN len(@ColName) > 0 THEN TRY_PARSE(@ColName AS INT USING ''' + SpecificCulture
                     + ''') ELSE NULL END'
FROM #Describe trg
    INNER JOIN BASe src
        ON trg.RecID = src.RecID
WHERE src.RowRank = 1
      AND trg.[IsNumeric] <> 1 --Exclude small int
      AND src.[ValueWeight]  >= 2;




--CheckForError for bigint values
WITH BASe
AS (SELECT RecID,
           [CultureID],
           [ValueWeight],
           ROW_NUMBER() OVER (PARTITION BY RecID ORDER BY RecID, [ValueWeight] DESC, [CultureID]) AS RowRank,
           Alias,
           SpecificCulture,
           [MinLength],
           [MaxLength]
    FROM
    (
        SELECT RecID,
               [CultureID],
                CASE WHEN LEN([MaxValue]) > 255 THEN 0 ELSE   
                CASE
                   WHEN (TRY_PARSE(Substring([MinValue],1,19) AS BIGINT USING SpecificCulture)) IS NOT NULL THEN
                       1
                   ELSE
                       0
               END + CASE
                         WHEN (TRY_PARSE(Substring([MaxValue],1,19) AS BIGINT USING SpecificCulture)) IS NOT NULL THEN
                             1
                         ELSE
                             0
                     END + CASE
                               WHEN (TRY_PARSE(Substring([RandValue],1,19) AS BIGINT USING SpecificCulture)) IS NOT NULL THEN
                                   1
                               ELSE
                                   0
                           END END AS [ValueWeight],
               Alias,
               SpecificCulture,
               [MinLength],
               [MaxLength]
        FROM #Describe
            CROSS APPLY
        (SELECT [CultureID], Alias, SpecificCulture FROM #CultureTable) web
        WHERE DecimalPoints = 0
              AND [IsDateTime] = 0
              AND [IsDate] = 0
              AND [IsTime] = 0
    --AND recID = 21 = 1
    ) ab )
UPDATE trg
SET trg.[IsNumeric] = 1,
    trg.SQLFlowExp = 'CASE WHEN len(@ColName) > 0 THEN TRY_PARSE(@ColName AS bigint USING ''' + SpecificCulture
                     + ''') ELSE NULL END'
FROM #Describe trg
    INNER JOIN BASe src
        ON trg.RecID = src.RecID
WHERE src.RowRank = 1
      AND trg.[IsNumeric] <> 1 --Exclude smallint and int
      AND src.[ValueWeight]  >= 2;


--Set String and System Columns
UPDATE trg
SET trg.SQLFlowExp = CASE
                                 WHEN [MaxLength]
                                      BETWEEN 0 AND 50 THEN
                                     'CAST(@ColName as varchar (50))'
                                 WHEN [MaxLength]
                                      BETWEEN 51 AND 255 THEN
                                     'CAST(@ColName as varchar (255))'
                                 WHEN [MaxLength]
                                      BETWEEN 256 AND 1024 THEN
                                     'CAST(@ColName as varchar (1024))'
                                 ELSE
                                     'CAST(@ColName as varchar (max))'
                     END
FROM #Describe trg
WHERE ISNULL([IsDate], 0) = 0
      AND ISNULL([IsDateTime], 0) = 0
      AND ISNULL([IsTime], 0) = 0
      AND ISNULL([IsNumeric], 0) = 0;

UPDATE trg
SET trg.SQLFlowExp = CASE
                         WHEN QUOTENAME(ColumnName) = '[DataSet_DW]' THEN
                             'CAST(@ColName as decimal(14,0))'
                         WHEN QUOTENAME(ColumnName) = '[FileDate_DW]' THEN
                             'CAST(@ColName as decimal(14,0))'
                         WHEN QUOTENAME(ColumnName) = '[FileSize_DW]' THEN
                             'CAST(@ColName as decimal(18,0))'
                         WHEN QUOTENAME(ColumnName) = '[FileName_DW]' THEN
                             'CAST(@ColName as varchar (255))'
                         WHEN QUOTENAME(ColumnName) = '[FileLineNumber_DW]' THEN
                             'CAST(@ColName as int)'
                     ELSE trg.SQLFlowExp
					 END
FROM #Describe trg


DECLARE @FlowID INT = '{FlowID.ToString()}',
        @FlowType VARCHAR(50) = '{FlowType}';

--Final Result For SQLFlow
SELECT @FlowID AS [FlowID],
       @FlowType AS [FlowType],
       0 AS [Virtual],
       QUOTENAME(ColumnName) AS [ColName],
       SQLFlowExp AS [SelectExp],
       QUOTENAME(ColumnName) AS [ColAlias],
       CASE
           WHEN QUOTENAME(ColumnName) = '[FileDate_DW]' THEN
               2090
           WHEN QUOTENAME(ColumnName) = '[FileSize_DW]' THEN
               2091
           WHEN QUOTENAME(ColumnName) = '[FileName_DW]' THEN
               2092
           WHEN QUOTENAME(ColumnName) = '[FileLineNumber_DW]' THEN
               2093
           ELSE
               Ordinal
       END AS [SortOrder],
       0 AS ExcludeColFromView
FROM #Describe
ORDER BY CASE
             WHEN QUOTENAME(ColumnName) = '[FileDate_DW]' THEN
                 2090
             WHEN QUOTENAME(ColumnName) = '[FileSize_DW]' THEN
                 2091
             WHEN QUOTENAME(ColumnName) = '[FileName_DW]' THEN
                 2092
             WHEN QUOTENAME(ColumnName) = '[FileLineNumber_DW]' THEN
                 2093
             ELSE
                 Ordinal
         END ASC;
";

            return rValue;
        }

    }
}

