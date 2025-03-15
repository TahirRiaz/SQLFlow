SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

CREATE view [flw].[SysMatchKeyDeletedRowHandeling]
as 
select 
    
    json_value(value, '$.ActionType') as ActionType,
	json_value(value, '$.ActionTypeDescription') as ActionTypeDescription
	
from [flw].[SysCFG]
cross apply openjson([ParamJsonValue])
where  ParamName = 'SysMatchKeyDeletedRowHandeling'
GO
