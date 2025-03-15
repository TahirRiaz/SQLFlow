SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE FUNCTION [flw].[GetWebUiUrl] ()
RETURNS NVARCHAR(255)
AS
BEGIN

    RETURN (SELECT TOP 1 [WebUiUrl] FROM [flw].[SysWebUi])
END;
GO
