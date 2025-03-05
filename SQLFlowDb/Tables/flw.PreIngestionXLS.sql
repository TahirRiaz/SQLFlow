CREATE TABLE [flw].[PreIngestionXLS]
(
[FlowID] [int] NOT NULL CONSTRAINT [DF_PreIngestionXLS_FlowID] DEFAULT (NEXT VALUE FOR [flw].[FlowID]),
[Batch] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SysAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ServicePrincipalAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcPath] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcPathMask] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcFile] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SearchSubDirectories] [bit] NULL CONSTRAINT [DF_PreIngestionXLS_earchSubDirectories] DEFAULT ((0)),
[copyToPath] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[srcDeleteIngested] [bit] NULL CONSTRAINT [DF_PreIngestionXLS_srcDeleteIngested] DEFAULT ((0)),
[srcDeleteAtPath] [bit] NULL CONSTRAINT [DF_PreIngestionXLS_srcDelete] DEFAULT ((0)),
[zipToPath] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[trgServer] [nvarchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[trgDBSchTbl] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[trgDesiredIndex] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[preFilter] [nvarchar] (1024) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FirstRowHasHeader] [bit] NULL CONSTRAINT [DF_PreIngestionXLS_FirstRowHasHeader] DEFAULT ((1)),
[SheetName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SheetRange] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[UseSheetIndex] [bit] NULL CONSTRAINT [DF_PreIngestionXLS_UseSheetIndex] DEFAULT ((0)),
[SyncSchema] [bit] NULL CONSTRAINT [DF_PreIngestionXLS_SyncSchema] DEFAULT ((1)),
[ExpectedColumnCount] [int] NULL CONSTRAINT [DF_PreIngestionXLS_ExpectedColumnCount] DEFAULT ((0)),
[IncludeFileLineNumber] [bit] NULL CONSTRAINT [DF_PreIngestionXLS_IncludeFileLineNumber] DEFAULT ((0)),
[DefaultColDataType] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FetchDataTypes] [bit] NULL CONSTRAINT [DF_PreIngestionXLS_FetchDataTypes] DEFAULT ((0)),
[OnErrorResume] [bit] NULL CONSTRAINT [DF_PreIngestionXLS_OnErrorResume] DEFAULT ((1)),
[NoOfThreads] [int] NULL CONSTRAINT [DF_PreIngestionXLS_Parallelize] DEFAULT ((4)),
[PreProcessOnTrg] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PostProcessOnTrg] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[InitFromFileDate] [varchar] (15) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[InitToFileDate] [varchar] (15) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PreInvokeAlias] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[BatchOrderBy] [int] NULL,
[DeactivateFromBatch] [bit] NULL CONSTRAINT [DF_PreIngestionXLS_DeactivateFromBatch] DEFAULT ((0)),
[EnableEventExecution] [bit] NULL CONSTRAINT [DF_PreIngestionXLS_EnableEventExecution] DEFAULT ((0)),
[FlowType] [varchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL CONSTRAINT [DF_PreIngestionXLS_FlowType] DEFAULT (N'xls'),
[FromObjectMK] [int] NULL,
[ToObjectMK] [int] NULL,
[ShowPathWithFileName] [bit] NULL CONSTRAINT [DF_PreIngestionXLS_ShowPathWithFileName] DEFAULT ((0)),
[CreatedBy] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_PreIngestionXLS_CreatedBy] DEFAULT (suser_sname()),
[CreatedDate] [datetime] NULL CONSTRAINT [DF_PreIngestionXLS_CreatedDate] DEFAULT (getdate())
) ON [PRIMARY]
GO
SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE TRIGGER [flw].[AddPreFlowToLogTableXLS] ON [flw].[PreIngestionXLS]
FOR INSERT, UPDATE

AS
BEGIN
	-- Handling INSERT operations
    IF (SELECT COUNT(*) FROM inserted) > 0 AND (SELECT COUNT(*) FROM deleted) = 0
    BEGIN
        INSERT INTO [flw].[SysLog] ([FlowID], [FlowType], [Process], [ProcessShort])
                SELECT FlowID, FlowType,   'xls -->' + [trgServer] + '.' + [trgDBSchTbl] as [Process], 'xls -->' + [trgServer] + '.' + [trgDBSchTbl] as [ProcessShort]
        FROM inserted
    END

    -- Handling UPDATE operations
    IF (SELECT COUNT(*) FROM inserted) > 0 AND (SELECT COUNT(*) FROM deleted) > 0
    BEGIN
        UPDATE [flw].[SysLog]
        SET [FlowType] = i.[FlowType],
            [Process] = 'xls -->' + i.[trgServer] + '.' + i.[trgDBSchTbl],
			[ProcessShort] = 'xls -->' + i.[trgServer] + '.' + i.[trgDBSchTbl]
        FROM inserted i
        INNER JOIN [flw].[SysLog] sl ON sl.[FlowID] = i.[FlowID]; 
    END
