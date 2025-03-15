SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name             :		[sqf].[SplitStr2Tbl]
  -- Date             :		2020.11.06
  -- Author           :		Tahir Riaz
  -- Company          :		Business IQ
  -- Purpose          :		Returns a table of strings that have been split by a delimiter.
							The strings are trimmed before being returned.  Null items are not
							returned so if there are multiple separators between items, 
							only the non-null items are returned.
  -- Usage			  :		SELECT * from [sqf].[SplitStr2Tbl]('Val1,Val2,Val3',',')
  -- Required grants  :		SELECT
  -- Called by        :		[sqf].[ExecBulkload]
  -- Notes			  :		Space is not a valid delimiter.
  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		11.06.2020		Initial
  ##################################################################################################################################################
*/
CREATE FUNCTION [flw].[SplitStr2Tbl](
    @sInputList varchar(8000) -- List of delimited items
  , @Delimiter char(1) = ',' -- delimiter that separates items
)   
RETURNS @List TABLE (recId int identity(1,1), Item varchar(8000))

AS BEGIN

DECLARE @Item Varchar(8000)
DECLARE @Pos int -- Current Starting Position
      , @NextPos int -- position of next delimiter
      , @LenInput int -- length of input
      , @LenNext int -- length of next item
      , @DelimLen int -- length of the delimiter

SELECT @Pos = 1
     , @DelimLen = LEN(@Delimiter) --  usually 1 
     , @LenInput = LEN(@sInputList)
     , @NextPos = CharIndex(@Delimiter, @sInputList, 1) 

-- Doesn't work for space as a delimiter
IF @Delimiter = ' ' BEGIN
   INSERT INTO @List (Item)
       SELECT 'ERROR: Blank is not a valid delimiter'
   RETURN
END


-- loop over the input, until the last delimiter.
While @Pos <= @LenInput and @NextPos > 0 BEGIN

    IF @NextPos > @Pos BEGIN -- another delimiter found
       SET @LenNext = @NextPos - @Pos           
       Set @Item = LTrim(RTrim(
                            substring(@sInputList
                                   , @Pos
                                  , @LenNext)
                               )
                         ) 
       IF LEN(@Item) > 0 
           Insert Into @List(Item) Select @Item
       -- ENDIF

    END -- IF

    -- Position over the next item
    SELECT @Pos = @NextPos + @DelimLen
         , @NextPos = CharIndex(@Delimiter
                              , @sInputList
                              , @Pos) 
END

-- Now there might be one more item left
SET @Item = LTrim(RTrim(
                      SUBSTRING(@sInputList
                               , @Pos
                               , @LenInput-@Pos + 1)
                       )
                 )

IF Len(@Item) > 0 -- Put the last item in, if found
   INSERT INTO @List(Item)   SELECT @Item

RETURN
END
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose:				The StrCleanup function removes any invalid characters from the input string and returns the cleaned up string. 							The strings are trimmed before being returned.  Null items are not
							returned so if there are multiple separators between items, 
							only the non-null items are returned.', 'SCHEMA', N'flw', 'FUNCTION', N'SplitStr2Tbl', NULL, NULL
GO
