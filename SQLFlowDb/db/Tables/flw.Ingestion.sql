CREATE TABLE [flw].[Ingestion]
(
[FlowID] [int] NOT NULL CONSTRAINT [DF_Ingestion_FlowID] DEFAULT (NEXT VALUE FOR [flw].[FlowID]),
[Batch] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SysAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcServer] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[srcDBSchTbl] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[trgServer] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[trgDBSchTbl] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[trgDesiredIndex] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DeactivateFromBatch] [bit] NULL CONSTRAINT [DF_Ingestion_DeactivateFromBatch] DEFAULT ((0)),
[StreamData] [bit] NULL CONSTRAINT [DF_Bulkload_StreamData] DEFAULT ((1)),
[NoOfThreads] [int] NULL CONSTRAINT [DF_Ingestion_NoOfStreams] DEFAULT ((1)),
[KeyColumns] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[IncrementalColumns] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[IncrementalClauseExp] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DateColumn] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DataSetColumn] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[NoOfOverlapDays] [int] NULL CONSTRAINT [DF_Bulkload_NoOfOverlapDays] DEFAULT ((7)),
[FetchMinValuesFromSrc] [bit] NULL CONSTRAINT [DF_Ingestion_FetchMinValuesFromSrc] DEFAULT ((0)),
[SkipUpdateExsisting] [bit] NULL CONSTRAINT [DF_Ingestion_SkipUpdateExsisting] DEFAULT ((0)),
[SkipInsertNew] [bit] NULL CONSTRAINT [DF_Ingestion_SkipInsertNew] DEFAULT ((0)),
[FullLoad] [int] NULL CONSTRAINT [DF_Ingestion_FullLoad] DEFAULT ((0)),
[TruncateTrg] [bit] NULL CONSTRAINT [DF_Ingestion_TruncateTrg] DEFAULT ((0)),
[TruncatePreTableOnCompletion] [bit] NULL,
[srcFilter] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcFilterIsAppend] [bit] NULL CONSTRAINT [DF_Ingestion_AppendSrcFilter] DEFAULT ((1)),
[IdentityColumn] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[HashKeyColumns] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[HashKeyType] [nvarchar] (25) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[IgnoreColumns] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[IgnoreColumnsInHashkey] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SysColumns] [nvarchar] (500) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_Ingestion_SysColumns] DEFAULT (N'InsertedDate_DW,UpdatedDate_DW'),
[ColumnStoreIndexOnTrg] [bit] NULL CONSTRAINT [DF_Ingestion_ColumnStoreIndexOnTrg] DEFAULT ((0)),
[SyncSchema] [bit] NULL CONSTRAINT [DF_BulkLoad_SyncSchema] DEFAULT ((1)),
[OnErrorResume] [bit] NULL CONSTRAINT [DF_Ingestion_OnErrorResume] DEFAULT ((1)),
[OnSyncCleanColumnName] [bit] NULL CONSTRAINT [DF_BulkLoad_OnSyncCleanColumnName] DEFAULT ((0)),
[ReplaceInvalidCharsWith] [nvarchar] (2) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[OnSyncConvertUnicodeDataType] [bit] NULL CONSTRAINT [DF_BulkLoad_OnSyncConvertUnicodeDataType] DEFAULT ((0)),
[CleanColumnNameSQLRegExp] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[trgVersioning] [bit] NULL CONSTRAINT [DF_Ingestion_trgVersioning] DEFAULT ((0)),
[InsertUnknownDimRow] [bit] NULL CONSTRAINT [DF_Ingestion_InsertUknownDimElement] DEFAULT ((0)),
[TokenVersioning] [bit] NULL CONSTRAINT [DF_Ingestion_TokenVersioning] DEFAULT ((0)),
[TokenRetentionDays] [int] NULL,
[PreProcessOnTrg] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PostProcessOnTrg] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PreInvokeAlias] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PostInvokeAlias] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Assertions] [nvarchar] (1000) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_Ingestion_Assertions] DEFAULT (N'CheckEmptyTable,CheckFreshnessDaily'),
[MatchKeysInSrcTrg] [bit] NULL CONSTRAINT [DF_Ingestion_MatchKey] DEFAULT ((0)),
[UseBatchUpsertToAvoideLockEscalation] [bit] NULL,
[BatchUpsertRowCount] [int] NULL CONSTRAINT [DF_Ingestion_BatchUpsertRowCount] DEFAULT ((2000)),
[InitLoad] [bit] NULL CONSTRAINT [DF_Ingestion_InitLoad] DEFAULT ((0)),
[InitLoadFromDate] [date] NULL,
[InitLoadToDate] [date] NULL,
[InitLoadBatchBy] [varchar] (1) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[InitLoadBatchSize] [int] NULL,
[InitLoadKeyColumn] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[InitLoadKeyMaxValue] [int] NULL,
[BatchOrderBy] [int] NULL,
[FlowType] [varchar] (25) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_Ingestion_FlowType] DEFAULT ('ing'),
[Description] [nvarchar] (2048) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FromObjectMK] [int] NULL,
[ToObjectMK] [int] NULL,
[CreatedBy] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_Ingestion_CreatedBy] DEFAULT (suser_sname()),
[CreatedDate] [datetime] NULL CONSTRAINT [DF_Ingestion_CreatedDate] DEFAULT (getdate())
) ON [PRIMARY]
GO
SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE TRIGGER [flw].[AddFlowToLogTable] ON [flw].[Ingestion]
FOR INSERT
AS