END
GO
ALTER TABLE [flw].[PreIngestionXLS] ADD CONSTRAINT [Chk_trgDBSchTbl_XLS] CHECK ((parsename([trgDBSchTbl],(3)) IS NOT NULL))
GO
ALTER TABLE [flw].[PreIngestionXLS] ADD CONSTRAINT [PK_PreIngestionXLS] PRIMARY KEY CLUSTERED ([FlowID]) ON [PRIMARY]
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[PreIngestionXLS] provides metadata for ingesting XLS files into a SQL Server database. The table also includes columns specific to XLS files, such as SheetName, SheetRange, and UseSheetIndex, which provide information about the sheets and ranges within the XLS file that should be ingested.

There are also columns like Batch, BatchOrderBy, and NoOfThreads which suggest that the pre-ingestion process can be run in batches with specific ordering and concurrency settings.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', '1 / 3

The [flw].[PreIngestionXLS].[Batch] column is a string column that stores the batch name for the data pipeline. A batch is a collection of related data operations that can be executed together as a unit. The batch name can be used to group related data pipeline flows together. The default constraint for this column is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'Batch'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionXLS].[BatchOrderBy] column specifies the order in which the batches are to be processed during the data pipeline. The values in this column determine the order of batches, with a smaller number indicating that the batch should be processed earlier in the pipeline. The default value of this column is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'BatchOrderBy'
GO
EXEC sp_addextendedproperty N'MS_Description', 'This column in the [flw].[PreIngestionXLS] table stores the path of the directory where the source file(s) should be copied before ingestion. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'copyToPath'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The column [flw].[PreIngestionXLS].[CreatedBy] stores the name of the user who created the corresponding data pipeline in the table. The default constraint [DF_PreIngestionXLS_CreatedBy] sets the value of this column to the name of the current user using the system function suser_sname().

