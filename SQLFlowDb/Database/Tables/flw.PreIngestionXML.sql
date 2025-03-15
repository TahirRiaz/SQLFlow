CREATE TABLE [flw].[PreIngestionXML]
(
[FlowID] [int] NOT NULL CONSTRAINT [DF_PreIngestionXML_FlowID] DEFAULT (NEXT VALUE FOR [flw].[FlowID]),
[Batch] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SysAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[ServicePrincipalAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcPath] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[srcPathMask] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcFile] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[trgServer] [nvarchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[trgDBSchTbl] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[trgDesiredIndex] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[hierarchyIdentifier] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[XmlToDataTableCode] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[preFilter] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SearchSubDirectories] [bit] NULL CONSTRAINT [DF_PreIngestionXML_earchSubDirectories] DEFAULT ((0)),
[copyToPath] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcDeleteIngested] [bit] NULL CONSTRAINT [DF_PreIngestionXML_srcDeleteIngested] DEFAULT ((0)),
[srcDeleteAtPath] [bit] NULL CONSTRAINT [DF_PreIngestionXML_srcDeleteAtPath] DEFAULT ((0)),
[zipToPath] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SyncSchema] [bit] NULL CONSTRAINT [DF_PreIngestionXML_SyncSchema] DEFAULT ((1)),
[ExpectedColumnCount] [int] NULL CONSTRAINT [DF_PreIngestionXML_ExpectedColumnCount] DEFAULT ((0)),
[DefaultColDataType] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FetchDataTypes] [bit] NULL CONSTRAINT [DF_PreIngestionXML_FetchDataTypes] DEFAULT ((1)),
[OnErrorResume] [bit] NULL CONSTRAINT [DF_PreIngestionXML_OnErrorResume] DEFAULT ((1)),
[NoOfThreads] [int] NULL CONSTRAINT [DF_PreIngestionXML_Parallelize] DEFAULT ((4)),
[PreProcessOnTrg] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PostProcessOnTrg] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PreInvokeAlias] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[InitFromFileDate] [varchar] (15) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[InitToFileDate] [varchar] (15) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[BatchOrderBy] [int] NULL,
[DeactivateFromBatch] [bit] NULL CONSTRAINT [DF_PreIngestionXML_DeactivateFromBatch] DEFAULT ((0)),
[EnableEventExecution] [bit] NULL CONSTRAINT [DF_PreIngestionXML_EnableEventExecution] DEFAULT ((0)),
[FlowType] [varchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL CONSTRAINT [DF_PreIngestionXML_FlowType] DEFAULT (N'xml'),
[FromObjectMK] [int] NULL,
[ToObjectMK] [int] NULL,
[ShowPathWithFileName] [bit] NULL CONSTRAINT [DF_PreIngestionXML_ShowPathWithFileName] DEFAULT ((0)),
[CreatedBy] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_PreIngestionXML_CreatedBy] DEFAULT (suser_sname()),
[CreatedDate] [datetime] NULL CONSTRAINT [DF_PreIngestionXML_CreatedDate] DEFAULT (getdate())
) ON [PRIMARY]
GO
SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE TRIGGER [flw].[AddPreFlowToLogTableXML] ON [flw].[PreIngestionXML]
FOR INSERT, UPDATE

AS
BEGIN
	-- Handling INSERT operations
    IF (SELECT COUNT(*) FROM inserted) > 0 AND (SELECT COUNT(*) FROM deleted) = 0
    BEGIN
        INSERT INTO [flw].[SysLog] ([FlowID], [FlowType], [Process], [ProcessShort])
                SELECT FlowID, FlowType,   'xml -->' + [trgServer] + '.' + [trgDBSchTbl] as [Process], 'xml -->' + [trgServer] + '.' + [trgDBSchTbl] as [ProcessShort]
        FROM inserted
    END

    -- Handling UPDATE operations
    IF (SELECT COUNT(*) FROM inserted) > 0 AND (SELECT COUNT(*) FROM deleted) > 0
    BEGIN
        UPDATE [flw].[SysLog]
        SET [FlowType] = i.[FlowType],
            [Process] = 'xml -->' + i.[trgServer] + '.' + i.[trgDBSchTbl],
			[ProcessShort] = 'xml -->' + i.[trgServer] + '.' + i.[trgDBSchTbl]
        FROM inserted i
        INNER JOIN [flw].[SysLog] sl ON sl.[FlowID] = i.[FlowID]; 
    END
END
GO
ALTER TABLE [flw].[PreIngestionXML] ADD CONSTRAINT [Chk_trgDBSchTbl_XML] CHECK ((parsename([trgDBSchTbl],(3)) IS NOT NULL))
GO
ALTER TABLE [flw].[PreIngestionXML] ADD CONSTRAINT [PK_PreIngestionXML] PRIMARY KEY CLUSTERED ([FlowID]) ON [PRIMARY]
GO
CREATE NONCLUSTERED INDEX [NCI_FlowID] ON [flw].[PreIngestionXML] ([FlowID]) INCLUDE ([Batch], [SysAlias], [DeactivateFromBatch]) ON [PRIMARY]
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[PreIngestionXML] provides metadata for ingesting XML files into a SQL Server database. The table contains columns related to the source and destination paths, file names, server and database information, filtering, and other processing options.

