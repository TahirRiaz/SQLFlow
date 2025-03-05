CREATE TABLE [flw].[SysAPIKey]
(
[ApiKeyID] [int] NOT NULL IDENTITY(1, 1),
[ServiceType] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[ApiKeyAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[AccessKey] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SecretKey] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS MASKED WITH (FUNCTION = 'default()') NULL,
[ServicePrincipalAlias] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[KeyVaultSecretName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL
)
GO
ALTER TABLE [flw].[SysAPIKey] ADD CONSTRAINT [PK_APIKeyID] PRIMARY KEY CLUSTERED ([ApiKeyID])
GO
CREATE UNIQUE NONCLUSTERED INDEX [NCI_SysAPIKey] ON [flw].[SysAPIKey] ([ApiKeyAlias])
GO