As an example, suppose a user named "John" creates a new data pipeline in the [flw].[PreIngestionXLS] table. Then, the [flw].[PreIngestionXLS].[CreatedBy] column for that pipeline would be automatically set to "John" by the default constraint.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'CreatedBy'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionXLS].[CreatedDate] column in the flw.PreIngestionXLS table represents the date and time when a record was created in the table. The data type of this column is datetime. When a new record is inserted into the flw.PreIngestionXLS table, the default value for this column is set to the current date and time using the getdate() function. This column can be used to track when a record was added to the table and for auditing purposes.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'CreatedDate'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The column [DeactivateFromBatch] in the [flw].[PreIngestionXLS] table is a boolean type column that specifies whether the current data flow should be deactivated from a batch. The default value for this column is 0, meaning that the data flow is active and will be executed as part of a batch. If the value is set to 1, the data flow will be deactivated and will not be executed as part of a batch. This column is used to control the execution of the data flow and can be useful for scenarios where certain data flows need to be executed separately or need to be excluded from a batch due to specific requirements or dependencies.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'DeactivateFromBatch'
GO
EXEC sp_addextendedproperty N'MS_Description', 'A string that overrides default data type for creating columns in the target table. Default values is varchar(255) and is defined in [flw].[SysCFG]. This is useful when the source file contains columns that are longer than 255 characters. The default column value is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'DefaultColDataType'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [ExpectedColumnCount] column in the [flw].[PreIngestionXLS] table stores the expected number of columns in the source Excel file. It is an integer value and has a default constraint of 0. This column is used in the data pipeline to verify that the source Excel file has the expected number of columns before the data is ingested into the target SQL Server database table. If the actual number of columns in the source Excel file does not match the expected number of columns, the data pipeline may fail or produce unexpected results. Therefore, it is important to set the value of this column appropriately based on the structure of the source Excel file.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'ExpectedColumnCount'
GO
EXEC sp_addextendedproperty N'MS_Description', 'A boolean flag indicating whether to infer the data types of the columns in the CSV file. The default value is 0. Enabling this flag will assert the data an suggest datatypes for each column. Results are stored in table ', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'FetchDataTypes'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The column [flw].[PreIngestionXLS].[FirstRowHasHeader] is a boolean column that indicates whether the first row of the source file contains header information or not. A value of 1 indicates that the first row contains header information while a value of 0 indicates that it does not. The default value for this column is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'FirstRowHasHeader'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionXLS].[FlowID] column is an integer type column that stores a unique identifier for each data pipeline configuration for ingesting data from an Excel file to a SQL Server database table. This column is a primary key for the table, ensuring the uniqueness of each data pipeline configuration. It does not have a default value and is required for all data pipeline configurations.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'FlowID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The column [flw].[PreIngestionXLS].[FlowType] indicates the type of data flow that will be processed. It is a non-null varchar(50) field with a default value of XLS. This column is used to differentiate the type of data flow, and can be used to filter, group or categorize data flows based on their type. It can be set to different values based on the source and target data, and the processing requirements for a specific data flow.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'FlowType'
GO
EXEC sp_addextendedproperty N'MS_Description', 'FromObjectMK column stores the unique identifier for the source object being ingested. A unique Master Key (MK) is generated for each SQLFlow Object. The master key is essential in calculating lineage dependencies and the correct execution order for each batch. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'FromObjectMK'
GO
EXEC sp_addextendedproperty N'MS_Description', 'A string that specifies the earliest file creation date that should be included in the ingestion process. Only files created on or after this date will be processed. The default value is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'InitFromFileDate'
GO
EXEC sp_addextendedproperty N'MS_Description', 'A string that specifies the latest file creation date that should be included in the ingestion process. Only files created before or on this date will be processed. The default value is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'InitToFileDate'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The NoOfThreads column of the [flw].[PreIngestionXLS] table is an integer column that specifies the number of parallel threads that will be used to read data from the source file. The default value for this column is 4, which means that the data will be read using 4 parallel threads. The purpose of using multiple threads is to improve the performance of data ingestion by reading data from the file in parallel. The number of threads can be increased or decreased based on the system configuration and the size of the file being ingested.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'NoOfThreads'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The column [flw].[PreIngestionXLS].[OnErrorResume] indicates whether to continue the data pipeline processing or stop it when an error occurs. If the value is set to 1 (default), the pipeline will continue processing even if an error occurs. If the value is set to 0, the pipeline will stop processing and throw an error when an error occurs.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'OnErrorResume'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The column [flw].[PreIngestionXLS].[PostProcessOnTrg] is of the nvarchar(250) data type and allows the user to specify a T-SQL statement that will be executed on the target table after the data has been ingested. This can be useful for performing additional data transformations or updating data in the target table based on the ingested data. The default value for this column is NULL, meaning that no post-processing will be performed unless the user specifies a T-SQL statement.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'PostProcessOnTrg'
GO
EXEC sp_addextendedproperty N'MS_Description', 'preFilter column stores the SQL WHERE clause expression which is appended to the transformation view. This expression can be used to filter the data based on certain criteria, such as date ranges or specific column values.  A transformation view is automatically generated for each target table and the definition for each transformation is read from the table [flw].[PreIngestionTransfrom]. [flw].[PreIngestionTransfrom] is populated dynamically, but the values can be overridden in accordance with contents of various columns. The default value for this column is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'preFilter'
GO
EXEC sp_addextendedproperty N'MS_Description', 'PreInvokeAlias column stores an alias for a pre-registered Azure Data Factory or Automation Runbook to be executed before the ingestion process begins. This feature can be used for tasks such as data validation or preparing the source data. The desired invoke must be pre-registered in the table [flw].[Invoke] and referenced by its Alias. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'PreInvokeAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The PreProcessOnTrg column in the [flw].[PreIngestionXLS] table stores the name of the stored procedure that will be executed on the target database before the data is inserted into it. This stored procedure is used for pre-processing data before it is inserted into the target table.

If a value is not specified in this column, then no stored procedure will be executed on the target database before the data is inserted.

