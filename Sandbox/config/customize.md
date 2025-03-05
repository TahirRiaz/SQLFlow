# SQLFlow Framework Configuration

This guide provides instructions for customizing SQLFlow's behavior by updating its configuration settings. These configurations control how SQLFlow functions and are heavily utilized throughout the SQLFlow UI.

You can either modify these SQL Server statements directly or update the JSON files using SQLFlowUi. Both methods update the configurations stored in the `[flw].[SysCFG]` table. Customizing these values allows you to tailor SQLFlow to your specific data pipeline requirements.

## Update DefaultSettings
The `DefaultSettings` file contains default configuration values for various components of the SQLFlow framework. 
These settings are used as fallback values when specific settings are not provided for individual components or processes. 
Updating the `DefaultSettings` ensures consistent behavior across the framework.

These default values appear throughout the SQLFlow UI as pre-populated fields and dropdown selections, significantly streamlining your workflow setup. When creating new pipelines or configuring data flows, the UI automatically applies these defaults, reducing manual entry and potential errors. Customizing these values to match your environment's common patterns will make SQLFlow more intuitive and efficient for your team to use.

 ### Default Settings Description
- `DefaultBatch`: The default batch name for ETL processes.
- `DefaultSysAlias`: The default system alias used for logging and auditing.
- `DefaultPreIngServicePrincipalAlias`: The default service principal alias for pre-ingestion tasks.
- `DefaultPreIngSrcPath`: The default source path for pre-ingestion tasks.
- `DefaultPreIngSrcFileName`: The default source file name pattern for pre-ingestion tasks.
- `DefaultPreIngTrgServer`: The default target server for pre-ingestion tasks.
- `DefaultPreIngTrgDbName`: The default target database name for pre-ingestion tasks.
- `DefaultPreIngTrgSchema`: The default target schema for pre-ingestion tasks.
- `DefaultPreIngTrgTable`: The default target table for pre-ingestion tasks.
- `DefaultSrcServicePrincipalAlias`: The default service principal alias for source data access.
- `DefaultSrcServer`: The default source server for data extraction.
- `DefaultSrcDbName`: The default source database name for data extraction.
- `DefaultSrcSchema`: The default source schema for data extraction.
- `DefaultSrcObject`: The default source object (e.g., view) for data extraction.
- `DefaultTrgServer`: The default target server for data loading.
- `DefaultTrgDbName`: The default target database name for data loading.
- `DefaultTrgSchema`: The default target schema for data loading.
- `DefaultTrgTable`: The default target table for data loading.
- `DefaultTrgServicePrincipalAlias`: The default service principal alias for target data access.
- `DefaultTrgPath`: The default target path for data loading.
- `DefaultTrgFileName`: The default target file name for data loading.

```sql
UPDATE [flw].[SysCFG]
SET [ParamJsonValue] = '{
 "Row1": {
   "DefaultBatch": "Init",
   "DefaultSysAlias": "InitSubGroup",
   "DefaultPreIngServicePrincipalAlias": "SQLFlowApp",
   "DefaultPreIngSrcPath": "raw/",
   "DefaultPreIngSrcFileName": ".*FixedPartFromYourFileName(.*?).",
   "DefaultPreIngTrgServer": "pre_prod",
   "DefaultPreIngTrgDbName": "[dw-pre-prod]",
   "DefaultPreIngTrgSchema": "[pre]",
   "DefaultPreIngTrgTable": "[YourTargetTable]",
   "DefaultSrcServicePrincipalAlias": "SQLFlowApp",
   "DefaultSrcServer": "pre_prod",
   "DefaultSrcDbName": "[dw-pre-prod]",
   "DefaultSrcSchema": "[pre]",
   "DefaultSrcObject": "[v_YourSourceObject]",
   "DefaultTrgServer": "dwh_prod",
   "DefaultTrgDbName": "[dw-dwh-prod]",
   "DefaultTrgSchema": "[arc]",
   "DefaultTrgTable": "[YourTargetTable]",
   "DefaultTrgServicePrincipalAlias": "SQLFlowAppExport",
   "DefaultTrgPath": "raw/",
   "DefaultTrgFileName": "YourTargetFileName"
 }
}'
WHERE [ParamName] = 'DefaultInitPipelineValues';
```



## Update SysJwtSettings
The `SysJwtSettings` file contains settings related to JSON Web Tokens (JWT) used for authentication in the SQLFlow.

### JWT Settings Description
- `SecretKey`: The secret key used for signing and verifying JWTs.
- `Issuer`: The issuer of the JWTs.
- `Audience`: The intended audience for the JWTs.
- `ExpireMinutes`: The expiration time in minutes for short-lived JWTs.
- `ExpireYears4LongLived`: The expiration time in years for long-lived JWTs.

