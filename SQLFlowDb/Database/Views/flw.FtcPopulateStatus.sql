SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE VIEW [flw].[FtcPopulateStatus]
as
SELECT
    FULLTEXTCATALOGPROPERTY(cat.name,'ItemCount') AS [ItemCount],
    FULLTEXTCATALOGPROPERTY(cat.name,'MergeStatus') AS [MergeStatus],
    FULLTEXTCATALOGPROPERTY(cat.name,'PopulateCompletionAge') AS [PopulateCompletionAge],
    FULLTEXTCATALOGPROPERTY(cat.name,'PopulateStatus') AS [PopulateStatus],
    FULLTEXTCATALOGPROPERTY(cat.name,'ImportStatus') AS [ImportStatus]
FROM sys.fulltext_catalogs AS cat
GO
