CREATE TABLE [flw].[PreIngestionPRC]
(
[FlowID] [int] NOT NULL CONSTRAINT [DF_PreIngestionPRC_FlowID] DEFAULT (NEXT VALUE FOR [flw].[FlowID]),
[Batch] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SysAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ServicePrincipalAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcServer] [nvarchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[srcPath] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcFile] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcCode] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[trgServer] [nvarchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[trgDBSchTbl] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[trgDesiredIndex] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[preFilter] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SyncSchema] [bit] NULL CONSTRAINT [DF_PreIngestionPRC_SyncSchema] DEFAULT ((1)),
[ExpectedColumnCount] [int] NULL CONSTRAINT [DF_PreIngestionPRC_ExpectedColumnCount] DEFAULT ((0)),
[FetchDataTypes] [bit] NULL CONSTRAINT [DF_PreIngestionPRC_FetchDataTypes] DEFAULT ((0)),
[OnErrorResume] [bit] NULL CONSTRAINT [DF_PreIngestionPRC_OnErrorResume] DEFAULT ((1)),
[NoOfThreads] [int] NULL CONSTRAINT [DF_PreIngestionPRC_Parallelize] DEFAULT ((1)),
[PreProcessOnTrg] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PostProcessOnTrg] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PreInvokeAlias] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[BatchOrderBy] [int] NULL,
[DeactivateFromBatch] [bit] NULL CONSTRAINT [DF_PreIngestionPRC_DeactivateFromBatch] DEFAULT ((0)),
[FlowType] [varchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL CONSTRAINT [DF_PreIngestionPRC_FlowType] DEFAULT (N'prc'),
[FromObjectMK] [int] NULL,
[ToObjectMK] [int] NULL,
[ShowPathWithFileName] [bit] NULL CONSTRAINT [DF_PreIngestionPRC_ShowPathWithFileName] DEFAULT ((0)),
[CreatedBy] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_PreIngestionPRC_CreatedBy] DEFAULT (suser_sname()),
[CreatedDate] [datetime] NULL CONSTRAINT [DF_PreIngestionPRC_CreatedDate] DEFAULT (getdate())
) ON [PRIMARY]
GO
SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE TRIGGER [flw].[AddPreFlowToLogTablePRC] ON [flw].[PreIngestionPRC]
FOR INSERT, UPDATE

AS
BEGIN
	-- Handling INSERT operations
    IF (SELECT COUNT(*) FROM inserted) > 0 AND (SELECT COUNT(*) FROM deleted) = 0
    BEGIN
        INSERT INTO [flw].[SysLog] ([FlowID], [FlowType], [Process], [ProcessShort])
                SELECT FlowID, FlowType,   'prc -->' + [trgServer] + '.' + [trgDBSchTbl] as [Process], 'prc -->' + [trgServer] + '.' + [trgDBSchTbl] as [ProcessShort]
        FROM inserted
    END

    -- Handling UPDATE operations
    IF (SELECT COUNT(*) FROM inserted) > 0 AND (SELECT COUNT(*) FROM deleted) > 0
    BEGIN
        UPDATE [flw].[SysLog]
        SET [FlowType] = i.[FlowType],
            [Process] = 'prc -->' + i.[trgServer] + '.' + i.[trgDBSchTbl],
			[ProcessShort] = 'prc -->' + i.[trgServer] + '.' + i.[trgDBSchTbl]
        FROM inserted i
        INNER JOIN [flw].[SysLog] sl ON sl.[FlowID] = i.[FlowID]; 
    END
END
GO
ALTER TABLE [flw].[PreIngestionPRC] ADD CONSTRAINT [Chk_trgDBSchTbl_PRC] CHECK ((parsename([trgDBSchTbl],(3)) IS NOT NULL))
GO
ALTER TABLE [flw].[PreIngestionPRC] ADD CONSTRAINT [PK_PreIngestionPRC] PRIMARY KEY CLUSTERED ([FlowID]) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [NCI_FlowID] ON [flw].[PreIngestionPRC] ([FlowID]) INCLUDE ([Batch], [SysAlias], [DeactivateFromBatch]) ON [PRIMARY]
GO
