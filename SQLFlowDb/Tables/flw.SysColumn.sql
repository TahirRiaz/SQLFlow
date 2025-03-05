CREATE TABLE [flw].[SysColumn]
(
[SysColumnID] [int] NOT NULL IDENTITY(1, 1),
[ColumnName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DataType] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DataTypeExp] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SelectExp] [nvarchar] (2048) COLLATE SQL_Latin1_General_CP1_CI_AS NULL
)
GO
ALTER TABLE [flw].[SysColumn] ADD CONSTRAINT [PK_SysColumnID] PRIMARY KEY CLUSTERED ([SysColumnID])
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysColumn] is a table that hosts column names that receive special treatment within the SQLFlow framework.', 'SCHEMA', N'flw', 'TABLE', N'SysColumn', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysColumn].[ColumnName] column stores the names of the columns that receive special treatment with the SQLFlow framework. These columns may have specific data types, data type expressions or select expressions defined in the other columns of this table.', 'SCHEMA', N'flw', 'TABLE', N'SysColumn', 'COLUMN', N'ColumnName'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysColumn].[DataType] column stores the data type of the column specified in the [flw].[SysColumn].[ColumnName] column. This information is used to determine how to handle the column during data processing and manipulation within the SQLFlow framework.', 'SCHEMA', N'flw', 'TABLE', N'SysColumn', 'COLUMN', N'DataType'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysColumn].[DataTypeExp] is the full T-SQL expression for the column data type. ', 'SCHEMA', N'flw', 'TABLE', N'SysColumn', 'COLUMN', N'DataTypeExp'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysColumn].[SelectExp] is a column in the [flw].[SysColumn] table that contains the SQL select statement expresion used to populate the data in the system columns.', 'SCHEMA', N'flw', 'TABLE', N'SysColumn', 'COLUMN', N'SelectExp'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysColumn].[SysColumnID] column is an auto-incremented identity column used as the primary key for the SysColumn table.', 'SCHEMA', N'flw', 'TABLE', N'SysColumn', 'COLUMN', N'SysColumnID'
GO
