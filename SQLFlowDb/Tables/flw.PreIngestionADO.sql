CREATE TABLE [flw].[PreIngestionADO]
(
[FlowID] [int] NOT NULL CONSTRAINT [DF_PreIngestionADO_FlowID] DEFAULT (NEXT VALUE FOR [flw].[FlowID]),
[Batch] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SysAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[srcServer] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[srcDatabase] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[srcSchema] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcObject] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[trgServer] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[trgDBSchTbl] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[trgDesiredIndex] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[BatchOrderBy] [int] NULL,
[DeactivateFromBatch] [bit] NULL CONSTRAINT [DF_PreIngestionADO_DeactivateFromBatch] DEFAULT ((0)),
[StreamData] [bit] NULL CONSTRAINT [DF_PreIngestionADO_StreamData] DEFAULT ((1)),
[NoOfThreads] [int] NULL CONSTRAINT [DF_PreIngestionADO_NoOfStreams] DEFAULT ((1)),
[IncrementalColumns] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[IncrementalClauseExp] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DateColumn] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[NoOfOverlapDays] [int] NULL CONSTRAINT [DF_PreIngestionADO_NoOfOverlapDays] DEFAULT ((7)),
[FetchMinValuesFromSysLog] [bit] NULL CONSTRAINT [DF_PreIngestionADO_FetchMinValuesFromSysLog] DEFAULT ((0)),
[FullLoad] [bit] NULL CONSTRAINT [DF_PreIngestionADO_FullLoad] DEFAULT ((0)),
[TruncateTrg] [bit] NULL CONSTRAINT [DF_PreIngestionADO_OnFulloadTruncTrg] DEFAULT ((0)),
[srcFilter] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcFilterIsAppend] [bit] NULL CONSTRAINT [DF_PreIngestionADO_srcFilterIsAppend] DEFAULT ((1)),
[preFilter] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[IgnoreColumns] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[InitLoad] [bit] NULL CONSTRAINT [DF_PreIngestionADO_InitLoad] DEFAULT ((0)),
[InitLoadFromDate] [date] NULL,
[InitLoadToDate] [date] NULL,
[InitLoadBatchBy] [varchar] (1) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_PreIngestionADO_InitLoadBatchBy] DEFAULT ('M'),
[InitLoadBatchSize] [int] NULL CONSTRAINT [DF_PreIngestionADO_InitLoadBatchMonths] DEFAULT ((1)),
[InitLoadKeyColumn] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[InitLoadKeyMaxValue] [int] NULL,
[SyncSchema] [bit] NULL CONSTRAINT [DF_PreIngestionADO_SyncSchema] DEFAULT ((1)),
[OnErrorResume] [bit] NULL CONSTRAINT [DF_PreIngestionADO_OnErrorResume] DEFAULT ((1)),
[CleanColumnNameSQLRegExp] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[RemoveInColumnName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PreProcessOnTrg] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PostProcessOnTrg] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PreInvokeAlias] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PostInvokeAlias] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FlowType] [varchar] (25) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_PreIngestionADO_FlowType] DEFAULT ('ado'),
[Description] [nvarchar] (2048) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FromObjectMK] [int] NULL,
[ToObjectMK] [int] NULL,
[CreatedBy] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_PreIngestionADO_CreatedBy] DEFAULT (suser_sname()),
[CreatedDate] [datetime] NULL CONSTRAINT [DF_PreIngestionADO_CreatedDate] DEFAULT (getdate())
)
GO
SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE TRIGGER [flw].[AddPreIngestionADO2SysLog] ON [flw].[PreIngestionADO]
FOR INSERT
AS

INSERT INTO  flw.SysLog (FlowID,[FlowType], [Process])
    SELECT FlowId ,FlowType ,  [srcServer] + '.' + [flw].[GetADOSourceName](FlowId)  + '-->' + [trgServer] + '.' + [trgDBSchTbl] as [Process]
        FROM inserted
