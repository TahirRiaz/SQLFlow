CREATE TABLE [flw].[SysDateTimeFormat]
(
[FormatID] [int] NOT NULL IDENTITY(1, 1),
[Format] [nvarchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FormatLength] AS (len([Format]))
) ON [PRIMARY]
GO
ALTER TABLE [flw].[SysDateTimeFormat] ADD CONSTRAINT [PK_FormatID] PRIMARY KEY CLUSTERED ([FormatID]) ON [PRIMARY]
GO
