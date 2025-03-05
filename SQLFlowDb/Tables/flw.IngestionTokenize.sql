CREATE TABLE [flw].[IngestionTokenize]
(
[TokenID] [int] NOT NULL IDENTITY(1, 1),
[FlowID] [int] NOT NULL,
[ColumnName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[TokenExpAlias] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL
)
GO
ALTER TABLE [flw].[IngestionTokenize] ADD CONSTRAINT [PK_IngestionTokens] PRIMARY KEY CLUSTERED ([TokenID])
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[IngestionTokenize] is utilized to define tokenization on flows registered in flw.Ingestion. A tokenization process kicks off after the ingestion process has completed successfully ', 'SCHEMA', N'flw', 'TABLE', N'IngestionTokenize', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[IngestionTokenize].[ColumnName] column is a nullable NVARCHAR(250) column in the [flw].[IngestionTokenize] SQLFlow table. It stores the name of the column in the target table that should be tokenized. The tokenization process kicks off after the successful ingestion process completes.', 'SCHEMA', N'flw', 'TABLE', N'IngestionTokenize', 'COLUMN', N'ColumnName'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[IngestionTokenize].[FlowID] column stores the identifier of the data flow registered in [flw].[Ingestion] table. This column is used to map the tokenization process to the correct data flow. ', 'SCHEMA', N'flw', 'TABLE', N'IngestionTokenize', 'COLUMN', N'FlowID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The TokenExpAlias column in the [flw].[IngestionTokenize] table is a string column that specifies the alias of the tokenization expression defined in the [flw].[IngestionTokenExp] table. This expression will be used to tokenize the data in the specified column after the ingestion process has completed successfully. The value in this column must match a valid tokenization expression alias in the [flw].[IngestionTokenExp] table for the tokenization process to be executed properly.', 'SCHEMA', N'flw', 'TABLE', N'IngestionTokenize', 'COLUMN', N'TokenExpAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[IngestionTokenize].[TokenID] is an auto-incrementing integer column that serves as the primary key for the table. Each row in the table is assigned a unique value in this column. The values in this column are automatically generated when a new row is added to the table, starting with 1 and incrementing by 1 for each subsequent row. This column cannot contain null values.', 'SCHEMA', N'flw', 'TABLE', N'IngestionTokenize', 'COLUMN', N'TokenID'
GO
