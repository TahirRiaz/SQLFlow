CREATE TABLE [flw].[IngestionVirtual]
(
[VirtualID] [int] NOT NULL IDENTITY(1, 1),
[FlowID] [int] NOT NULL,
[ColumnName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DataType] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DataTypeExp] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SelectExp] [nvarchar] (2048) COLLATE SQL_Latin1_General_CP1_CI_AS NULL
) ON [PRIMARY]
GO
ALTER TABLE [flw].[IngestionVirtual] ADD CONSTRAINT [PK_IngestionVirtual] PRIMARY KEY CLUSTERED ([VirtualID]) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [NCI_FlowID] ON [flw].[IngestionVirtual] ([FlowID]) ON [PRIMARY]
GO
EXEC sp_addextendedproperty N'MS_Description', 'This table hosts data for virtual columns that are injected dynamically into the data pipeline. Virtual columns can be anything that can be expressed in a select statement,  a transformation, a concatenation, or a calculation. This can be handy if the desired column is not in the source.', 'SCHEMA', N'flw', 'TABLE', N'IngestionVirtual', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[IngestionVirtual].[ColumnName] column in the [flw].[IngestionVirtual] table hosts the name of the virtual column to be injected into the data pipeline. This column is nullable, meaning that it may not always be necessary to define a column name when creating a virtual column.', 'SCHEMA', N'flw', 'TABLE', N'IngestionVirtual', 'COLUMN', N'ColumnName'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[IngestionVirtual].[DataType] column in the SQLFlow table [flw].[IngestionVirtual] holds the data type of the virtual column specified in the [flw].[IngestionVirtual].[ColumnName] column. It specifies the data type of the data to be inserted into the virtual column. This column provides information about the data type of the virtual column, such as integer, float, date, and string.', 'SCHEMA', N'flw', 'TABLE', N'IngestionVirtual', 'COLUMN', N'DataType'
GO
EXEC sp_addextendedproperty N'MS_Description', 'Based on the table definition, column [flw].[IngestionVirtual].[DataTypeExp] is used to store an expression that defines the data type of the virtual column. The expression can be any valid T-SQL expression that returns a data type, such as "CAST(''2022-01-01'' AS DATE)". This allows for dynamic data type assignment for virtual columns.', 'SCHEMA', N'flw', 'TABLE', N'IngestionVirtual', 'COLUMN', N'DataTypeExp'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The FlowID column in the flw.IngestionVirtual table stores the unique identifier of the data flow to which the virtual column belongs. It is a foreign key column that references the FlowID column in the flw.Ingestion table.', 'SCHEMA', N'flw', 'TABLE', N'IngestionVirtual', 'COLUMN', N'FlowID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[IngestionVirtual].[SelectExp] column is used to specify the T-SQL expression for generating virtual columns. It can be any valid T-SQL select statement that generates the desired output for the virtual column.', 'SCHEMA', N'flw', 'TABLE', N'IngestionVirtual', 'COLUMN', N'SelectExp'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The VirtualID column in the [flw].[IngestionVirtual] table is an INT column that serves as the primary key for the table. It is an identity column that automatically increments its value for each new row inserted into the table.', 'SCHEMA', N'flw', 'TABLE', N'IngestionVirtual', 'COLUMN', N'VirtualID'
GO
