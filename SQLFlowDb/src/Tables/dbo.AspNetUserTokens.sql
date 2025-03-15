CREATE TABLE [dbo].[AspNetUserTokens]
(
[UserId] [nvarchar] (450) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[LoginProvider] [nvarchar] (450) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[Name] [nvarchar] (450) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[Value] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL
) ON [PRIMARY]
GO
ALTER TABLE [dbo].[AspNetUserTokens] ADD CONSTRAINT [PK_AspNetUserTokens] PRIMARY KEY CLUSTERED ([UserId], [LoginProvider], [Name]) ON [PRIMARY]
GO
ALTER TABLE [dbo].[AspNetUserTokens] ADD CONSTRAINT [FK_AspNetUserTokens_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[AspNetUsers] ([Id]) ON DELETE CASCADE
GO
