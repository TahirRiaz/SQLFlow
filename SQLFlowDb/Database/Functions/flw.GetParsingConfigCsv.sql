SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetParsingConfigCsv]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The purpose of the provided user-defined function [flw].[GetParsingConfigCsv] is to generate an XML configuration string that can be used to parse a comma-separated values (CSV) file.
  -- Summary			:	The function takes an input parameter @FlowID, which is used to fetch the required parsing configuration settings from the [flw].[PreIngestionCSV] table.
							
							The parsing configuration settings include the column delimiter, column widths, whether the first row has a header, the expected number of columns, 
							the number of rows to skip before parsing starts, whether to skip empty rows, whether to include file line numbers, whether to trim results, whether to strip control characters, whether the first row sets expected column count, the text qualifier, the escape character, the comment character, the number of rows to skip after parsing ends, the maximum buffer size, the maximum number of rows, the source encoding, the flag to delete ingested data, and the text field type.

							The function converts the column widths to an XML string using the [flw].[StringSplit] function, 
							and then generates an XML configuration string using the fetched parsing configuration settings and the converted column widths. 
							The resulting XML configuration string can be used with a generic parser to read the CSV file. The function returns the generated XML configuration string as the output.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/

CREATE FUNCTION [flw].[GetParsingConfigCsv] (
-- Add the parameters for the function here
@FlowID INT)
RETURNS NVARCHAR(MAX)
AS
BEGIN
    -- Declare the return variable here
    DECLARE @rValue NVARCHAR(MAX) = N'';
    DECLARE @ColumnDelimiter                 NVARCHAR(5),
            @ColumnWidths                    NVARCHAR(250),
            @FirstRowHasHeader               NVARCHAR(5),
            @ExpectedColumnCount             NVARCHAR(25),
            @SkipStartingDataRows            NVARCHAR(25),
            @SkipEmptyRows                   NVARCHAR(5),
            @IncludeFileLineNumber           NVARCHAR(5),
            @TrimResults                     NVARCHAR(5),
            @StripControlChars               NVARCHAR(5),
            @FirstRowSetsExpectedColumnCount NVARCHAR(5),
            @TextQualifier                   NVARCHAR(5),
            @EscapeCharacter                 NVARCHAR(5),
            @CommentCharacter                NVARCHAR(5),
            @SkipEndingDataRows              NVARCHAR(25),
            @MaxBufferSize                   NVARCHAR(25),
            @MaxRows                         NVARCHAR(25),
            @srcEncoding                     NVARCHAR(25),
            @srcDeleteIngested               NVARCHAR(5),
            @TextFieldType                   NVARCHAR(25),
            @ColumnWidthNodes                NVARCHAR(4000) = N'';

    SELECT @ColumnDelimiter = ISNULL(ColumnDelimiter, ''),
           @ColumnWidths = ISNULL(ColumnWidths, ''),
           @FirstRowHasHeader = IIF(ISNULL(FirstRowHasHeader, 0) = 0, 'False', 'True'),
           @ExpectedColumnCount = ISNULL(ExpectedColumnCount, 0),
           @SkipStartingDataRows = ISNULL(SkipStartingDataRows, 0),
           @SkipEmptyRows = IIF(ISNULL(SkipEmptyRows, 0) = 0, 'False', 'True'),
           @IncludeFileLineNumber = IIF(ISNULL(IncludeFileLineNumber, 0) = 0, 'False', 'True'),
           @TrimResults = IIF(ISNULL(TrimResults, 0) = 0, 'False', 'True'),
           @StripControlChars = IIF(ISNULL(StripControlChars, 0) = 0, 'False', 'True'),
           @FirstRowSetsExpectedColumnCount = IIF(ISNULL(FirstRowSetsExpectedColumnCount, 0) = 0, 'False', 'True'),
           @TextQualifier = ISNULL(TextQualifier, ''),
           @EscapeCharacter = ISNULL(EscapeCharacter, ''),
           @CommentCharacter = ISNULL(CommentCharacter, ''),
           @SkipEndingDataRows = ISNULL(SkipEndingDataRows, 0),
           @MaxBufferSize = ISNULL(MaxBufferSize, 1024),
           @MaxRows = ISNULL(MaxRows, 0)
      FROM flw.PreIngestionCSV
     WHERE FlowID = @FlowID;

    SELECT @ColumnWidthNodes = @ColumnWidthNodes + N'<ColumnWidth>' + item + N'</ColumnWidth>'
      FROM [flw].[StringSplit](@ColumnWidths, ',');
    SET @ColumnWidthNodes
        = CHAR(13) + CHAR(10) + N'<ColumnWidths>' + CHAR(13) + CHAR(10) + @ColumnWidthNodes + CHAR(13) + CHAR(10)
          + N'</ColumnWidths>';

    SET @rValue
        = N'<?xml version="1.0" encoding="utf-8"?>
