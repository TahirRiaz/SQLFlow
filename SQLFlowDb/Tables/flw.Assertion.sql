CREATE TABLE [flw].[Assertion]
(
[AssertionID] [int] NOT NULL IDENTITY(1, 1),
[AssertionName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[AssertionExp] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL
)
GO
ALTER TABLE [flw].[Assertion] ADD CONSTRAINT [PK_Assertions] PRIMARY KEY CLUSTERED ([AssertionID])
GO
