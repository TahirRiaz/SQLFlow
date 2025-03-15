SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE FUNCTION [flw].[GetInternalTables]
()
RETURNS TABLE
AS
RETURN
(
    SELECT ObjectName
	FROM [flw].[SysTableType]
	WHERE [Type] = 'Internal'
);
--[flw].[IngestionTokenExp],
GO