The table also includes columns specific to XML files, such as RootXPath and xmlns, which provide information about the root node and XML namespace.

There are also columns like Batch, BatchOrderBy, and NoOfThreads which suggest that the pre-ingestion process can be run in batches with specific ordering and concurrency settings.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', 'Batch is an arbitrary identifier that groups flow executions. Execution of a flows can be grouped by Batch and SysAlias in conjunction or independently. Defining Batch is mandatory in all data flow tables. Valid Batch values can be registered in [flw].[ SysBatch] for convenience, but this doesn''t enforce referential integrity.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'Batch'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionXML].[BatchOrderBy] column in the [flw].[PreIngestionXML] table stores an integer value indicating the order in which the batch should be processed. This column can be used to prioritize the order in which files are ingested. If not specified, the order of files will be determined by the order in which they are retrieved from the file system. The default value for this column is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'BatchOrderBy'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionXML].[copyToPath] column stores the file path where the ingested XML files are to be copied. If specified, ingested files will be copied to the specified location after the ingestion process. This can be useful for creating backups or storing copies of the original files. The default value for this column is NULL, indicating that no files will be copied.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'copyToPath'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionXML].[CreatedBy] column of the SQLFlow table [flw].[PreIngestionXML] stores the name of the user who created the data pipeline. The default value for this column is the name of the user who executed the CREATE TABLE statement, using the function suser_sname().', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'CreatedBy'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionXML].[CreatedDate] column of the [flw].[PreIngestionXML] table stores the date and time when the record was created. This value is automatically generated by the system and does not have a default value specified. The value stored in this column can be used to track the creation time of the record.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'CreatedDate'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionXML].[DeactivateFromBatch] column of the [flw].[PreIngestionXML] table stores a Boolean value indicating whether the data pipeline should be deactivated from the batch after execution. If set to 1 (True), the pipeline will be deactivated from the batch after execution. The default value for this column is 0 (False).', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'DeactivateFromBatch'
GO
EXEC sp_addextendedproperty N'MS_Description', 'A string that overrides default data type for creating columns in the target table. Default values is varchar(255) and is defined in [flw].[SysCFG]. This is useful when the source file contains columns that are longer than 255 characters. The default column value is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'DefaultColDataType'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionXML].[ExpectedColumnCount] column stores an integer value indicating the expected number of columns in the target database table. If the actual number of columns in the target table does not match the value specified in this column, the ingestion process will fail. The default value for this column is 0, which means that the ingestion process will not check the number of columns in the target table.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'ExpectedColumnCount'
GO
EXEC sp_addextendedproperty N'MS_Description', 'A boolean flag indicating whether to infer the data types of the columns in the CSV file. The default value is 0. Enabling this flag will assert the data an suggest datatypes for each column. Results are stored in table ', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'FetchDataTypes'
GO
EXEC sp_addextendedproperty N'MS_Description', 'FlowID is a system generated unique numeric identifier for each data pipeline. FlowID can be utilized to track and audit the execution of data pipelines flows. See the search command for details.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'FlowID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionXML].[FlowType] column stores a string that represents the type of data pipeline, which is "XML" in this case. This column has a default value of "XML" and cannot be null. It can be used to distinguish between different types of data pipelines and to filter or group them as needed.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'FlowType'
GO
EXEC sp_addextendedproperty N'MS_Description', 'FromObjectMK column stores the unique identifier for the source object being ingested. A unique Master Key (MK) is generated for each SQLFlow Object. The master key is essential in calculating lineage dependencies and the correct execution order for each batch. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'FromObjectMK'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionXML].[RootXPath] column in the SQLFlow table [flw].[PreIngestionXML] specifies the root XPath of the XML file. The root XPath represents the top-level element in the XML file and is used to identify the location of the data to be ingested.

