CREATE TABLE [flw].[IngestionTransfrom]
(
[TransfromID] [int] NOT NULL IDENTITY(1, 1),
[FlowID] [int] NULL,
[ColumnName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DataTypeExp] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SelectExp] [nvarchar] (2048) COLLATE SQL_Latin1_General_CP1_CI_AS NULL
) ON [PRIMARY]
GO
ALTER TABLE [flw].[IngestionTransfrom] ADD CONSTRAINT [PK_IngestionTransfrom] PRIMARY KEY CLUSTERED ([TransfromID]) ON [PRIMARY]
GO
