CREATE TABLE [flw].[SysError]
(
[ErrorID] [int] NOT NULL IDENTITY(1, 1),
[ErrorMessage] [varchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Solution] [varchar] (500) COLLATE SQL_Latin1_General_CP1_CI_AS NULL
) ON [PRIMARY]
GO
ALTER TABLE [flw].[SysError] ADD CONSTRAINT [PK_SysError] PRIMARY KEY CLUSTERED ([ErrorID]) ON [PRIMARY]
GO
