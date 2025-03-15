SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
/*
  ##################################################################################################################################################
  -- Name				:   [flw].[DropProperties]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure generates a set of SQL statements to drop all extended properties in a SQL Server database.
							Extended properties are metadata that can be added to various database objects (tables, columns, views, stored procedures, etc.)
							to store additional information about them.
  -- Summary			:	This stored procedure generates SQL statements to drop extended properties from the following objects:

							Tables
							Columns
							Check constraints
							Default constraints
							Views
							Stored procedures
							Foreign key constraints
							Primary key constraints
							Table triggers
							User-defined function parameters
							Stored procedure parameters
							Database-level properties
							Schema-level properties
							Database files
							Filegroups
							The generated SQL statements use the sp_dropextendedproperty stored procedure to drop the extended properties. 
							To execute the statements and actually drop the extended properties, you would need to execute the generated SQL statements.

							Note that this stored procedure only generates SQL statements to drop the extended properties and does not execute them. 
							To use this stored procedure, you can execute it like this:

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[DropProperties]
AS
BEGIN
    SELECT 'EXEC sp_dropextendedproperty
@name = ''' + xp.name + '''
,@level0type = ''schema''
,@level0name = ''' + OBJECT_SCHEMA_NAME(xp.major_id) + '''
,@level1type = ''table''
,@level1name = ''' + OBJECT_NAME(xp.major_id) + ''''
    FROM sys.extended_properties xp
        JOIN sys.tables t
            ON xp.major_id = t.object_id
    WHERE xp.class_desc = 'OBJECT_OR_COLUMN'
          AND xp.minor_id = 0
    UNION
    --columns
    SELECT 'EXEC sp_dropextendedproperty
@name = ''' + sys.extended_properties.name + '''
,@level0type = ''schema''
,@level0name = ''' + OBJECT_SCHEMA_NAME(extended_properties.major_id)
           + '''
,@level1type = ''table''
,@level1name = ''' + OBJECT_NAME(extended_properties.major_id) + '''
,@level2type = ''column''
,@level2name = ''' + columns.name + ''''
    FROM sys.extended_properties
        JOIN sys.columns
            ON COLUMNS.object_id = extended_properties.major_id
               AND COLUMNS.column_id = extended_properties.minor_id
    WHERE extended_properties.class_desc = 'OBJECT_OR_COLUMN'
          AND extended_properties.minor_id > 0
    UNION
    --check constraints
    SELECT 'EXEC sp_dropextendedproperty
@name = ''' + xp.name + '''
,@level0type = ''schema''
,@level0name = ''' + OBJECT_SCHEMA_NAME(xp.major_id) + '''
,@level1type = ''table''
,@level1name = ''' + OBJECT_NAME(cc.parent_object_id) + '''
,@level2type = ''constraint''
,@level2name = ''' + cc.name + ''''
    FROM sys.extended_properties xp
        JOIN sys.check_constraints cc
            ON xp.major_id = cc.object_id
    UNION
    --check constraints
    SELECT 'EXEC sp_dropextendedproperty
@name = ''' + xp.name + '''
,@level0type = ''schema''
,@level0name = ''' + OBJECT_SCHEMA_NAME(xp.major_id) + '''
,@level1type = ''table''
,@level1name = ''' + OBJECT_NAME(cc.parent_object_id) + '''
,@level2type = ''constraint''
,@level2name = ''' + cc.name + ''''
    FROM sys.extended_properties xp
        JOIN sys.default_constraints cc
            ON xp.major_id = cc.object_id
    UNION
    --views
    SELECT 'EXEC sp_dropextendedproperty
@name = ''' + xp.name + '''
,@level0type = ''schema''
,@level0name = ''' + OBJECT_SCHEMA_NAME(xp.major_id) + '''
,@level1type = ''view''
,@level1name = ''' + OBJECT_NAME(xp.major_id) + ''''
    FROM sys.extended_properties xp
        JOIN sys.views t
            ON xp.major_id = t.object_id
    WHERE xp.class_desc = 'OBJECT_OR_COLUMN'
          AND xp.minor_id = 0
    UNION
    --sprocs
    SELECT 'EXEC sp_dropextendedproperty
@name = ''' + xp.name + '''
,@level0type = ''schema''
,@level0name = ''' + OBJECT_SCHEMA_NAME(xp.major_id) + '''
,@level1type = ''procedure''
,@level1name = ''' + OBJECT_NAME(xp.major_id) + ''''
    FROM sys.extended_properties xp
        JOIN sys.procedures t
            ON xp.major_id = t.object_id
    WHERE xp.class_desc = 'OBJECT_OR_COLUMN'
          AND xp.minor_id = 0
    UNION
    --FKs
    SELECT 'EXEC sp_dropextendedproperty
@name = ''' + xp.name + '''
,@level0type = ''schema''
,@level0name = ''' + OBJECT_SCHEMA_NAME(xp.major_id) + '''
,@level1type = ''table''
,@level1name = ''' + OBJECT_NAME(cc.parent_object_id) + '''
,@level2type = ''constraint''
,@level2name = ''' + cc.name + ''''
    FROM sys.extended_properties xp
        JOIN sys.foreign_keys cc
            ON xp.major_id = cc.object_id
    UNION
    --PKs
    SELECT 'EXEC sys.sp_dropextendedproperty @level0type = N''SCHEMA'', @level0name = [' + SCH.name
           + '], @level1type = ''TABLE'', @level1name = [' + TBL.name
           + '] , @level2type = ''CONSTRAINT'', @level2name = [' + SKC.name + '] ,@name = '''
           + REPLACE(CAST(SEP.name AS NVARCHAR(300)), '''', '''''') + ''''
    FROM sys.tables TBL
        INNER JOIN sys.schemas SCH
            ON TBL.schema_id = SCH.schema_id
        INNER JOIN sys.extended_properties SEP
            INNER JOIN sys.key_constraints SKC
                ON SEP.major_id = SKC.object_id
            ON TBL.object_id = SKC.parent_object_id
    WHERE SKC.type_desc = N'PRIMARY_KEY_CONSTRAINT'
    UNION
    --Table triggers
    SELECT 'EXEC sys.sp_dropextendedproperty @level0type = N''SCHEMA'', @level0name = [' + SCH.name
           + '], @level1type = ''TABLE'', @level1name = [' + TBL.name
           + '] , @level2type = ''TRIGGER'', @level2name = [' + TRG.name + '] ,@name = '''
           + REPLACE(CAST(SEP.name AS NVARCHAR(300)), '''', '''''') + ''''
    FROM sys.tables TBL
        INNER JOIN sys.triggers TRG
            ON TBL.object_id = TRG.parent_id
        INNER JOIN sys.extended_properties SEP
            ON TRG.object_id = SEP.major_id
        INNER JOIN sys.schemas SCH
            ON TBL.schema_id = SCH.schema_id
    UNION
    --UDF params
    SELECT 'EXEC sys.sp_dropextendedproperty @level0type = N''SCHEMA'', @level0name = [' + SCH.name
           + '], @level1type = ''FUNCTION'', @level1name = [' + OBJ.name
           + '] , @level2type = ''PARAMETER'', @level2name = [' + PRM.name + '] ,@name = '''
           + REPLACE(CAST(SEP.name AS NVARCHAR(300)), '''', '''''') + ''''
    FROM sys.extended_properties SEP
        INNER JOIN sys.objects OBJ
            ON SEP.major_id = OBJ.object_id
        INNER JOIN sys.schemas SCH
            ON OBJ.schema_id = SCH.schema_id
        INNER JOIN sys.parameters PRM
            ON SEP.major_id = PRM.object_id
               AND SEP.minor_id = PRM.parameter_id
    WHERE SEP.class_desc = N'PARAMETER'
          AND OBJ.type IN ( 'FN', 'IF', 'TF' )
    UNION
    --sp params
    SELECT 'EXEC sys.sp_dropextendedproperty @level0type = N''SCHEMA'', @level0name = [' + SCH.name
           + '], @level1type = ''PROCEDURE'', @level1name = [' + SPR.name
           + '] , @level2type = ''PARAMETER'', @level2name = [' + PRM.name + '] ,@name = '''
           + REPLACE(CAST(SEP.name AS NVARCHAR(300)), '''', '''''') + ''''
    FROM sys.extended_properties SEP
        INNER JOIN sys.procedures SPR
            ON SEP.major_id = SPR.object_id
        INNER JOIN sys.schemas SCH
            ON SPR.schema_id = SCH.schema_id
        INNER JOIN sys.parameters PRM
            ON SEP.major_id = PRM.object_id
               AND SEP.minor_id = PRM.parameter_id
    WHERE SEP.class_desc = N'PARAMETER'
    UNION
    --DB
    SELECT 'EXEC sys.sp_dropextendedproperty @name = ''' + REPLACE(CAST(SEP.name AS NVARCHAR(300)), '''', '''''')
           + ''''
    FROM sys.extended_properties SEP
    WHERE class_desc = N'DATABASE'
    UNION
    --schema
    SELECT 'EXEC sys.sp_dropextendedproperty @level0type = N''SCHEMA'', @level0name = [' + SCH.name + '] ,@name = '''
           + REPLACE(CAST(SEP.name AS NVARCHAR(300)), '''', '''''') + ''''
    FROM sys.extended_properties SEP
        INNER JOIN sys.schemas SCH
            ON SEP.major_id = SCH.schema_id
    WHERE SEP.class_desc = N'SCHEMA'
    UNION
    --DATABASE_FILE
    SELECT 'EXEC sys.sp_dropextendedproperty @level0type = N''FILEGROUP'', @level0name = [' + DSP.name
           + '], @level1type = ''LOGICAL FILE NAME'', @level1name = ' + DBF.name + ' ,@name = '''
           + REPLACE(CAST(SEP.name AS NVARCHAR(300)), '''', '''''') + ''''
    FROM sys.extended_properties SEP
        INNER JOIN sys.database_files DBF
            ON SEP.major_id = DBF.file_id
        INNER JOIN sys.data_spaces DSP
            ON DBF.data_space_id = DSP.data_space_id
    WHERE SEP.class_desc = N'DATABASE_FILE'
    UNION
    --filegroup
    SELECT 'EXEC sys.sp_dropextendedproperty @level0type = N''FILEGROUP'', @level0name = [' + DSP.name
           + '] ,@name = ''' + REPLACE(CAST(SEP.name AS NVARCHAR(300)), '''', '''''') + ''''
    FROM sys.extended_properties SEP
        INNER JOIN sys.data_spaces DSP
            ON SEP.major_id = DSP.data_space_id
    WHERE DSP.type_desc = 'ROWS_FILEGROUP';

END;
GO
