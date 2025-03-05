CREATE TABLE [flw].[LineageEdge]
(
[RecID] [int] NOT NULL IDENTITY(1, 1),
[DataSet] [int] NOT NULL,
[FlowID] [int] NOT NULL,
[FlowType] [varchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[FromObjectMK] [int] NULL,
[ToObjectMK] [int] NULL,
[FromObject] [varchar] (500) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ToObject] [varchar] (500) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Dependency] [varchar] (8000) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[IsAfterDependency] [bit] NULL,
[Circular] [bit] NULL CONSTRAINT [DF_LineageEdge_Circular] DEFAULT ((0)),
[CreateDate] [datetime] NULL CONSTRAINT [DF_LineageEdge_CreateDate] DEFAULT (getdate())
)
GO
ALTER TABLE [flw].[LineageEdge] ADD CONSTRAINT [PK_LineageEdge] PRIMARY KEY CLUSTERED ([RecID])
GO
EXEC sp_addextendedproperty N'MS_Description', 'Moving on to another table of [flw].[LineageEdge]. Data flows defined in various SQLFlow tables are consolidated into a graph table [flw].[LineageEdge]. Data lineage and execution order is calculated for each individual flows. This is an internal table and the go to place for understanding how the data linage is built', 'SCHEMA', N'flw', 'TABLE', N'LineageEdge', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', 'The CreateDate column in the [flw].[LineageEdge] table is a DATETIME column that stores the date and time when the record was created. It is used to track the creation time of each lineage edge record in the table.', 'SCHEMA', N'flw', 'TABLE', N'LineageEdge', 'COLUMN', N'CreateDate'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[LineageEdge].[DataSet] column in the flw.LineageEdge table is a nullable integer column that holds the identifier of the dataset to which the corresponding flow belongs. It is used to associate a flow with a particular dataset, and it can be used to group and filter flows based on the dataset they are associated with.', 'SCHEMA', N'flw', 'TABLE', N'LineageEdge', 'COLUMN', N'DataSet'
GO
EXEC sp_addextendedproperty N'MS_Description', 'That''s correct! In the [flw].[LineageEdge] table, the [Dependency] column stores a comma-separated list of dependency objects for the FromObject column. The dependency objects can be tables, views, stored procedures, etc., and they represent the objects that need to be executed before the FromObject can be executed.', 'SCHEMA', N'flw', 'TABLE', N'LineageEdge', 'COLUMN', N'Dependency'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[LineageEdge].[FlowID] column is an integer column in the SQLFlow table [flw].[LineageEdge], which stores the unique identifier of the flow to which a given edge belongs. This column is used to consolidate data flows defined in various SQLFlow tables into a graph table and calculate data lineage and execution order for each individual flow.', 'SCHEMA', N'flw', 'TABLE', N'LineageEdge', 'COLUMN', N'FlowID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[LineageEdge].[FlowType] column in the SQLFlow table [flw].[LineageEdge] stores a string value representing the type of data flow for a given record in the table. This can include values such as ado, csv, exp, ing, inv, sp, xls, xml, or any other custom type that has been defined for a particular SQLFlow implementation. The purpose of this column is to allow for easy filtering and categorization of data flows in the SQLFlow system. Dependency edges has FlowType as blank. ', 'SCHEMA', N'flw', 'TABLE', N'LineageEdge', 'COLUMN', N'FlowType'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[LineageEdge].[FromObject]', 'SCHEMA', N'flw', 'TABLE', N'LineageEdge', 'COLUMN', N'FromObject'
GO
EXEC sp_addextendedproperty N'MS_Description', 'FromObjectMK column stores the unique identifier for the source object. A unique Master Key (MK) is generated for each SQLFlow Object.', 'SCHEMA', N'flw', 'TABLE', N'LineageEdge', 'COLUMN', N'FromObjectMK'
GO
EXEC sp_addextendedproperty N'MS_Description', 'Yes, that is correct. The IsAfterDependency column in the flw.LineageEdge table indicates whether the objects in the Dependency column should be executed before or after the current object. If the value is 1, it means that the dependencies should be executed before the current object, and if the value is 0, it means that the dependencies should be executed after the current object.', 'SCHEMA', N'flw', 'TABLE', N'LineageEdge', 'COLUMN', N'IsAfterDependency'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[LineageEdge].[RecID] column is an identity column that uniquely identifies each row in the table. It is used as the primary key of the table.', 'SCHEMA', N'flw', 'TABLE', N'LineageEdge', 'COLUMN', N'RecID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[LineageEdge].[ToObject] column contains the name or identifier of the object that the current object depends on. It can be a table name, view name, stored procedure name, or any other type of database object. The data in this column helps establish the data lineage and the execution order of objects in a SQLFlow data pipeline.', 'SCHEMA', N'flw', 'TABLE', N'LineageEdge', 'COLUMN', N'ToObject'
GO
EXEC sp_addextendedproperty N'MS_Description', 'ToObjectMK column stores the unique identifier for the target object.', 'SCHEMA', N'flw', 'TABLE', N'LineageEdge', 'COLUMN', N'ToObjectMK'
GO
