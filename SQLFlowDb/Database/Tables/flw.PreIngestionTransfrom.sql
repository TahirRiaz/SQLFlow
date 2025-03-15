CREATE TABLE [flw].[PreIngestionTransfrom]
(
[TransfromID] [int] NOT NULL IDENTITY(1, 1),
[FlowID] [int] NOT NULL,
[FlowType] [nvarchar] (25) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[Virtual] [bit] NULL CONSTRAINT [DF_PreIngestionTransfrom_Virtual] DEFAULT ((0)),
[ColName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[SelectExp] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ColAlias] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DataType] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SortOrder] [int] NULL,
[ExcludeColFromView] [bit] NULL CONSTRAINT [DF_PreIngestionTransfrom_ExcludeColFromView] DEFAULT ((0)),
[ColNameClean] AS (CONVERT([nvarchar](250),ltrim(rtrim(replace(replace([ColName],'[',''),']',''))),(0))) PERSISTED
) ON [PRIMARY]
GO
ALTER TABLE [flw].[PreIngestionTransfrom] ADD CONSTRAINT [PK_PreIngestionTransfrom] PRIMARY KEY CLUSTERED ([TransfromID]) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [NCI_ColNameClean] ON [flw].[PreIngestionTransfrom] ([ColNameClean]) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [NCI_FlowID] ON [flw].[PreIngestionTransfrom] ([FlowID], [FlowType], [ColName]) ON [PRIMARY]
GO
CREATE UNIQUE NONCLUSTERED INDEX [NCI_UniqueColumnName] ON [flw].[PreIngestionTransfrom] ([FlowID], [FlowType], [ColName]) ON [PRIMARY]
GO
EXEC sp_addextendedproperty N'MS_Description', 'The table contains columns related to the flow, flow type, and details about the transformation.

The table includes columns such as TransfromID, FlowID, FlowType, Virtual, ColName, SelectExp, ColAlias, SortOrder, and ExcludeColFromView. These columns suggest that the table is used to store information about the columns being transformed during pre-processing, such as the column name, select expression, and column alias.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionTransfrom', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionTransfrom].[ColAlias] column is a nullable string column in the [flw].[PreIngestionTransfrom] table in a SQL Server database. It stores the alias name for a transformed column. The alias is used as the new column name for the transformed data, instead of the original column name.

This column can be useful for creating more user-friendly column names or for avoiding naming conflicts in the final table.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionTransfrom', 'COLUMN', N'ColAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionTransfrom].[ColName] column is a non-null string column in the [flw].[PreIngestionTransfrom] table in a SQL Server database. It specifies the name of the column that the transformation is applied to.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionTransfrom', 'COLUMN', N'ColName'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionTransfrom].[ExcludeColFromView] column in the [flw].[PreIngestionTransfrom] table is a bit column used to indicate whether or not a transformed column should be excluded from the final view of the transformed data.

When set to 1, the column will be excluded from the final view of the transformed data. This is useful when a column is only used as an intermediate step during data transformation and is not needed in the final output.

By default, the value of the [flw].[PreIngestionTransfrom].[ExcludeColFromView] column is 0 (false), indicating that the column should be included in the final view of the transformed data.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionTransfrom', 'COLUMN', N'ExcludeColFromView'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionTransfrom].[FlowID] column is a non-null integer column in the [flw].[PreIngestionTransfrom] table in a SQL Server database. It is used to associate the column transformation information with a specific pre-ingestion flow or job, possibly by referencing the primary key of another table. This column does not have a default value specified, meaning that a value for this column must be provided explicitly when inserting data into the table.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionTransfrom', 'COLUMN', N'FlowID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionTransfrom].[FlowType] column is a non-null column in the [flw].[PreIngestionTransfrom] table in a SQL Server database. It specifies the type of data flow that the column transformation is associated with, which could be CSV, XML, or another format.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionTransfrom', 'COLUMN', N'FlowType'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionTransfrom].[SelectExp] column is a nullable string column in the [flw].[PreIngestionTransfrom] table in a SQL Server database. It stores the T-SQL SELECT expression used for transforming data. The SELECT expression may reference columns in the table being transformed as well as any user-defined functions or system functions.

When the data is transformed, the result of the SELECT expression will replace the original value in the specified column. This column is an important part of the column transformation process, as it defines the specific logic used to transform the data in the target column.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionTransfrom', 'COLUMN', N'SelectExp'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionTransfrom].[SortOrder] column is an integer column in the [flw].[PreIngestionTransfrom] table in a SQL Server database. It is used to specify the order in which the columns should be transformed when multiple columns are being transformed within the same data pipeline flow.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionTransfrom', 'COLUMN', N'SortOrder'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionTransfrom].[TransfromID] column is an integer identity column that serves as the primary key for the [flw].[PreIngestionTransfrom] table in a SQL Server database.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionTransfrom', 'COLUMN', N'TransfromID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionTransfrom].[Virtual] column is a nullable Boolean column in the [flw].[PreIngestionTransfrom] table in a SQL Server database. It indicates whether the column transformation is virtual or not.

A virtual column transformation is one that does not physically exist in the source data, but is instead calculated or derived from other columns. ', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionTransfrom', 'COLUMN', N'Virtual'
GO
