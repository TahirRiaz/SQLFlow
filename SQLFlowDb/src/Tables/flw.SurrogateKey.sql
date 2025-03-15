CREATE TABLE [flw].[SurrogateKey]
(
[SurrogateKeyID] [int] NOT NULL IDENTITY(1, 1),
[FlowID] [int] NOT NULL,
[SurrogateServer] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SurrogateDbSchTbl] [nvarchar] (150) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SurrogateColumn] [nvarchar] (150) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[KeyColumns] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[sKeyColumns] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PreProcess] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PostProcess] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ToObjectMK] [int] NULL
) ON [PRIMARY]
GO
ALTER TABLE [flw].[SurrogateKey] ADD CONSTRAINT [PK_SurrogateKey] PRIMARY KEY CLUSTERED ([SurrogateKeyID]) ON [PRIMARY]
GO
EXEC sp_addextendedproperty N'MS_Description', 'Final output of the lineage calculation is stored  [flw].[LineageParsed]. This table contains the calculated overall execution order of a SQLFlow processes. This table is used to device the execution order for Batches.', 'SCHEMA', N'flw', 'TABLE', N'SurrogateKey', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SurrogateKey].[FlowID] column is used to store the ID of the flow to which the surrogate key generation is added as a post-process. This way, the generated key value can be automatically pushed back to the source table after the flow execution, as part of the post-processing step.', 'SCHEMA', N'flw', 'TABLE', N'SurrogateKey', 'COLUMN', N'FlowID'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SurrogateKey].[KeyColumns] specifies the business key column(s) used to generate the surrogate key. If multiple columns are used, they should be separated by comma.', 'SCHEMA', N'flw', 'TABLE', N'SurrogateKey', 'COLUMN', N'KeyColumns'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SurrogateKey].[PostProcess]', 'SCHEMA', N'flw', 'TABLE', N'SurrogateKey', 'COLUMN', N'PostProcess'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SurrogateKey].[PreProcess] column contains the name of a stored procedure to execute before generating the surrogate key. This stored procedure can be used to clean, validate, or transform the incoming business key value before generating the surrogate key.', 'SCHEMA', N'flw', 'TABLE', N'SurrogateKey', 'COLUMN', N'PreProcess'
GO
EXEC sp_addextendedproperty N'MS_Description', '[sKeyColumns] is used when you need to create a mapping between the source column and the surrogate key column. This is useful when you are consolidating data from various systems into the same surrogate key. The sKeyColumns value is a comma-separated list of column names, with each column representing the source column that maps to the corresponding surrogate key column.', 'SCHEMA', N'flw', 'TABLE', N'SurrogateKey', 'COLUMN', N'sKeyColumns'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The SurrogateColumn column in the flw.SurrogateKey table contains the name of the column that will store the generated surrogate key value in the target table. When a new row is inserted into the target table, the surrogate key value will be generated and inserted into this column. The column must exist in the target table and have a compatible data type with the generated surrogate key value.', 'SCHEMA', N'flw', 'TABLE', N'SurrogateKey', 'COLUMN', N'SurrogateColumn'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SurrogateKey].[SurrogateDbSchTbl] column contains the database, schema, and table name where the generated surrogate keys will be stored. The format of this column should be <Database>.<Schema>.<Table>. For example, MyDB.dbo.Customer.

When a surrogate key is generated for a row, the value will be inserted into this table in the specified column ([flw].[SurrogateKey].[SurrogateColumn]). The [flw].[SurrogateKey].[KeyColumns] column specifies the name(s) of the column(s) in the target table that will be used to generate the surrogate key. The [flw].[SurrogateKey].[sKeyColumns] column specifies the name(s) of the column(s) in the target table that will receive the generated surrogate key value(s).

Note that the target table should have a primary key or unique index on the key column(s) specified in [flw].[SurrogateKey].[KeyColumns].', 'SCHEMA', N'flw', 'TABLE', N'SurrogateKey', 'COLUMN', N'SurrogateDbSchTbl'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SurrogateKey].[SurrogateKeyID] is an identity column used as a primary key for the [flw].[SurrogateKey] table. Each row in the table represents the configuration for generating surrogate keys for a specific source table. ', 'SCHEMA', N'flw', 'TABLE', N'SurrogateKey', 'COLUMN', N'SurrogateKeyID'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SurrogateKey].[SurrogateServer] is a column in the SurrogateKey table that stores the server name where the surrogate key values will be generated. Adding a server enables a test environment to fetch surrogate key values from a Production environment. When surrogate key generation is executed in a non-production environment, it can use the server name to connect to a production server to retrieve the next surrogate key value. This feature is useful when testing or validating data in non-production environments that must maintain referential integrity with production data.', 'SCHEMA', N'flw', 'TABLE', N'SurrogateKey', 'COLUMN', N'SurrogateServer'
GO
EXEC sp_addextendedproperty N'MS_Description', 'ToObjectMK column stores the unique identifier for the target object.', 'SCHEMA', N'flw', 'TABLE', N'SurrogateKey', 'COLUMN', N'ToObjectMK'
GO
