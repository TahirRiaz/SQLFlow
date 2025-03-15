CREATE TABLE [dbo].[AspNetUsers]
(
[Id] [nvarchar] (450) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[AccessFailedCount] [int] NOT NULL,
[ConcurrencyStamp] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Email] [nvarchar] (256) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[EmailConfirmed] [bit] NOT NULL,
[LockoutEnabled] [bit] NOT NULL,
[LockoutEnd] [datetimeoffset] NULL,
[NormalizedEmail] [nvarchar] (256) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[NormalizedUserName] [nvarchar] (256) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PasswordHash] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PhoneNumber] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PhoneNumberConfirmed] [bit] NOT NULL,
[SecurityStamp] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[TwoFactorEnabled] [bit] NOT NULL,
[UserName] [nvarchar] (256) COLLATE SQL_Latin1_General_CP1_CI_AS NULL
) ON [PRIMARY]
GO
ALTER TABLE [dbo].[AspNetUsers] ADD CONSTRAINT [PK_AspNetUsers] PRIMARY KEY CLUSTERED ([Id]) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [EmailIndex] ON [dbo].[AspNetUsers] ([NormalizedEmail]) ON [PRIMARY]
GO
CREATE UNIQUE NONCLUSTERED INDEX [UserNameIndex] ON [dbo].[AspNetUsers] ([NormalizedUserName]) WHERE ([NormalizedUserName] IS NOT NULL) ON [PRIMARY]
GO