<GenericParser> ' + IIF(LEN(@ColumnWidthNodes) > 0, @ColumnWidthNodes, '') + N'
  <MaxBufferSize>' + @MaxBufferSize + N'</MaxBufferSize>
  <MaxRows>' + @MaxRows + N'</MaxRows>
  <SkipStartingDataRows>' + @SkipStartingDataRows + N'</SkipStartingDataRows>
  <ExpectedColumnCount>' + @ExpectedColumnCount + N'</ExpectedColumnCount>
  <FirstRowHasHeader>' + @FirstRowHasHeader + N'</FirstRowHasHeader>
  <TrimResults>' + @TrimResults + N'</TrimResults>
  <StripControlChars>' + @StripControlChars + N'</StripControlChars>
  <SkipEmptyRows>' + @SkipEmptyRows + N'</SkipEmptyRows>
  <TextFieldType>' + IIF(LEN(@ColumnDelimiter) > 0, 'Delimited', 'FixedWidth')
          + N'</TextFieldType>
  <FirstRowSetsExpectedColumnCount>' + @FirstRowSetsExpectedColumnCount
          + N'</FirstRowSetsExpectedColumnCount>
  <ColumnDelimiter>' + [flw].[GetASQIIValue](@ColumnDelimiter) + N'</ColumnDelimiter>
  <TextQualifier>' + [flw].[GetASQIIValue](@TextQualifier) + N'</TextQualifier>
  <EscapeCharacter>' + [flw].[GetASQIIValue](@EscapeCharacter) + N'</EscapeCharacter>
  <CommentCharacter>' + [flw].[GetASQIIValue](@CommentCharacter) + N'</CommentCharacter>
  <IncludeFileLineNumber>' + @IncludeFileLineNumber + N'</IncludeFileLineNumber>
  <SkipEndingDataRows>' + @SkipEndingDataRows + N'</SkipEndingDataRows>
</GenericParser>
'   ;

    -- Return the result of the function
    RETURN @rValue;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The purpose of the provided user-defined function [flw].[GetParsingConfigCsv] is to generate an XML configuration string that can be used to parse a comma-separated values (CSV) file.
  -- Summary			:	The function takes an input parameter @FlowID, which is used to fetch the required parsing configuration settings from the [flw].[PreIngestionCSV] table.
							
							The parsing configuration settings include the column delimiter, column widths, whether the first row has a header, the expected number of columns, 
							the number of rows to skip before parsing starts, whether to skip empty rows, whether to include file line numbers, whether to trim results, whether to strip control characters, whether the first row sets expected column count, the text qualifier, the escape character, the comment character, the number of rows to skip after parsing ends, the maximum buffer size, the maximum number of rows, the source encoding, the flag to delete ingested data, and the text field type.

							The function converts the column widths to an XML string using the [flw].[StringSplit] function, 
							and then generates an XML configuration string using the fetched parsing configuration settings and the converted column widths. 
							The resulting XML configuration string can be used with a generic parser to read the CSV file. The function returns the generated XML configuration string as the output.', 'SCHEMA', N'flw', 'FUNCTION', N'GetParsingConfigCsv', NULL, NULL
GO