INSERT INTO  flw.SysLog (FlowID,[FlowType], [Process])
    SELECT FlowId ,FlowType ,  [srcServer] + '.' + [srcDBSchTbl] + '-->' + [trgServer] + '.' + [trgDBSchTbl] as [Process]
        FROM inserted
GO
ALTER TABLE [flw].[Ingestion] ADD CONSTRAINT [Chk_srcDBSchTbl_ING] CHECK ((parsename([srcDBSchTbl],(3)) IS NOT NULL))
GO
ALTER TABLE [flw].[Ingestion] ADD CONSTRAINT [Chk_trgDBSchTbl_ING] CHECK ((parsename([trgDBSchTbl],(3)) IS NOT NULL))
GO
ALTER TABLE [flw].[Ingestion] ADD CONSTRAINT [PK_Ingestion] PRIMARY KEY CLUSTERED ([FlowID]) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [NCI_FlowID] ON [flw].[Ingestion] ([FlowID]) INCLUDE ([Batch], [SysAlias], [DeactivateFromBatch]) ON [PRIMARY]
GO
EXEC sp_addextendedproperty N'MS_Description', 'The ingestion table is the starting point and an integral part of SQLFlow where all the metadata is populated. All the inputs and settings in this table define the execution of the ETL process.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', 'Batch is an arbitrary identifier that groups flow executions. Execution of a flow can be grouped by Batch and SysAlias in conjunction or independently. Defining Batch is mandatory in all data flow tables. Valid Batch values can be registered in [flw].[SysBatch] for convenience, but this doesn''t enforce referential integrity.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'Batch'
GO
EXEC sp_addextendedproperty N'MS_Description', 'BatchOrderBy column stores the order in which data flows should be executed within a Batch. This is useful when there are dependencies between different data flows in the same Batch. Feature is currently under development. ', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'BatchOrderBy'
GO
EXEC sp_addextendedproperty N'MS_Description', 'CleanColumnNameSQLRegExp column stores a regular expression pattern used to clean column names during the ingestion process. This can be useful for ensuring that column names adhere to a specific naming convention or removing unwanted characters. This column does not have a default value specified. This value overrides the default expression defined in flw.cfg.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'CleanColumnNameSQLRegExp'
GO
EXEC sp_addextendedproperty N'MS_Description', 'ColumnStoreIndexOnTrg column stores a Boolean value indicating whether a column store index should be created on the target table during the initial execution. Column store indexes can improve query performance for large data sets. Please note that update process for existing rows can be significantly slower on a table with column store index. ', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'ColumnStoreIndexOnTrg'
GO
EXEC sp_addextendedproperty N'MS_Description', 'CreatedBy column stores the name of the user who created the data ingestion flow. This can be useful for tracking and auditing the creation of data ingestion flows. The value is automatically fetched from the session information between the client and SQL server. The default value for this column is the current user''s name, obtained through SUSER_SNAME().', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'CreatedBy'
GO
EXEC sp_addextendedproperty N'MS_Description', 'CreatedDate column stores the date and time when the data ingestion flow was created. The default value for this column is the current date and time, obtained through GETDATE().', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'CreatedDate'
GO
EXEC sp_addextendedproperty N'MS_Description', 'DataSetColumn stores the name of the column used to clustered the source data and processed it in ascending order. This feature is handy if you have multiple files receding in the staging table and each file must be processed independently and in correct order.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'DataSetColumn'
GO
EXEC sp_addextendedproperty N'MS_Description', 'DateColumn column stores column name of the date column which can be utilized to build an overlapping incremental dataset. The column is utilized in conjunction with NoOfOverlapDays.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'DateColumn'
GO
EXEC sp_addextendedproperty N'MS_Description', 'DeactivateFromBatch column stores a Boolean value indicating whether the data ingestion flow should be deactivated from the batch execution. This feature can be utilized to exclude a data pipeline from the scheduled execution, for example, when it is no longer needed or when it requires maintenance. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'DeactivateFromBatch'
GO
EXEC sp_addextendedproperty N'MS_Description', 'Description column stores a brief description of the ingestion flow. This information can be used to provide context for understanding the purpose and functionality of the flow. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'Description'
GO
EXEC sp_addextendedproperty N'MS_Description', 'FetchMinValuesFromSrc column stores a Boolean value indicating whether to fetch the minimum values from the source for incremental data ingestion. This flag can be temporarily activated to reprocessed existing data. 
[flw].[Ingestion].[SkipUpdateExsisting]: SkipUpdateExsisting column stores a Boolean value indicating whether existing records in the target table should be skipped during the update process. This can be useful in situations where only new records need to be inserted. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'FetchMinValuesFromSrc'
GO
EXEC sp_addextendedproperty N'MS_Description', 'FlowID is a system-generated unique numeric identifier for each data pipeline. FlowID can be used to track and audit the execution of data pipeline flows. See the search command for details.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'FlowID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'FlowType is a system-generated value. Ingestion flows are abbreviated as "ing". FlowType is used to route execution to the correct backend logic. The default value for this column is ''ing''.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'FlowType'
GO
EXEC sp_addextendedproperty N'MS_Description', 'FromObjectMK column stores the unique identifier for the source object being ingested. A unique Master Key (MK) is generated for each SQLFlow Object. The master key is essential in calculating lineage dependencies and the correct execution order for each batch. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'FromObjectMK'
GO
EXEC sp_addextendedproperty N'MS_Description', 'FullLoad column stores a Boolean value indicating whether a full data load should be performed instead of an incremental load. The default value for this column is 0 (False). This flag can turn an incremental processing to full processing.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'FullLoad'
GO
EXEC sp_addextendedproperty N'MS_Description', 'HashKeyColumns stores a comma-separated list of column names that are used to generate a hash key for each row during the ingestion process. This hash key can be used to identify and compare changes in the source data. This feature can be hand if there exists no business key on the source data set. Arbitary columns in combination with HashKey can be used to detected changes and new rows. HashKey value is stored as fixed length binary value.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'HashKeyColumns'
GO
EXEC sp_addextendedproperty N'MS_Description', 'HashKeyType column stores the type of hash algorithm to be used for generating hash keys for each row during the ingestion process. This can be used to identify and compare changes in the source data. SHA2_512 is used as the hashing algorithm.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'HashKeyType'
GO
EXEC sp_addextendedproperty N'MS_Description', 'IdentityColumn column stores the name of the column that should be injected and populated in the target table. This column is often used as clustering key in archive tables. In most cases this approach yield the optimal index performance.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'IdentityColumn'
GO
EXEC sp_addextendedproperty N'MS_Description', 'IgnoreColumns column stores a list of column names to be excluded from the ingestion process. This can be useful when specific columns are not required in the target table.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'IgnoreColumns'
GO
EXEC sp_addextendedproperty N'MS_Description', 'IncrementalClauseExp column stores an optional hard coded filter expression to be applied on the source data during ingestion. This can be used to ingest only a specific subset of data based on a condition. If a target table receives data from multiple sources, then hard a coded filters can be utilized to processes each source independently. Example AND SystemID = 13', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'IncrementalClauseExp'
GO
EXEC sp_addextendedproperty N'MS_Description', 'IncrementalColumns stores the column names used for correctly fetching changed data since the last execution.  Max value is calculated from the target table to facilitate an automated incremental logic. If [FetchMinValuesFromSrc] is set to true, then Minimum value is calculate from the source and utilized to reprocess the whole source dataset.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'IncrementalColumns'
GO
EXEC sp_addextendedproperty N'MS_Description', 'InsertUnknownDimRow column stores a Boolean value indicating whether an unknown dimension row should be inserted into the target table during the ingestion process. This can be useful for handling situations where dimension data is not available for a specific fact record. This feature supplements dynamically creating dimensional datasets.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'InsertUnknownDimRow'
GO
EXEC sp_addextendedproperty N'MS_Description', 'KeyColumns column stores the list of key columns that uniquely identify each row in the source and target tables. Business Key columns are essential for identifying new rows and updating existing.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'KeyColumns'
GO
EXEC sp_addextendedproperty N'MS_Description', 'NoOfOverlapDays stores the number of days to overlap when incremental data is ingested. Date offset is calculated based on present values within the provided IncrementalColumns. This ensures that all relevant data is included in the ingestion. The default value for this column is 1.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'NoOfOverlapDays'
GO
EXEC sp_addextendedproperty N'MS_Description', 'NoOfThreads defines the number of threads that should be used during the ingestion process. Only relevant when StreamData is set to false. The default value for this column is 0.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'NoOfThreads'
GO
EXEC sp_addextendedproperty N'MS_Description', 'OnErrorResume column stores a Boolean value indicating whether the next data pipeline should run if the current process fails. This feature is utilized in Batch execution. The default value for this column is 1 (True).', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'OnErrorResume'
GO
EXEC sp_addextendedproperty N'MS_Description', 'OnSyncCleanColumnName column stores a Boolean value indicating whether to clean the column names in the target table during synchronization. This value ensures that the target table column names are consistent and properly formatted. The default value for this column is 0 (False). This feature was specifically designed for SAP tables. The cleanup process utilizes a regular expression registered in [flw].[SysCFG] (ColCleanupSQLRegExp)', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'OnSyncCleanColumnName'
GO
EXEC sp_addextendedproperty N'MS_Description', 'OnSyncConvertUnicodeDataType column stores a Boolean value indicating whether to convert Unicode data types in the source table to non-Unicode data types in the target table during synchronization. This value ensures that the target table data types are optimized for storage and processing. The default value for this column is 0 (False). This feature was spesfically designed for SAP Open Hub which utilizes NVARCHAR for all character based columns.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'OnSyncConvertUnicodeDataType'
GO
EXEC sp_addextendedproperty N'MS_Description', 'PostInvokeAlias column stores an alias for a pre-registered Azure Data Factory or Automation Runbook to be executed after the ingestion process has completed successfully. This feature can be used for tasks such as data validation or further processing of the ingested data. The desired invoke must be pre-registered in the table [flw].[Invoke] and referenced by its Alias. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'PostInvokeAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', 'PostProcessOnTrg column refers to any post-processing tasks that should be executed on the target table after the ingestion process is completed. These tasks can include data validation, cleaning, transformation, or any other actions needed to finalize the data in the target table. This column does not have a default value specified. ', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'PostProcessOnTrg'
GO
EXEC sp_addextendedproperty N'MS_Description', 'PreInvokeAlias column stores an alias for a pre-registered Azure Data Factory or Automation Runbook to be executed before the ingestion process begins. This feature can be used for tasks such as data validation or preparing the source data. The desired invoke must be pre-registered in the table [flw].[Invoke] and referenced by its Alias. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'PreInvokeAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', 'PreProcessOnTrg column refers to any preprocessing tasks that should be executed on the target table before the ingestion process begins. These tasks can include data validation, cleaning, or preparing the target table for new data. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'PreProcessOnTrg'
GO
EXEC sp_addextendedproperty N'MS_Description', 'SkipInsertNew column stores a Boolean value indicating whether new records should be skipped during the insertion process. This can be useful in situations where only updates to existing records are needed. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'SkipInsertNew'
GO
EXEC sp_addextendedproperty N'MS_Description', 'SkipUpdateExsisting column stores a Boolean value indicating whether to skip updating existing records in the target table during the data ingestion process. If set to True, only new records will be inserted, and existing records will not be updated. This can help improve the performance of the ingestion process in scenarios where updating existing records is not necessary. The default value for this column is not specified.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'SkipUpdateExsisting'
GO
EXEC sp_addextendedproperty N'MS_Description', 'srcDBSchTbl column stores the name of the source database, schema, and table/view from where the data is fetched. Please ensure that provided value follows the recommended format: [Database].[Schema].[ObjectName]', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'srcDBSchTbl'
GO
EXEC sp_addextendedproperty N'MS_Description', 'srcFilterIsAppend column stores a Boolean value indicating whether the IncrementalClauseExp expression should  be treated as replacement or addition to the calculated filters. This can be used to combine multiple filters for more complex filtering scenarios. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'srcFilterIsAppend'
GO
EXEC sp_addextendedproperty N'MS_Description', 'srcServer column refers to the Alias column from the table [flw].[SysDataSource]. Alias is an arbitrary acronym referencing the source database for the current pipeline.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'srcServer'
GO
EXEC sp_addextendedproperty N'MS_Description', 'StreamData column stores a Boolean value indicating whether the data should be streamed during ingestion. Streaming data can optimize processing times and resource consumption. The default value for this column is 1 (True). When false, the data is first read into memory and then streamed to the target table in a multi-threaded operation. Streaming is recommended for tables that cannot be accommodated in available ram.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'StreamData'
GO
EXEC sp_addextendedproperty N'MS_Description', 'SyncSchema column stores a Boolean value indicating whether the schema of the target table should be automatically synchronized with the source table. This value ensures that schema changes in the source table are propagated to the target table. The default value for this column is 1 (True).', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'SyncSchema'
GO
EXEC sp_addextendedproperty N'MS_Description', 'SysAlias is an arbitrary alias that identifies the source system from which the data was initially fetched. Execution of data pipeline can be grouped by Batch and SysAlias in conjunction or independently. Defining SysAlias is mandatory in all data flow tables. Valid SysAlias values can be registered in [flw].[SysAlias] for convenience, but this doesn''t enforce referential integrity.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'SysAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', 'SysColumns column stores a list of System specific column names that should be ingested and populated into the pipline. Valid options are hosted by the table [flw].[SysColumn]. Valid options are InsertedDate_DW, UpdatedDate_DW, DeletedDate_DW, RowStatus_DW', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'SysColumns'
GO
EXEC sp_addextendedproperty N'MS_Description', 'TokenRetentionDays column stores the number of days to retain historical tokens for the target table. This value determines how long historical versions of the target table are available. The default value for this column is 0, indicating that tokens are retained indefinitely. This feature is under development.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'TokenRetentionDays'
GO
EXEC sp_addextendedproperty N'MS_Description', 'TokenVersioning column stores a Boolean value indicating whether to enable token-based encryption of the target table. This feature is under development. The default value for this column is 0 (False).', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'TokenVersioning'
GO
EXEC sp_addextendedproperty N'MS_Description', 'ToObjectMK column stores the unique identifier for the target object where the data is being ingested. A unique Master Key (MK) is generated for each SQLFlow Object. The master key is essential in calculating lineage dependencies and the correct execution order for each batch. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'ToObjectMK'
GO
EXEC sp_addextendedproperty N'MS_Description', 'trgDBSchTbl column stores the name of the target database, schema, and table where the data is ingested. Please ensure that provided value follows the recommended format: [Database].[Schema].[ObjectName]', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'trgDBSchTbl'
GO
EXEC sp_addextendedproperty N'MS_Description', 'trgServer column refers to the Alias column from the table [flw].[SysDataSource]. Alias is an arbitrary acronym referencing the source database for the current pipeline.', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'trgServer'
GO
EXEC sp_addextendedproperty N'MS_Description', 'trgVersioning column stores a Boolean value indicating whether to enable versioning for the target table. This value ensures that historical versions of the target table are retained. The default value for this column is 0 (False). The history table is established during the first execution utilizing temporal tables. Please note that versioning cannot be enabled a preexisting data flow. ', 'SCHEMA', N'flw', 'TABLE', N'Ingestion', 'COLUMN', N'trgVersioning'
GO