GO
ALTER TABLE [flw].[PreIngestionADO] ADD CONSTRAINT [PK_PreIngestionADO] PRIMARY KEY CLUSTERED ([FlowID])
GO
CREATE NONCLUSTERED INDEX [NCI_FlowID] ON [flw].[PreIngestionADO] ([FlowID]) INCLUDE ([Batch], [SysAlias], [DeactivateFromBatch])
GO
EXEC sp_addextendedproperty N'MS_Description', 'Batch is an arbitrary identifier that groups flow executions. Execution of a flows can be grouped by Batch and SysAlias in conjunction or independently. Defining Batch is mandatory in all data flow tables. Valid Batch values can be registered in [flw].[ SysBatch] for convenience, but this doesn''t enforce referential integrity.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'Batch'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionADO].[BatchOrderBy] column is an optional integer column that specifies the order in which batches should be executed when processing data. The lower the number, the earlier the batch will be processed. This column is not mandatory and is often used when batches are dependent on one another and should be executed in a specific order. If not specified, batches will be executed in ascending order of their Batch value.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'BatchOrderBy'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The column [flw].[PreIngestionADO].[CleanColumnNameSQLRegExp] is a nullable nvarchar(1024) column in the [flw].[PreIngestionADO] table. It stores a regular expression string to be used for cleaning column names of special characters, such as whitespaces, and unsupported characters for the target database. This column can be used to apply a custom clean up logic on the column names if required. By default, this column has no default constraint defined.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'CleanColumnNameSQLRegExp'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionADO].[CreatedDate] column is a datetime column that contains the date and time when the data pipeline is created. It has a default constraint that sets the value to the current date and time using the GETDATE() function when a new row is inserted into the table. This column can be used to keep track of when a particular data pipeline was created.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'CreatedBy'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The DateColumn column in the flw.PreIngestionADO table is a string column that stores the name of the date/time column that should be used as the incremental load checkpoint for the current pipeline. This means that data from the source database will only be ingested if it has a value greater than the maximum value of the date/time column in the target table. By default, this column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'DateColumn'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionADO].[DeactivateFromBatch] column stores a Boolean value indicating whether the current data pipeline should be deactivated or not. If this column value is set to 1 (True), the pipeline will be deactivated after the specified batch is completed. The default value for this column is 0 (False).', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'DeactivateFromBatch'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[PreIngestionADO].[Description]', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'Description'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionADO].[FetchMinValuesFromSysLog] column is a Boolean value that determines whether to fetch minimum values from the system log table. The default value is 0 (False). When enabled, the system log table is queried for the minimum value of the DateColumn for the current data flow table. This is useful when data from the source table is being deleted or updated, and you want to ensure that you only retrieve the latest data. If this column is set to 1 (True), the minimum value of the DateColumn is used to determine the starting point for data retrieval.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'FetchMinValuesFromSysLog'
GO
EXEC sp_addextendedproperty N'MS_Description', 'FlowID is a system generated unique numeric identifier for each data pipeline. FlowID can be utilized to track and audit the execution of data pipelines flows. See the search command for details.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'FlowID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionADO].[FlowType] column is a varchar(25) column that specifies the type of data flow for the pipeline process. The default value for this column is ''ado''. This column is used to differentiate between different types of data sources or sinks. ', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'FlowType'
GO
EXEC sp_addextendedproperty N'MS_Description', 'FromObjectMK column stores the unique identifier for the source object being ingested. A unique Master Key (MK) is generated for each SQLFlow Object. The master key is essential in calculating lineage dependencies and the correct execution order for each batch. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'FromObjectMK'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[PreIngestionADO].[FullLoad] column stores a Boolean value indicating whether the entire target table should be loaded or just the changes since the last ingestion. This can be used to determine whether to run a full load of the target table or an incremental load based on the source data changes. The default value for this column is 0 (False), indicating that only changes since the last ingestion should be loaded.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'FullLoad'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionADO].[IgnoreColumns] column stores a list of column names that should be ignored during ingestion. These columns are not inserted into the target table. The column stores a comma-separated list of column names, which can be referenced directly. The default value for this column is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'IgnoreColumns'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionADO].[IncrementalClauseExp] column stores an SQL expression to be used in incremental loading. Incremental loading can be used to load only the changed or new data into the target table. This expression determines the filter to be applied to the source table to get the required rows. The value stored in this column is a string containing a SQL expression.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'IncrementalClauseExp'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The column [flw].[PreIngestionADO].[IncrementalColumns] is a nullable nvarchar(1024) column in the SQLFlow table [flw].[PreIngestionADO]. It stores a comma-separated list of column names to be used in the incremental ingestion process. The incremental ingestion process is used to import only the data that has changed since the previous run. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'IncrementalColumns'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionADO].[InitLoad] column is a Boolean value that specifies whether to perform an initial load of data into the target table. If set to 1 (True), data from the source table will be loaded into the target table in its entirety. If set to 0 (False), only new or updated data will be loaded into the target table. The default value is 0 (False).', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'InitLoad'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionADO].[InitLoadBatchBy] column specifies the interval to use when performing the initial load of data into the target table. It is a character string that can have one of the following values: Number of days (D), months (M), or Key values (K). The valid options are ', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'InitLoadBatchBy'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[PreIngestionADO].[InitLoadBatchSize]', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'InitLoadBatchSize'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The InitLoadFromDate column in the [flw].[PreIngestionADO] table stores the start date for an initial load of data. This column is of type date and has a default value of NULL. If an initial load of data is required, this column should be populated with the desired start date.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'InitLoadFromDate'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionADO].[InitLoadKeyColumn] column stores the name of the column used as the key for incremental loads in a data pipeline. This column is used when InitLoad column is set to 1 (True) to indicate that an initial load of data is required. The values of the column specified in this field are used to determine the initial data to load. The default value for this column is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'InitLoadKeyColumn'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The InitLoadKeyMaxValue column in the [flw].[PreIngestionADO] table stores the maximum value of the key column to be used for the initial load. It is an optional column used in conjunction with the InitLoadKeyColumn column to define the range of values for the initial load. The default value for this column is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'InitLoadKeyMaxValue'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The InitLoadToDate column in the [flw].[PreIngestionADO] table stores the end date for an initial load operation. This column is used in conjunction with InitLoadFromDate to specify the range of data that should be initially loaded into the target table. The default value for this column is NULL, which means that no end date is specified and the initial load will include all available data up to the start date. The dates should be in the format of YYYY-MM-DD.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'InitLoadToDate'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionADO].[NoOfOverlapDays] column stores an integer value that indicates the number of days of overlap between the source and target data when ingesting data. The default value for this column is 7 days. This means that data from the previous 7 days is ingested along with the current data. This column can be used to ensure that all the necessary data is ingested into the target table, especially when there is a delay in ingesting the data.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'NoOfOverlapDays'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionADO].[NoOfThreads] column stores an integer value indicating the number of threads that should be used to stream the data during ingestion. The default value for this column is 1. This column is used to optimize processing times and resource consumption. When false, the data is first read into memory and then streamed to the target table in a multi-threaded operation. Streaming is recommended for tables that cannot be accommodated in available RAM.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'NoOfThreads'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionADO].[OnErrorResume] column is a boolean value indicating whether the data pipeline should resume execution when an error occurs. The default value for this column is 1 (True), meaning that execution will resume when an error occurs. If set to 0 (False), execution will stop and the error will need to be resolved before continuing.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'OnErrorResume'
GO
EXEC sp_addextendedproperty N'MS_Description', 'PostInvokeAlias column stores an alias for a pre-registered Azure Data Factory or Automation Runbook to be executed after the ingestion process. This feature can be used for tasks such as data validation or preparing the source data. The desired invoke must be pre-registered in the table [flw].[Invoke] and referenced by its Alias. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'PostInvokeAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionADO].[PostProcessOnTrg] column is used to specify a SQL statement that needs to be executed on the target database table after the data has been loaded into it. This column is optional, and if specified, the SQL statement in it will be executed immediately after the data has been loaded. This can be useful for performing any data cleanups or transformations that need to be done on the data in the target table.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'PostProcessOnTrg'
GO
EXEC sp_addextendedproperty N'MS_Description', 'preFilter column stores the SQL WHERE clause expression which is appended to the transformation view. This expression can be used to filter the data based on certain criteria, such as date ranges or specific column values.  A transformation view is automatically generated for each target table and the definition for each transformation is read from the table [flw].[PreIngestionTransfrom]. [flw].[PreIngestionTransfrom] is populated dynamically, but the values can be overridden in accordance with contents of various columns. The default value for this column is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'preFilter'
GO
EXEC sp_addextendedproperty N'MS_Description', 'PreInvokeAlias column stores an alias for a pre-registered Azure Data Factory or Automation Runbook to be executed before the ingestion process begins. This feature can be used for tasks such as data validation or preparing the source data. The desired invoke must be pre-registered in the table [flw].[Invoke] and referenced by its Alias. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'PreInvokeAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The column [flw].[PreIngestionADO].[PreProcessOnTrg] is used to store the name of the stored procedure to be executed on the target database before inserting or updating the data. This stored procedure can be used to perform any pre-processing logic on the data before it is written to the target database. If there is no pre-processing required, this column can be left null.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'PreProcessOnTrg'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[PreIngestionADO].[RemoveInColumnName] is a column in the [flw].[PreIngestionADO] table which is used to remove a specific substring from the column names during the data pipeline process. This column allows users to specify a substring that should be removed from the source column names before they are processed further in the pipeline. This can be useful in situations where column names contain a prefix or suffix that is not relevant to the downstream processing or when there is a need to standardize the column names across different sources. If a value is specified in this column, the pipeline process will remove the substring from all the column names before they are processed further.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'RemoveInColumnName'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The srcDatabase column in the [flw].[PreIngestionADO] table stores the name of the source database from which data is being ingested. This column is optional and may be left as NULL if the data is being ingested from a specific schema or table within the source database. If provided, the database name must be specified as a string. There is no default value for this column specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'srcDatabase'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionADO].[srcFilter] column stores the expression for filtering the data from the source table. This column stores the filter expression in a SQL WHERE clause format. The default value for this column is NULL. If the column has a value, the data that matches the specified criteria will be included in the pipeline. The [flw].[PreIngestionADO].[srcFilterIsAppend] column indicates whether the filter should be treated as a replacement or addition to the calculated filters. If [flw].[PreIngestionADO].[srcFilterIsAppend] is set to 1 (True), the filter is added to the calculated filters. If [flw].[PreIngestionADO].[srcFilterIsAppend] is set to 0 (False), the filter will replace the calculated filters.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'srcFilter'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The srcFilterIsAppend column in the [flw].[PreIngestionADO] table stores a Boolean value that indicates whether the IncrementalClauseExp expression should be treated as a replacement or addition to the calculated filters. This column can be used to combine multiple filters for more complex filtering scenarios. The default value for this column is 1 (True).', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'srcFilterIsAppend'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The srcObject column in the flw.PreIngestionADO table stores the name of the source database object (table or view) from where the data will be ingested. This column is a mandatory field and cannot be null. When defining the value of this column, please ensure that the object exists in the specified source database.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'srcObject'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The srcDatabase column in the [flw].[PreIngestionADO] table stores the name of the source database from which data is being ingested. Naming of the object should follow the convention of the ADO Source.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'srcSchema'
GO
EXEC sp_addextendedproperty N'MS_Description', 'srcServer refers to the column Alias from the table [flw].[SysDataSource]. Alias is an arbitrary acronym referencing the source database for the current pipeline.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'srcServer'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The StreamData column in the [flw].[PreIngestionADO] table stores a Boolean value indicating whether the data should be streamed during ingestion. If the value is set to 1 (True), the data will be streamed during ingestion. Streaming data can optimize processing times and resource consumption. On the other hand, if the value is set to 0 (False), the data is first read into memory and then streamed to the target table in a multi-threaded operation. This mode is recommended for tables that cannot be accommodated in available RAM. The default value for this column is 1 (True), indicating that data streaming is enabled by default.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'StreamData'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionADO].[SyncSchema] column is a Boolean value that indicates whether the schema of the target table should be automatically synchronized with the source table. If the value is set to 1 (True), schema changes in the source table are propagated to the target table. The default value for this column is 1 (True).', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'SyncSchema'
GO
EXEC sp_addextendedproperty N'MS_Description', 'SysAlias is an arbitrary alias that identifies the source system from which the data was initially fetched. Execution of data pipelines can be grouped by Batch and SysAlias in conjunction or independently. Defining SysAlias is mandatory in all data flow tables. Valid SysAlias values can be registered in [flw].[SysAlias] for convenience, but this doesn''t enforce referential integrity.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'SysAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', 'ToObjectMK column stores the unique identifier for the target object where the data is being ingested. A unique Master Key (MK) is generated for each SQLFlow Object. The master key is essential in calculating lineage dependencies and the correct execution order for each batch. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'ToObjectMK'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The trgDBSchTbl column in the [flw].[PreIngestionADO] table stores the name of the target database, schema, and table where the data will be ingested. The value provided in this column should follow the recommended format: [Database].[Schema].[ObjectName].

For example, if the target database is Sales, the schema is dbo, and the table name is Orders, the value for trgDBSchTbl should be Sales.dbo.Orders.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'trgDBSchTbl'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The column [flw].[PreIngestionADO].[trgServer] refers to the Alias column from the table [flw].[SysDataSource]. Alias is an arbitrary acronym referencing the target database for the current pipeline. This column is not nullable and must be specified when defining a new data pipeline. There is no default value specified for this column.



', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'trgServer'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The column [flw].[PreIngestionADO].[TruncateTrg] is a Boolean value that indicates whether to truncate the target table before loading data. If this value is set to 1 (True), the target table will be truncated before data ingestion. The default value for this column is 0 (False), meaning that the target table will not be truncated before ingestion.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionADO', 'COLUMN', N'TruncateTrg'
GO
