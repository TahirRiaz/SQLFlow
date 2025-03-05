SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
-- =============================================
-- Author:      <Author, , Name>
-- Create Date: <Create Date, , >
-- Description: <Description, , >
-- =============================================
CREATE PROCEDURE [dbo].[demosp]
(
    @file_name NVARCHAR(250),
    @folder_name NVARCHAR(100),
    @full_path NVARCHAR(500)
)
AS
BEGIN
    -- SET NOCOUNT ON added to prevent extra result sets from
    -- interfering with SELECT statements.
    SET NOCOUNT ON

    -- Insert statements for procedure here
	INSERT INTO dbo.temp
    SELECT @file_name AS file_name, @folder_name AS folder_name, @full_path AS full_path
	
	
END
GO
