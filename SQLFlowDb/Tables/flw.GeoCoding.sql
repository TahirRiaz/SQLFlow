CREATE TABLE [flw].[GeoCoding]
(
[GeoCodingID] [int] NOT NULL IDENTITY(1, 1),
[FlowID] [int] NOT NULL,
[GoogleAPIKey] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[KeyColumn] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[LonColumn] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[LatColumn] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[AddressColumn] [nvarchar] (500) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[trgDBSchTbl] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL
)
GO
ALTER TABLE [flw].[GeoCoding] ADD CONSTRAINT [PK_GeoCoding] PRIMARY KEY CLUSTERED ([GeoCodingID])
GO