```sql
-- Update SysJwtSettings
UPDATE [flw].[SysCFG]
SET [ParamJsonValue] = '{
 "Row1": {
   "SecretKey": "X2ddasdmU8Q~72PTDpSTWdA#>Mzh)0wi[wiPvQX4DRWasdfKSVJm6Y5Q4b~3asdf",
   "Issuer": "SQLFlowUi",
   "Audience": "SQLFlowUiUsers",
   "ExpireMinutes": 300,
   "ExpireYears4LongLived": 2
 }
}'
WHERE [ParamName] = 'SysJwtSettings';
```

## Update SysJwtAuthUser
The `SysJwtAuthUser` file contains user credentials for JWT authentication used by SQLFlowUi.

### JWT Auth User Description
- `JwtAuthUserName`: The username for JWT authentication.
- `JwtAuthUserPwd`: The password for JWT authentication.

```sql 
-- Update SysJwtAuthUser
UPDATE [flw].[SysCFG]
SET [ParamJsonValue] = '{
 "Row1": {
   "JwtAuthUserName": "authuser@xyz.no",
   "JwtAuthUserPwd": "cdA#>Mzh)0wiabced"
 }
}'
WHERE [ParamName] = 'SysJwtAuthUser';
```

## Update WebApi
The `WebApi` file contains settings related to the Web API endpoints used by SQLFlowUi.

### Web API Endpoint Description
- `WebApiUrl`: The base URL for the SQLFlow Web API.
- `Login`: The endpoint for user login.
- `Logout`: The endpoint for user logout.
- `CheckAuth`: The endpoint for checking user authentication.
- `ValidateToken`: The endpoint for validating JWTs.
- `CancelProcess`: The endpoint for canceling ETL processes.
- `Assertion`: The endpoint for executing assertions.
- `HealthCheck`: The endpoint for performing health checks.
- `SourceControl`: The endpoint for source control operations.
- `LineageMap`: The endpoint for executing lineage mapping.
- `FlowProcess`: The endpoint for executing flow processes.
- `FlowNode`: The endpoint for executing flow nodes.
- `FlowBatch`: The endpoint for executing flow batches.
- `TrgTblSchema`: The endpoint for executing target table schema operations.
- `DetectUniqueKey`: The endpoint for detecting unique keys.

```sql
UPDATE [flw].[SysCFG]
SET [ParamJsonValue] = '{
 "Row1": {
   "WebApiUrl": "https://api_host_on_azure.azurewebsites.net/api/",
   "WebUiUrl": "https://ui_host_on_azure.azurewebsites.net/",
   "Login": "Login",
   "Logout": "Logout",
   "CheckAuth": "CheckAuth",
   "ValidateToken": "ValidateToken",
   "CancelProcess": "CancelProcess",
   "Assertion": "ExecAssertion",
   "HealthCheck": "ExecHealthCheck",
   "SourceControl": "ExecSourceControl",
   "LineageMap": "ExecLineageMap",
   "FlowProcess": "ExecFlowProcess",
   "FlowNode": "ExecFlowNode",
   "FlowBatch": "ExecFlowBatch",
   "TrgTblSchema": "ExecTrgTblSchema",
   "DetectUniqueKey": "ExecDetectUniqueKey"
 }
}'
WHERE [ParamName] = 'WebApi';
```

## Update SysSmtpServer
The `SysSmtpServer` file contains settings for the SMTP server used for sending emails (Not In Use).

### SMTP Server Settings Description
- `Host`: The hostname or IP address of the SMTP server.
- `Port`: The port number used for SMTP communication.
- `Ssl`: Indicates whether to use SSL/TLS encryption for the SMTP connection.
- `User`: The username for authenticating with the SMTP server.
- `Password`: The password for authenticating with the SMTP server.


```sql
UPDATE [flw].[SysCFG]
SET [ParamJsonValue] = '{
 "Row1": {
   "Host": "smtp.office365.com",
   "Port": 587,
   "Ssl": true,
   "User": "demo@user.io",
   "Password": "@SetYourPassword"
 }
}'
WHERE [ParamName] = 'SysSmtpServer';
```



## SysEventTaskSettings
The `SysEventTaskSettings` file contains settings for event-driven tasks triggered by Azure Event Grid.

- `refreshLineageAfterMinutes`: The interval in minutes for refreshing the lineage information.
- `maxParallelTasks`: The maximum number of parallel tasks allowed.
- `maxParallelSteps`: The maximum number of parallel steps allowed within a task.

```sql
UPDATE [flw].[SysCFG]
SET [ParamJsonValue] = '{
 "Row1": {
   "refreshLineageAfterMinutes": 120,
   "maxParallelTasks": 1,
   "maxParallelSteps": 1
 }
}'
```

### Event Task Settings Description
- `refreshLineageAfterMinutes`: The interval in minutes for refreshing the lineage information.
- `maxParallelTasks`: The maximum number of parallel tasks allowed.
- `maxParallelSteps`: The maximum number of parallel steps allowed within a task.


These configuration files provide the necessary settings for various components of the SQLFlow framework, 
including authentication, Web API endpoints, SMTP server, and event-driven task settings. 
By modifying these JSON files, you can customize the behavior and configuration of SQLFlow to suit your specific requirements.