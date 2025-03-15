CREATE TABLE [flw].[PreIngestionADOVirtual]
(
[VirtualID] [int] NOT NULL IDENTITY(1, 1),
[FlowID] [int] NOT NULL,
[ColumnName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DataType] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Length] [int] NULL,
[Precision] [int] NULL,
[Scale] [int] NULL,
[SelectExp] [nvarchar] (2048) COLLATE SQL_Latin1_General_CP1_CI_AS NULL
) ON [PRIMARY]
GO
ALTER TABLE [flw].[PreIngestionADOVirtual] ADD CONSTRAINT [PK_PreIngestionADOVirtual] PRIMARY KEY CLUSTERED ([VirtualID]) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [NCI_FlowID] ON [flw].[PreIngestionADOVirtual] ([FlowID]) ON [PRIMARY]
GO
EXEC sp_addextendedproperty N'MS_Description', 'This table hosts data for virtual columns that are injected dynamically into the data pipeline. Virtual columns can be anything that can be expressed in a select statement,  a transformation, a concatenation, or a calculation. This can be handy if the desired column is not in the source.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADOVirtual', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[PreIngestionADOVirtual].[ColumnName] is a column in the flw.PreIngestionADOVirtual table which stores the name of the virtual column being injected dynamically into the data pipeline. This column allows you to specify a custom name for the virtual column, which can be different from the name of the column in the source data.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADOVirtual', 'COLUMN', N'ColumnName'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[PreIngestionADOVirtual].[DataType] is a column in the PreIngestionADOVirtual table, which stores the data type of a virtual column defined in the pipeline. It is of type NVARCHAR(250) and can have values such as INT, DECIMAL, VARCHAR, etc. The data type should be specified according to the data type of the calculated expression or transformation that is used to create the virtual column.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADOVirtual', 'COLUMN', N'DataType'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionADOVirtual].[FlowID] column in the flw].[PreIngestionADOVirtual table stores the Flow ID value for the virtual column. This column is a foreign key that references the [flw].[PreIngestionADO].[FlowID] column in the [flw].[PreIngestionADO] table. It specifies the data pipeline to which the virtual column belongs.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADOVirtual', 'COLUMN', N'FlowID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionADOVirtual].[Length] column in the flw.PreIngestionADOVirtual table indicates the length of the data type for the corresponding column specified in the [ColumnName] column. For example, if the virtual column is created by concatenating two columns of type nvarchar(50) and nvarchar(100), the [Length] value would be 150. The value of this column can be used to determine the maximum length of the virtual column in the target table. If the virtual column is not created by concatenating multiple columns, the [Length] value will be the same as the corresponding column''s length.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADOVirtual', 'COLUMN', N'Length'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionADOVirtual].[Precision] column in the table is used to define the precision of the virtual column. Precision is the total number of digits in a number, both to the left and right of the decimal point. It is a required parameter when defining the data type of a virtual column. The column has a data type of INT and a default value of NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADOVirtual', 'COLUMN', N'Precision'
GO
EXEC sp_addextendedproperty N'MS_Description', 'This column stores the scale of the virtual column. Scale represents the number of digits to the right of the decimal point in a number. The scale is used with the precision parameter to specify the maximum number of decimal places for a column of numeric data type. If the data type is not numeric, the scale value is ignored.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADOVirtual', 'COLUMN', N'Scale'
GO
EXEC sp_addextendedproperty N'MS_Description', 'This column stores the SQL expression that defines the virtual column. This expression can be anything that can be expressed in a select statement, such as a transformation, concatenation or calculation. The virtual column will be generated dynamically during the pipeline process using this expression.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADOVirtual', 'COLUMN', N'SelectExp'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[PreIngestionADOVirtual].[VirtualID] is a primary key column of the table [flw].[PreIngestionADOVirtual]. It is an integer identity column with a starting value of 1 and an increment value of 1. Each record in this table will have a unique VirtualID value.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADOVirtual', 'COLUMN', N'VirtualID'
GO
