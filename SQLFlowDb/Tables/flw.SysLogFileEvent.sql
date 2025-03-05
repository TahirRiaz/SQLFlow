CREATE TABLE [flw].[SysLogFileEvent]
(
[LogFileEventID] [int] NOT NULL IDENTITY(1, 1),
[FlowID] [int] NULL,
[FileName_DW] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[EventDate_DW] [datetime] NULL
)
WITH
(
DATA_COMPRESSION = PAGE
)
GO
ALTER TABLE [flw].[SysLogFileEvent] ADD CONSTRAINT [PK_SysLogFileEvent] PRIMARY KEY CLUSTERED ([LogFileEventID]) WITH (DATA_COMPRESSION = PAGE)
GO
