SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE FUNCTION [flw].[GetWebApiUrl] ()
RETURNS nvarchar(255)
AS
BEGIN

    RETURN (SELECT TOP 1 [WebApiUrl] FROM [flw].[SysWebApi])
END;
GO
