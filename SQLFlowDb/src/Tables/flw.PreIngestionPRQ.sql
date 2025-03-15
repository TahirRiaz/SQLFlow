CREATE TABLE [flw].[PreIngestionPRQ]
(
[FlowID] [int] NOT NULL CONSTRAINT [DF_PreIngestionPRQ_FlowID] DEFAULT (NEXT VALUE FOR [flw].[FlowID]),
[Batch] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SysAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ServicePrincipalAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcPath] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcPathMask] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcFile] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SearchSubDirectories] [bit] NULL CONSTRAINT [DF_PreIngestionPRQ_earchSubDirectories] DEFAULT ((0)),
[copyToPath] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcDeleteIngested] [bit] NULL CONSTRAINT [DF_PreIngestionPRQ_srcDeleteIngested] DEFAULT ((0)),
[srcDeleteAtPath] [bit] NULL CONSTRAINT [DF_PreIngestionPRQ_srcDelete] DEFAULT ((0)),
[zipToPath] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[trgServer] [nvarchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[trgDBSchTbl] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[trgDesiredIndex] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[preFilter] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PartitionList] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SyncSchema] [bit] NULL CONSTRAINT [DF_PreIngestionPRQ_SyncSchema] DEFAULT ((1)),
[ExpectedColumnCount] [int] NULL CONSTRAINT [DF_PreIngestionPRQ_ExpectedColumnCount] DEFAULT ((0)),
[FetchDataTypes] [bit] NULL CONSTRAINT [DF_PreIngestionPRQ_FetchDataTypes] DEFAULT ((0)),
[DefaultColDataType] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[OnErrorResume] [bit] NULL CONSTRAINT [DF_PreIngestionPRQ_OnErrorResume] DEFAULT ((1)),
[NoOfThreads] [int] NULL CONSTRAINT [DF_PreIngestionPRQ_Parallelize] DEFAULT ((1)),
[PreProcessOnTrg] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PostProcessOnTrg] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[InitFromFileDate] [varchar] (15) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[InitToFileDate] [varchar] (15) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PreInvokeAlias] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[BatchOrderBy] [int] NULL,
[DeactivateFromBatch] [bit] NULL CONSTRAINT [DF_PreIngestionPRQ_DeactivateFromBatch] DEFAULT ((0)),
[EnableEventExecution] [bit] NULL CONSTRAINT [DF_PreIngestionPRQ_EnableEventExecution] DEFAULT ((0)),
[FlowType] [varchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL CONSTRAINT [DF_PreIngestionPRQ_FlowType] DEFAULT (N'prq'),
[FromObjectMK] [int] NULL,
[ToObjectMK] [int] NULL,
[ShowPathWithFileName] [bit] NULL CONSTRAINT [DF_PreIngestionPRQ_ShowPathWithFileName] DEFAULT ((0)),
[CreatedBy] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_PreIngestionPRQ_CreatedBy] DEFAULT (suser_sname()),
[CreatedDate] [datetime] NULL CONSTRAINT [DF_PreIngestionPRQ_CreatedDate] DEFAULT (getdate())
) ON [PRIMARY]
GO
SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE TRIGGER [flw].[AddPreFlowToLogTablePRQ] ON [flw].[PreIngestionPRQ]
FOR INSERT, UPDATE

AS
BEGIN
	-- Handling INSERT operations
    IF (SELECT COUNT(*) FROM inserted) > 0 AND (SELECT COUNT(*) FROM deleted) = 0
    BEGIN
        INSERT INTO [flw].[SysLog] ([FlowID], [FlowType], [Process], [ProcessShort])
                SELECT FlowID, FlowType,   'prq -->' + [trgServer] + '.' + [trgDBSchTbl] as [Process], 'prq -->' + [trgServer] + '.' + [trgDBSchTbl] as [ProcessShort]
        FROM inserted
    END

    -- Handling UPDATE operations
    IF (SELECT COUNT(*) FROM inserted) > 0 AND (SELECT COUNT(*) FROM deleted) > 0
    BEGIN
        UPDATE [flw].[SysLog]
        SET [FlowType] = i.[FlowType],
            [Process] = 'prq -->' + i.[trgServer] + '.' + i.[trgDBSchTbl],
			[ProcessShort] = 'prq -->' + i.[trgServer] + '.' + i.[trgDBSchTbl]
        FROM inserted i
        INNER JOIN [flw].[SysLog] sl ON sl.[FlowID] = i.[FlowID]; 
    END
END
GO
ALTER TABLE [flw].[PreIngestionPRQ] ADD CONSTRAINT [Chk_trgDBSchTbl_PRQ] CHECK ((parsename([trgDBSchTbl],(3)) IS NOT NULL))
GO
ALTER TABLE [flw].[PreIngestionPRQ] ADD CONSTRAINT [PK_PreIngestionPRQ] PRIMARY KEY CLUSTERED ([FlowID]) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [NCI_FlowID] ON [flw].[PreIngestionPRQ] ([FlowID]) INCLUDE ([Batch], [SysAlias], [DeactivateFromBatch]) ON [PRIMARY]
GO