The default constraint for this column is NULL.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'PreProcessOnTrg'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The SearchSubDirectories column in the [flw].[PreIngestionXLS] table stores a Boolean value indicating whether to search for files in subdirectories of the source path. If set to 1 (True), files in subdirectories will also be processed. The default value for this column is 0 (False).', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'SearchSubDirectories'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionXLS].[SheetName] column is used to specify the name of the worksheet in the Excel file that contains the data to be ingested. If the Excel file contains multiple worksheets, you can specify the name of the worksheet to be ingested in this column. The name should be a string and match the actual name of the worksheet in the Excel file. If this column is left blank or null, the first worksheet in the Excel file will be ingested by default.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'SheetName'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The SheetRange column in the [flw].[PreIngestionXLS] table stores the range of cells from which data is to be imported from the Excel file. The value is a string in the format "StartCell:EndCell", where StartCell and EndCell are cell references. For example, the value "A1:B10" would import data from cells A1 to B10. SheetRange is a mandatory column', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'SheetRange'
GO
EXEC sp_addextendedproperty N'MS_Description', 'This column specifies whether the file path should be concatenated into the target column FileName_DW. The default value is 0, indicating that the error message will not include the file name.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'ShowPathWithFileName'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[PreIngestionXLS].[srcDeleteAtPath] column is a bit column that indicates whether the source file should be deleted after successful ingestion into the target database table. The default value for this column is 0, meaning that the source file will not be deleted after ingestion. If set to 1, the source file will be deleted after successful ingestion.

This column can be used to manage disk space by automatically deleting source files after they have been ingested, reducing the need for manual file cleanup.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'srcDeleteAtPath'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The column [flw].[PreIngestionXLS].[srcDeleteIngested] is a bit type column, with a default value of 0. It indicates whether the source file should be deleted after it has been ingested into the target database. If this flag is set to 1, the source file will be deleted after ingestion is complete. If this flag is set to 0, the source file will be left untouched.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'srcDeleteIngested'
GO
EXEC sp_addextendedproperty N'MS_Description', 'This column stores the name of the source Excel file from where the data is fetched. The file name can be specified as a regular expression. Only files matching the expression will be processed by the ingestion logic. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'srcFile'
GO
EXEC sp_addextendedproperty N'MS_Description', 'This column stores the path of the source Excel file(s) from where the data is fetched. This value must be a valid file path, local or Azure Data Lake Gen2 container Path. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'srcPath'
GO
EXEC sp_addextendedproperty N'MS_Description', 'This column stores a regular expression mask that is used to filter the folders in the source path. Only files within folders matching the mask will be processed', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'srcPathMask'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The column [flw].[PreIngestionXLS].[SyncSchema] is a boolean type column that determines whether the data source schema will be synchronized with the destination schema during the data transfer process. The default value is 1 which means that the schemas will be synchronized. If this value is set to 0, the schema synchronization will be skipped, and the data will be copied as-is to the destination table.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'SyncSchema'
GO
EXEC sp_addextendedproperty N'MS_Description', 'This column stores an alias for the system that is the source of the data. The value of this column is used to populate the metadata of the target table.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'SysAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', 'ToObjectMK column stores the unique identifier for the target object where the data is being ingested. A unique Master Key (MK) is generated for each SQLFlow Object. The master key is essential in calculating lineage dependencies and the correct execution order for each batch. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'ToObjectMK'
GO
EXEC sp_addextendedproperty N'MS_Description', 'This column in the [flw].[PreIngestionXLS] table stores the database schema and table name of the target table where the data is ingested. The value must be in the format of [DatabaseName].[SchemaName].[TableName]. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'trgDBSchTbl'
GO
EXEC sp_addextendedproperty N'MS_Description', 'This column in the [flw].[PreIngestionXLS] table stores the name of the SQL Server where the target table is located. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'trgServer'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The column [flw].[PreIngestionXLS].[UseSheetIndex] is a boolean flag that indicates whether to use the sheet index instead of the sheet name to read data from the Excel file. By default, this column is set to 0, which means that the sheet name is used. When this flag is set to 1, the index of the sheet is used instead.

For example, if the Excel file has three sheets named Sheet1, Sheet2, and Sheet3, and UseSheetIndex is set to 1, then the data will be read from the sheet whose index is specified in the SheetName column. If UseSheetIndex is set to 0, the data will be read from the sheet whose name is specified in the SheetName column.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'UseSheetIndex'
GO
EXEC sp_addextendedproperty N'MS_Description', 'This column in the [flw].[PreIngestionXLS] table stores the path of the directory where the source file(s) should be compressed in Zip format before ingestion. This column does not have a default value specified.', 'SCHEMA', N'flw', 'TABLE', N'PreIngestionXLS', 'COLUMN', N'zipToPath'
GO
