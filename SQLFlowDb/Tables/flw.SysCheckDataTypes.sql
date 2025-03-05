CREATE TABLE [flw].[SysCheckDataTypes]
(
[RecID] [int] NOT NULL IDENTITY(1, 1),
[TableSchema] [nvarchar] (128) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[TableName] [sys].[sysname] NOT NULL,
[ColumnName] [sys].[sysname] NULL,
[Ordinal] [int] NULL,
[DataType] [nvarchar] (128) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DataTypeExp] [nvarchar] (255) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[NewDataTypeExp] [nvarchar] (255) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[NewMaxDataTypeExp] [nvarchar] (255) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[MinValue] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[MaxValue] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[RandValue] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ValueWeight] [int] NULL,
[MinLength] [int] NOT NULL,
[MaxLength] [int] NOT NULL,
[SelectExp] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[CommaCount] [int] NULL,
[DotCount] [int] NULL,
[ColonCount] [int] NULL,
[IsDate] [bit] NULL,
[IsDateTime] [bit] NULL,
[IsTime] [bit] NULL,
[DateLocal] [int] NULL,
[ValAsDate] [datetime] NULL,
[IsNumeric] [bit] NULL,
[IsString] [bit] NULL CONSTRAINT [DF__DesribeTa__IsStr__12BEA5E7] DEFAULT ((0)),
[DecimalPoints] [int] NULL,
[cmdSQL] [nvarchar] (2484) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SQLFlowExp] [nvarchar] (2484) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[MaxSQLFlowExp] [nvarchar] (2484) COLLATE SQL_Latin1_General_CP1_CI_AS NULL
) ON [PRIMARY]
GO
ALTER TABLE [flw].[SysCheckDataTypes] ADD CONSTRAINT [PK_SysCheckDataTypes] PRIMARY KEY CLUSTERED ([RecID]) ON [PRIMARY]
GO
