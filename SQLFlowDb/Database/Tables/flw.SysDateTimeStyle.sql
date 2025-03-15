CREATE TABLE [flw].[SysDateTimeStyle]
(
[StyleCode] [int] NULL,
[Query] [nvarchar] (250) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DateStyle] [nvarchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[DateSample] [nvarchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
[Type] [nvarchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL
) ON [PRIMARY]
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysDateFormats] is a lookup table used for parsing a string value to a valid date time format.', 'SCHEMA', N'flw', 'TABLE', N'SysDateTimeStyle', NULL, NULL
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysDateFormats].[DateSample] represents a sample date formatted according to the corresponding [flw].[SysDateFormats].[DateStyle] value.

For example, if [flw].[SysDateFormats].[DateStyle] = "mm/dd/yy", then [flw].[SysDateFormats].[DateSample] might be "12/30/06". This provides a reference for how dates should be formatted to match the specified style.', 'SCHEMA', N'flw', 'TABLE', N'SysDateTimeStyle', 'COLUMN', N'DateSample'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysDateFormats].[DateStyle] column represents the style code for the specific date format. In this case, the example value "mm/dd/yy" represents the month/day/year format using two-digit values for month, day, and year.', 'SCHEMA', N'flw', 'TABLE', N'SysDateTimeStyle', 'COLUMN', N'DateStyle'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysDateFormats].[Query] column contains the T-SQL select statement that can be used to convert a string value into a valid date time using the CONVERT function. It includes the CONVERT function with the appropriate style code that matches the date format in the corresponding row.', 'SCHEMA', N'flw', 'TABLE', N'SysDateTimeStyle', 'COLUMN', N'Query'
GO
EXEC sp_addextendedproperty N'MS_Description', '[flw].[SysDateFormats].[StyleCode] is an integer value that represents the style code used in the T-SQL convert function to identify the specific format of a datetime string.', 'SCHEMA', N'flw', 'TABLE', N'SysDateTimeStyle', 'COLUMN', N'StyleCode'
GO
EXEC sp_addextendedproperty N'MS_Description', 'The [flw].[SysDateFormats].[Type] column specifies the data type of the date format, which can be DateTime, Time or Date.', 'SCHEMA', N'flw', 'TABLE', N'SysDateTimeStyle', 'COLUMN', N'Type'
GO
