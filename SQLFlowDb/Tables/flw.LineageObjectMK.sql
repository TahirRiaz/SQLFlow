CREATE TABLE [flw].[LineageObjectMK]
(
[ObjectMK] [int] NOT NULL IDENTITY(1, 1),
[SysAlias] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[ObjectName] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
[ObjectType] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ObjectSource] [nvarchar] (70) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[ObjectID] [int] NULL,
[ObjectDbID] [int] NULL,
[IsFlowObject] [bit] NULL,
[NotInUse] [bit] NULL,
[IsDependencyObject] [bit] NULL CONSTRAINT [DF_LineageObjectMK_IsDependencyObject] DEFAULT ((0)),
[BeforeDependency] [nvarchar] (4000) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[AfterDependency] [nvarchar] (4000) COLLATE SQL_Latin1_General_CP1_CI_AS NULL CONSTRAINT [DF_LineageObjectMK_SPPreDep] DEFAULT ((0)),
[ObjectDef] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[RelationJson] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[CurrentIndexes] [nvarchar] (max) COLLATE SQL_Latin1_General_CP1_CI_AS NULL
)
GO
ALTER TABLE [flw].[LineageObjectMK] ADD CONSTRAINT [PK_ObjectMK] PRIMARY KEY CLUSTERED ([ObjectMK])
GO
CREATE NONCLUSTERED INDEX [NCI_ObjectMK] ON [flw].[LineageObjectMK] ([ObjectMK]) INCLUDE ([ObjectType])
GO
CREATE NONCLUSTERED INDEX [NCI_SysAliasObjectName] ON [flw].[LineageObjectMK] ([SysAlias], [ObjectName])
GO
EXEC sp_addextendedproperty N'MS_Description', 'Moving on to another SQLFlow table, namely [flw].[LineageObjectMK]. This table generates a master key for all FromObjects, ToObjects, Invoke objects, file objects, export objects and stored procedure objects. This internal master key is essential for correctly calculating the data lineage. ', 'SCHEMA', N'flw', 'TABLE', N'LineageObjectMK', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[LineageObjectMK].[AfterDependency] column is a varchar(4000) field that contains a list of objects that need to be executed after the current object. These objects are dependencies of the current object and must be executed first before the current object can be executed. The list of objects is stored as a comma-separated string, with each object represented by its master key. This column is used in the data lineage calculation to determine the execution order of objects.', 'SCHEMA', N'flw', 'TABLE', N'LineageObjectMK', 'COLUMN', N'AfterDependency'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[LineageObjectMK].[BeforeDependency] is a string field that stores a comma-separated list of objects that must be executed before the current object in the data lineage. These objects are dependencies for the current object, and their results are needed to complete the execution of the current object. This field is used in the calculation of the data lineage and is important for determining the correct execution order of objects.', 'SCHEMA', N'flw', 'TABLE', N'LineageObjectMK', 'COLUMN', N'BeforeDependency'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[LineageObjectMK].[IsDependencyObject] is a boolean field that indicates if the current object is a dependency object or not. A dependency object is an object that is used in a dependency relationship with other objects, and does not play a direct role in the data flow or calculation. For example, a stored procedure that is called by another stored procedure may be considered a dependency object, as it is not directly used in the data flow, but is still required for the calculation to take place.', 'SCHEMA', N'flw', 'TABLE', N'LineageObjectMK', 'COLUMN', N'IsDependencyObject'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[LineageObjectMK].[IsFlowObject] column is a boolean flag that indicates whether an object is a flow object or not. A flow object is an object that represents a SQL flow, such as a stored procedure, a view, or a table-valued function. When an object is a flow object, it is used to generate the data lineage for the SQL flow. This column is used internally by the system to determine the type of the object and whether it should be included in the data lineage calculation. If the value is 1, then it means the object is a flow object, otherwise, it is not.', 'SCHEMA', N'flw', 'TABLE', N'LineageObjectMK', 'COLUMN', N'IsFlowObject'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The field [flw].[LineageObjectMK].[NotInUse] in the SQLFlow table [flw].[LineageObjectMK] is a calculated field that indicates whether the current object is relevant for the data lineage calculation. If the value is set to 1, it means that the object is not used in the calculation, and if the value is set to 0, it means that the object is used in the calculation. This field is useful in cases where there are a large number of objects in the database, and not all objects are relevant for the data lineage calculation. By setting the value of this field to 1 for irrelevant objects, it is possible to speed up the calculation process and reduce the amount of resources required.', 'SCHEMA', N'flw', 'TABLE', N'LineageObjectMK', 'COLUMN', N'NotInUse'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[LineageObjectMK].[ObjectDbID] column is an integer column that stores the ID of the database where the object is located. This information is used to determine the data lineage between objects across databases.', 'SCHEMA', N'flw', 'TABLE', N'LineageObjectMK', 'COLUMN', N'ObjectDbID'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[LineageObjectMK].[ObjectDef] column contains the DDL (Data Definition Language) script for the object, including stored procedures, tables, views, functions, etc. This script is used to create the object in the database and is essential for correctly calculating the data lineage. For files and external objects, this column may be empty.', 'SCHEMA', N'flw', 'TABLE', N'LineageObjectMK', 'COLUMN', N'ObjectDef'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[LineageObjectMK].[ObjectID] is a column that stores the ID of the object in the source system (e.g. SQL Server). This is used to uniquely identify the object and retrieve metadata about it, such as its data type, schema, and other properties. The object ID is used in the data lineage calculation process to identify the source and target objects for each data flow.', 'SCHEMA', N'flw', 'TABLE', N'LineageObjectMK', 'COLUMN', N'ObjectID'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[LineageObjectMK].[ObjectMK] is a primary key column of the [flw].[LineageObjectMK] table. It is an identity column and it generates a unique master key for each object defined in SQLFlow. The master key is essential for correctly calculating the data lineage.', 'SCHEMA', N'flw', 'TABLE', N'LineageObjectMK', 'COLUMN', N'ObjectMK'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[LineageObjectMK].[ObjectName] is a non-null column in the [flw].[LineageObjectMK] table. It represents the name of the object for which the master key is being generated. This object can be a table, view, stored procedure, file object, export object, or any other object that is a part of the SQL flow.', 'SCHEMA', N'flw', 'TABLE', N'LineageObjectMK', 'COLUMN', N'ObjectName'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The column [flw].[LineageObjectMK].[ObjectSource] contains information about the source dataset of the object. ', 'SCHEMA', N'flw', 'TABLE', N'LineageObjectMK', 'COLUMN', N'ObjectSource'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[LineageObjectMK].[ObjectType] column in the SQLFlow database table flw.LineageObjectMK contains the type of the object being referenced. This could include object types like tables, views, stored procedures, and files. The value in this column helps in determining how the object should be handled and processed as part of the data lineage calculations.', 'SCHEMA', N'flw', 'TABLE', N'LineageObjectMK', 'COLUMN', N'ObjectType'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[LineageObjectMK].[SysAlias] column is used to store the system alias associated with the object.', 'SCHEMA', N'flw', 'TABLE', N'LineageObjectMK', 'COLUMN', N'SysAlias'
GO
