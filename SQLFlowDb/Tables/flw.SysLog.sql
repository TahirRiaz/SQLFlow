CREATE TABLE [flw].[SysLog]
(
[FlowID] [int] NOT NULL,
[FlowType] [varchar] (25) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ProcessShort] [nvarchar] (500) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[StartTime] [datetime] NULL,
[EndTime] [datetime] NULL,
[DurationFlow] [int] NULL,
[DurationPre] [int] NULL,
[DurationPost] [int] NULL,
[Fetched] [int] NULL,
[Inserted] [int] NULL,
[Updated] [int] NULL,
[Deleted] [int] NULL,
[Success] [bit] NULL,
[FlowRate] [decimal] (18, 2) NULL,
[NoOfThreads] [int] NULL,
[SysAlias] [varchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Batch] [varchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Process] [nvarchar] (500) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SrcAlias] [varchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[TrgAlias] [varchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FileName] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FileSize] [int] NULL,
[FileDate] [varchar] (15) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FileDateHist] [varchar] (15) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ExecMode] [varchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SelectCmd] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[InsertCmd] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[UpdateCmd] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DeleteCmd] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[RuntimeCmd] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[CreateCmd] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[SurrogateKeyCmd] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ErrorInsert] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ErrorUpdate] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ErrorDelete] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ErrorRuntime] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FromObjectDef] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ToObjectDef] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PreProcessOnTrgDef] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[PostProcessOnTrgDef] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DataTypeWarning] [xml] NULL,
[ColumnWarning] [xml] NULL,
[AssertRowCount] [nvarchar] (2000) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[NextExportDate] [date] NULL,
[NextExportValue] [int] NULL,
[WhereIncExp] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[WhereDateExp] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[WhereXML] [xml] NULL,
[TrgIndexes] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[TraceLog] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[InferDatatypeCmd] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL
)
GO
ALTER TABLE [flw].[SysLog] ADD CONSTRAINT [PK_SysLog] PRIMARY KEY CLUSTERED ([FlowID])
GO
CREATE NONCLUSTERED INDEX [NCI_EndTime] ON [flw].[SysLog] ([EndTime])
GO
CREATE NONCLUSTERED INDEX [NCI_FlowID] ON [flw].[SysLog] ([FlowID]) INCLUDE ([Success], [EndTime])
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysLog] is a logging table in the SQLFlow framework that stores information about the execution of various processes within the framework. It stores data related to the process, such as start and end time, duration, fetched data, inserted, updated, and deleted data, and other relevant information. This table provides key input for incremental processes and is essential for monitoring and debugging of the framework. It also logs details of various commands such as select, insert, update, delete, create, and surrogate key commands. The table includes columns for tracking errors and warnings, indexes used, file information, and other details that are useful for tracking the performance and progress of various SQLFlow processes.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', 'Reserved for future use', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'AssertRowCount'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The Batch column in the flw.SysLog table is used to store the name or identifier of the batch or job that the flow belongs to. This can be useful for tracking and organizing different sets of flows within the SQLFlow framework.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'Batch'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLog].[ColumnWarning] column is used to log any warnings related to columns during execution. For example, it may log a warning if a source column is not found in the target table, or if a column is found in the target table but not included in the source. The warnings can be used to investigate any unexpected behavior during execution.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'ColumnWarning'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLog].[CreateCmd] column contains the SQL command used to create the target table of the executed process. It is logged in the SysLog table for reference purposes.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'CreateCmd'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysLog].[DataTypeWarning] is a column that logs any warnings related to mismatch between source and target column data types, which can be vital for correct checksum calculations. It stores the warning message in an XML format.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'DataTypeWarning'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLog].[DeleteCmd] column contains the dynamically generated DELETE command that was used during the execution of a specific process. It can be used for debugging and audit purposes to understand exactly which rows were deleted during a specific execution.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'DeleteCmd'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLog].[Deleted] column in the flw.SysLog table stores the number of rows deleted during the execution of a particular SQLFlow process. This is one of the many columns that store execution-related statistics and other relevant information for monitoring and debugging purposes.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'Deleted'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLog].[DurationFlow] column stores the duration of the flow execution in seconds.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'DurationFlow'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysLog].[DurationPost] is a column in the SysLog table that stores the duration of the post-process operation in milliseconds. The post-process operation is the final step in a flow, after all the data has been processed and loaded into the target system.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'DurationPost'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLog].[DurationPre] column in the flw.SysLog table represents the duration of the pre-execution process in seconds.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'DurationPre'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The EndTime column in the flw.SysLog table records the date and time when the execution of a process completed.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'EndTime'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLog].[ErrorDelete] column in the flw.SysLog table is used to store any errors encountered during delete operations. It is a text field and can hold large amounts of error message data. This field can be useful for troubleshooting and identifying the source of any issues that may arise during the execution of delete operations within the SQLFlow framework.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'ErrorDelete'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLog].[ErrorInsert] column in the flw.SysLog table is used to capture any error messages that may occur during an INSERT operation. If there are no errors, this column will be NULL. If an error occurs, the error message will be captured in this column for debugging and troubleshooting purposes.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'ErrorInsert'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLog].[ErrorRuntime] column stores any runtime errors that occur during the execution of a SQLFlow process. If an error occurs, the error message is stored in this column for further analysis and debugging.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'ErrorRuntime'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLog].[ErrorUpdate] column contains the error message that occurred during an update operation. It is used for logging and debugging purposes.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'ErrorUpdate'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLog].[ExecMode] column in the flw.SysLog table is an optional tag provided at execution time that can be used to differentiate between various types of executions, such as scheduled, manual, etc. Stats can be generated based on the tag to better understand the behavior of SQLFlow and its executions.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'ExecMode'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The Fetched column in [flw].[SysLog] represents the number of rows fetched from a data source during an execution.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'Fetched'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLog].[FileDate] column stores the last modified date of the file that is being processed. YYYYMMDDHHMMSS, It is a VARCHAR(15) data type column.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'FileDate'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLog].[FileName] column in the flw.SysLog table contains the name of the file that was processed during the execution of the corresponding SQLFlow job.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'FileName'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLog].[FileSize] column in the SQLFlow framework logs the size of the file that was processed in the execution.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'FileSize'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysLog].[FlowID] is a primary key column in the flw.SysLog table, which is a central logging table for all executions within the SQLFlow framework. It stores a unique identifier for each execution, and serves as the primary means of tracking and analyzing execution history for SQLFlow processes.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'FlowID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLog].[FlowRate] column stores the number of rows per second for the execution. It is calculated by dividing the total number of rows affected (fetched, inserted, updated or deleted) by the total execution time. The result is rounded to two decimal places and stored as a decimal(18,2) value.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'FlowRate'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysLog].[FlowType]', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'FlowType'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLog].[FromObjectDef] column in the [flw].[SysLog] table contains the Data Definition Language (DDL) script for the source object used in the execution of the flow. This column provides a snapshot of the source object at the time of execution and can be used for auditing and debugging purposes.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'FromObjectDef'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The InsertCmd column in the flw.SysLog table contains the SQL command used to insert data into a target table during the execution of a flow in SQLFlow. It stores the actual insert statement that was executed during the process. This column can be useful for auditing purposes or for debugging errors in the data flow.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'InsertCmd'
GO
EXEC sp_addextendedproperty N'MS_Description', 'This column in the [flw].[SysLog] table tracks the number of rows inserted during a SQLFlow execution for a given FlowID.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'Inserted'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLog].[NextExportDate] column represents the date of the next export for the corresponding flow. This column is used in the incremental process to determine the date range for the next export.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'NextExportDate'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The NextExportValue column in [flw].[SysLog] represents the next key value to be used for incremental loads. It is used to keep track of the last processed key value in the source data, so that only new or updated data is extracted and loaded during subsequent runs.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'NextExportValue'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysLog].[NoOfThreads]', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'NoOfThreads'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysLog].[PostProcessOnTrgDef] is a column in the [flw].[SysLog] table which contains the DDL script of the stored procedure or user-defined function that is called to perform post-processing on the target table after the data has been loaded. This column serves as a reference to the relevant object in the database.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'PostProcessOnTrgDef'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLog].[PreProcessOnTrgDef] column contains the DDL script for a pre-process stored procedure that can be executed before the execution of the target stored procedure.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'PreProcessOnTrgDef'
GO
EXEC sp_addextendedproperty N'MS_Description', 'This column provides information about the process being executed and which objects are involved in the process. It can be useful for monitoring and debugging the execution of a particular flow.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'Process'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLog].[RuntimeCmd] column in the SysLog table of SQLFlow contains the dynamically generated SQL commands that are executed during runtime. This can include SQL commands generated by SQLFlow during ETL processes, data transformations, or data processing operations. The RuntimeCmd column can be useful for debugging and troubleshooting, as it provides a record of the exact SQL commands that were executed during a particular run of a process.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'RuntimeCmd'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysLog].[SelectCmd] contains the SELECT statement used to retrieve data from a source system. This column is often populated when data is fetched from a database or file. It can be used for debugging purposes or to understand the data retrieval process.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'SelectCmd'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysLog].[StartTime] column stores the timestamp of the start time of the execution of a flow in the SQLFlow framework.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'StartTime'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLog].[Success] column in the flw.SysLog table is a bit column that indicates whether or not the execution of a particular flow was successful. A value of 1 indicates success, while 0 indicates failure. This column is useful for monitoring and debugging purposes, as it can help identify issues that may be causing flows to fail.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'Success'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLog].[SurrogateKeyCmd] column in the [flw].[SysLog] table contains the SQL command used to generate or update the surrogate key in the destination table during the data integration process. The surrogate key is a system-generated unique identifier for each row in the target table that is used for data integrity and consistency purposes. This column is important for tracking the generation and maintenance of surrogate keys over time, and can be used for auditing and debugging purposes.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'SurrogateKeyCmd'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysLog].[SysAlias] is a column in the flw.SysLog table which indicates the data source or system alias used for the execution.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'SysAlias'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysLog].[ToObjectDef] column in the SysLog table is used to store the DDL script for the target object. The target object is the destination table/view/procedure that is used in the execution process. The DDL script is usually generated by SQLFlow and it describes the schema and structure of the target object. The ToObjectDef column helps in identifying the target object, along with its properties, and can be used for auditing and debugging purposes.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'ToObjectDef'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLog].[TrgIndexes] column is used to store a list of indexes on the target table that have been created as part of the ETL process. This information can be useful for performance tuning and troubleshooting purposes.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'TrgIndexes'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLog].[UpdateCmd] column in the flw.SysLog table stores the SQL statement used to update data during an execution of the SQLFlow framework. It is logged for monitoring and debugging purposes.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'UpdateCmd'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysLog].[Updated] column indicates the number of rows that were updated during the execution of a SQLFlow process.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'Updated'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The WhereDateExp column in the flw.SysLog table is used to store a WHERE clause expression for filtering rows by a specific date range. It can be used for incremental loads, where only rows that have been modified or added since a certain date need to be processed. The expression can include operators such as >= (greater than or equal to) and <= (less than or equal to) to define the date range.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'WhereDateExp'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysLog].[WhereIncExp] contains the WHERE clause used for incremental loading. It is used to filter the rows in the source table that should be considered for incremental processing.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'WhereIncExp'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The WhereXML column in the [flw].[SysLog] table contains an XML string that defines the filtering criteria to be applied to the source data during an incremental load. This XML can be used to construct the WHERE clause of the SELECT statement that extracts the data from the source table.', 'SCHEMA', N'flw', 'TABLE', N'SysLog', 'COLUMN', N'WhereXML'
GO
