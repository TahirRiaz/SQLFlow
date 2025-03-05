SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE FUNCTION [dbo].[GetStableHash] (@input NVARCHAR(MAX))
RETURNS INT
AS
BEGIN
    DECLARE @hash INT = 17;
    DECLARE @i INT = 1;
    DECLARE @char INT;
    DECLARE @len INT = LEN(@input);

    WHILE @i <= @len
    BEGIN
        SET @char = UNICODE(SUBSTRING(@input, @i, 1));
        SET @hash = @hash * 23 + @char;
        SET @i = @i + 1;
    END

    RETURN @hash;
END
GO