For example, consider an XML file that contains information about a list of employees. The root XPath for this file might be "/employees", where "employees" is the name of the top-level element in the file. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'hierarchyIdentifier'
GO
EXEC sp_addextendedproperty N'MS_Description', 'A string that specifies the earliest file creation date that should be included in the ingestion process. Only files created on or after this date will be processed. The default value is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'InitFromFileDate'
GO
EXEC sp_addextendedproperty N'MS_Description', 'A string that specifies the latest file creation date that should be included in the ingestion process. Only files created before or on this date will be processed. The default value is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'InitToFileDate'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionXML].[NoOfThreads] column stores an integer value that determines the number of threads used to process the XML file. Increasing the number of threads can speed up the ingestion process, but it may also increase the load on the server. The default value for this column is 4.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'NoOfThreads'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The column [flw].[PreIngestionXML].[OnErrorResume] stores a Boolean value indicating whether the data pipeline should continue execution in case of an error. If set to True, the pipeline will continue execution after an error has occurred. If set to False, the pipeline will stop execution as soon as an error occurs. The default value for this column is 1 (True), meaning that the pipeline will resume execution in case of an error.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'OnErrorResume'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionXML].[PostProcessOnTrg] column stores the name of the stored procedure that should be executed on the target SQL Server database table after the data has been ingested. This can be used to perform additional processing or transformations on the data after it has been loaded into the table. The default value for this column is NULL, meaning that no post-processing will be performed by default.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'PostProcessOnTrg'
GO
EXEC sp_addextendedproperty N'MS_Description', 'preFilter column stores the SQL WHERE clause expression which is appended to the transformation view. This expression can be used to filter the data based on certain criteria, such as date ranges or specific column values.  A transformation view is automatically generated for each target table and the definition for each transformation is read from the table [flw].[PreIngestionTransfrom]. [flw].[PreIngestionTransfrom] is populated dynamically, but the values can be overridden in accordance with contents of various columns. The default value for this column is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'preFilter'
GO
EXEC sp_addextendedproperty N'MS_Description', 'PreInvokeAlias column stores an alias for a pre-registered Azure Data Factory or Automation Runbook to be executed before the ingestion process begins. This feature can be used for tasks such as data validation or preparing the source data. The desired invoke must be pre-registered in the table [flw].[Invoke] and referenced by its Alias. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'PreInvokeAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The PreProcessOnTrg column of the [flw].[PreIngestionXML] table stores the name of the stored procedure that should be executed on the target table before data is ingested. This column can be used to apply transformations or data cleansing on the target table. The default value for this column is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'PreProcessOnTrg'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionXML].[SearchSubDirectories] column stores a Boolean value indicating whether to search for XML files in subdirectories of the source path. If set to True, XML files in subdirectories will also be processed. The default value for this column is 0 (False).', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'SearchSubDirectories'
GO
EXEC sp_addextendedproperty N'MS_Description', 'This column specifies whether the file path should be concatenated into the target column FileName_DW. The default value is 0, indicating that the error message will not include the file name.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'ShowPathWithFileName'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionXML].[srcDeleteAtPath] column stores a Boolean value indicating whether the source XML file should be deleted after it has been ingested. If set to 1 (True), the source file will be deleted after successful ingestion. The default value for this column is 0 (False), which means that the source file will not be deleted after ingestion unless specified otherwise.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'srcDeleteAtPath'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionXML].[srcDeleteIngested] column stores a Boolean value indicating whether the source XML file should be deleted after it has been successfully ingested into the target database. The default value for this column is 0 (False), meaning the file will not be deleted after ingestion. If the value is set to 1 (True), the file will be deleted after the ingestion process is complete.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'srcDeleteIngested'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionXML].[srcFile] column in the SQLFlow framework table stores the name of the source XML file. This is a required field and must be specified in order to run the ingestion process. The file name must match the specified mask if one is provided.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'srcFile'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The column [srcPath] in the [flw].[PreIngestionXML] table specifies the location of the source XML file that needs to be ingested. It is a non-null string value and must be provided for the ingestion process to start. The path can be absolute or relative to the location of the ingestion script. An example of [srcPath] with a relative path is .\Data\sample.xml, while an example of [srcPath] with an absolute path is C:\Users\Username\Documents\sample.xml.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'srcPath'
GO
EXEC sp_addextendedproperty N'MS_Description', 'This column stores a regular expression mask that is used to filter the folders in the source path. Only files within folders matching the mask will be processed', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'srcPathMask'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionXML].[SyncSchema] column is a Boolean value indicating whether to synchronize the schema of the target database table with the schema of the XML file. If set to 1 (True), the column will synchronize the target table schema with the schema of the XML file during the ingestion process. The default value for this column is 1 (True), meaning that the schema will be synchronized by default. If set to 0 (False), the schema of the target table will not be synchronized with the XML file schema during ingestion.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'SyncSchema'
GO
EXEC sp_addextendedproperty N'MS_Description', 'SysAlias is an arbitrary alias that identifies the source system from which the data was initially fetched. Execution of data pipelines can be grouped by Batch and SysAlias in conjunction or independently. Defining SysAlias is mandatory in all data flow tables. Valid SysAlias values can be registered in [flw].[SysAlias] for convenience, but this doesn''t enforce referential integrity.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'SysAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', 'ToObjectMK column stores the unique identifier for the target object where the data is being ingested. A unique Master Key (MK) is generated for each SQLFlow Object. The master key is essential in calculating lineage dependencies and the correct execution order for each batch. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'ToObjectMK'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionXML].[trgDBSchTbl] column stores the name of the target SQL Server database, schema and table where the data from the XML file will be ingested. The value of this column is a concatenated string of the target database, schema and table names separated by dots. If the target table does not exist, it will be created during the ingestion process. The default value for this column is NULL, meaning that the target database, schema and table need to be specified explicitly.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'trgDBSchTbl'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionXML].[trgServer] column stores the Alias of the SQL Server instance where the target database table resides. This column is required for the ingestion process to identify the location of the target database. The default value for this column is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'trgServer'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionXML].[zipToPath] column is a nullable string column that stores the path to which the XML file(s) should be zipped after they have been ingested into the target database. This column can be used to archive the source file after ingestion. The default value for this column is NULL. If this column is set, the source file will be zipped to the specified path.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXML', 'COLUMN', N'zipToPath'
GO
