CREATE TABLE [flw].[SysDataSource]
(
[DataSourceID] [int] NOT NULL IDENTITY(1, 1),
[SourceType] [nvarchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DatabaseName] [nvarchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Alias] [nvarchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ConnectionString] [nvarchar] (2000) COLLATE SQL_Latin1_General_CP1_CI_AS MASKED WITH (FUNCTION = 'default()') NULL,
[ServicePrincipalAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[KeyVaultSecretName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SupportsCrossDBRef] [bit] NULL CONSTRAINT [DF_SysDataSource_SupportsRemoteScripting] DEFAULT ((0)),
[IsSynapse] [bit] NULL CONSTRAINT [DF_IngestionDS_IsSynapse] DEFAULT ((0)),
[IsLocal] [bit] NULL CONSTRAINT [DF_SysDataSource_IsLocal] DEFAULT ((0)),
[ActivityMonitoring] [bit] NULL
) ON [PRIMARY]
GO
ALTER TABLE [flw].[SysDataSource] ADD CONSTRAINT [PK_DataSourceID] PRIMARY KEY CLUSTERED ([DataSourceID]) ON [PRIMARY]
GO
GRANT SELECT ON  [flw].[SysDataSource] TO [TestUser]
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysDataSource] table stores the connection strings to various relational databases. It supports MySQL, SQL Server, and Azure DB. The [Alias] column in this table is used as a source and target server in various SQLFlow processes. This table also contains information related to the Key Vault used for storing credentials, whether the data source supports cross-database references, whether it is an Azure Synapse Analytics workspace, and whether it is a local data source.', 'SCHEMA', N'flw', 'TABLE', N'SysDataSource', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', 'The Alias column in the flw.SysDataSource table refers to a user-defined name or identifier that represents a specific data source. This alias is used as a reference to the data source in various SQLFlow processes.', 'SCHEMA', N'flw', 'TABLE', N'SysDataSource', 'COLUMN', N'Alias'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The SQLConnectionString column in the flw.SysDataSource table stores the connection string to the relational database. This connection string is used to establish a connection to the data source when executing SQLFlow processes.', 'SCHEMA', N'flw', 'TABLE', N'SysDataSource', 'COLUMN', N'ConnectionString'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysDataSource].[DatabaseName] column stores the name of the data source, such as the hostname or IP address of the server where the database is hosted.', 'SCHEMA', N'flw', 'TABLE', N'SysDataSource', 'COLUMN', N'DatabaseName'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysDataSource].[DataSourceID] column is an identity column and serves as the primary key of the [flw].[SysDataSource] table. It uniquely identifies each row in the table.', 'SCHEMA', N'flw', 'TABLE', N'SysDataSource', 'COLUMN', N'DataSourceID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysDataSource].[IsLocal] column is a bit (boolean) column that indicates whether the data source is located on the same server as SQLFlow or not. A value of 1 indicates that the data source is local, while a value of 0 indicates that the data source is remote. This information can be used by SQLFlow to optimize data transfer and processing.', 'SCHEMA', N'flw', 'TABLE', N'SysDataSource', 'COLUMN', N'IsLocal'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The IsSynapse column in the [flw].[SysDataSource] table is a boolean flag that indicates whether the data source is a Microsoft Azure Synapse Analytics database (formerly known as Azure SQL Data Warehouse). When set to 1, it indicates that the data source is a Synapse Analytics database, and when set to 0 or NULL, it indicates that the data source is not a Synapse Analytics database.', 'SCHEMA', N'flw', 'TABLE', N'SysDataSource', 'COLUMN', N'IsSynapse'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysDataSource].[SourceType] column in the [flw].[SysDataSource] table specifies the type of the database source. The valid values for this column are "MySQL", "MSSQL" (Microsoft SQL Server), and "AZDB" (Azure SQL Database).', 'SCHEMA', N'flw', 'TABLE', N'SysDataSource', 'COLUMN', N'SourceType'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysDataSource].SupportsCrossDBRef column specifies whether the data source supports cross-database references or not. If this column is set to 1, cross-database references are supported, otherwise, they are not. Cross-database references allow SQL queries to reference tables from different databases in the same server.', 'SCHEMA', N'flw', 'TABLE', N'SysDataSource', 'COLUMN', N'SupportsCrossDBRef'
GO
