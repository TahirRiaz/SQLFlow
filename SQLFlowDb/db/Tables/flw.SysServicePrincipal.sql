CREATE TABLE [flw].[SysServicePrincipal]
(
[ServicePrincipalID] [int] NOT NULL IDENTITY(1, 1),
[ServicePrincipalAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[TenantId] [nvarchar] (100) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SubscriptionId] [nvarchar] (100) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ApplicationId] [nvarchar] (100) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ClientSecret] [nvarchar] (100) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ResourceGroup] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DataFactoryName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[AutomationAccountName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[StorageAccountName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[BlobContainer] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[KeyVaultName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL
) ON [PRIMARY]
GO
ALTER TABLE [flw].[SysServicePrincipal] ADD CONSTRAINT [PK_SysServicePrincipal] PRIMARY KEY CLUSTERED ([ServicePrincipalID]) ON [PRIMARY]
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysServicePrincipal] table hosts connectivity information for executing Azure Data Factory Pipelines and Azure Automation runbooks.', 'SCHEMA', N'flw', 'TABLE', N'SysServicePrincipal', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', 'The ApplicationId column in the flw.SysServicePrincipal table stores the ID of the Azure Active Directory (AAD) application associated with the service principal. This value is required to authenticate the service principal and authorize it to perform actions in Azure.', 'SCHEMA', N'flw', 'TABLE', N'SysServicePrincipal', 'COLUMN', N'ApplicationId'
GO
EXEC sp_addextendedproperty N'MS_Description', 'This column stores the name of the Azure Automation Account associated with the service principal in the flw.SysServicePrincipal table.



', 'SCHEMA', N'flw', 'TABLE', N'SysServicePrincipal', 'COLUMN', N'AutomationAccountName'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The ClientSecret column in the flw.SysServicePrincipal table stores a secret key that is used to authenticate the service principal to access Azure resources.', 'SCHEMA', N'flw', 'TABLE', N'SysServicePrincipal', 'COLUMN', N'ClientSecret'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The DataFactoryName column in the flw.SysServicePrincipal table stores the name of the Azure Data Factory instance that the service principal is used for connecting to.', 'SCHEMA', N'flw', 'TABLE', N'SysServicePrincipal', 'COLUMN', N'DataFactoryName'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysServicePrincipal].[ResourceGroup] column in the flw.SysServicePrincipal table stores the name of the Azure resource group that contains the Azure Data Factory or Azure Automation account associated with the service principal.', 'SCHEMA', N'flw', 'TABLE', N'SysServicePrincipal', 'COLUMN', N'ResourceGroup'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysServicePrincipal].[ServicePrincipalAlias] stores a user-defined alias for the service principal.', 'SCHEMA', N'flw', 'TABLE', N'SysServicePrincipal', 'COLUMN', N'ServicePrincipalAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysServicePrincipal].[ServicePrincipalID]', 'SCHEMA', N'flw', 'TABLE', N'SysServicePrincipal', 'COLUMN', N'ServicePrincipalID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The SubscriptionId column in the flw.SysServicePrincipal table stores the unique identifier of the Azure subscription associated with the service principal.', 'SCHEMA', N'flw', 'TABLE', N'SysServicePrincipal', 'COLUMN', N'SubscriptionId'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The TenantId column in the flw.SysServicePrincipal table stores the unique identifier for the Azure AD tenant associated with the service principal. This identifier is assigned to the tenant by Microsoft Azure Active Directory and is a globally unique identifier (GUID). The Azure AD tenant represents an organization or a group of users who share a common access to Azure resources.', 'SCHEMA', N'flw', 'TABLE', N'SysServicePrincipal', 'COLUMN', N'TenantId'
GO
