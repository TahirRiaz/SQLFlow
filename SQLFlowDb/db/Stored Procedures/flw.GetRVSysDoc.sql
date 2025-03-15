SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE PROCEDURE [flw].[GetRVSysDoc] @ObjectName NVARCHAR(255) = ''
AS
BEGIN
    SELECT '[' + OBJECT_SCHEMA_NAME(object_id) + '].' + '[' + OBJECT_NAME(object_id) + ']' ObjectName
    FROM sys.objects
    WHERE is_ms_shipped = 0
          AND type_desc IN ( 'USER_TABLE', 'VIEW', 'SQL_STORED_PROCEDURE', 'SQL_TABLE_VALUED_FUNCTION',
                             'SQL_SCALAR_FUNCTION', 'SEQUENCE_OBJECT', 'SYNONYM'
                           )
          AND object_id = CASE WHEN LEN(@ObjectName) > 0 THEN  OBJECT_ID(@ObjectName) ELSE object_id END 
--and [NAME] = 'FlowDS'
END;
GO
