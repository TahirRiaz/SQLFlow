using Microsoft.Data.SqlClient;

namespace SqlFlow.SqlServer.FullMode;

/// <summary>
/// Provisions the V3-native full-mode control-plane tables idempotently (the same EnsureSchema / swallow-2714
/// pattern the run log uses). It deliberately replaces the legacy secret-resting schema: flw.DataSource holds a
/// secretless ConnectionRef (a passwordless connection string or a <c>${...}</c> reference, enforced by a CHECK
/// at the INSERT trust boundary), flw.CredentialProfile holds only secret REFERENCES (no plaintext client
/// secret), and the flow tables (flw.Ingestion / flw.IngestionVirtual) keep the legacy column shape verbatim so
/// the existing lossless mapper reads them, with no secret ever living in flow storage. The run log creates
/// flw.SysLog / flw.SysStats itself, so they are not created here (a single code path into the log).
/// </summary>
public static class ControlPlaneSchema
{
    private const int ObjectAlreadyExists = 2714;

    public static async Task EnsureAsync(string controlConnectionString, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(controlConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        foreach (var batch in Batches)
        {
            try
            {
                await using var command = new SqlCommand(batch, connection) { CommandTimeout = 0 };
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (SqlException ex) when (ex.Number == ObjectAlreadyExists)
            {
                // Concurrent first-use race between the IF-NOT-EXISTS check and CREATE; another caller won.
            }
        }
    }

    // One batch per object so each runs as its own command (CREATE SCHEMA must start a batch, and a later
    // table referencing flw.* must follow the schema's creation).
    private static readonly string[] Batches =
    [
        "IF SCHEMA_ID(N'flw') IS NULL EXEC(N'CREATE SCHEMA [flw]');",

        """
        IF OBJECT_ID(N'[flw].[CredentialProfile]', N'U') IS NULL
        CREATE TABLE [flw].[CredentialProfile] (
            [CredentialProfileID] int IDENTITY(1,1) NOT NULL,
            [ProfileAlias] nvarchar(128) NOT NULL,
            [Mode] nvarchar(32) NOT NULL CONSTRAINT [DF_CredProfile_Mode] DEFAULT (N'ConnectionStringAuth'),
            [TenantId] nvarchar(128) NULL,
            [ClientId] nvarchar(128) NULL,
            [ClientSecretRef] nvarchar(256) NULL,
            [KeyVaultName] nvarchar(128) NULL,
            [KeyVaultUriOverride] nvarchar(256) NULL,
            [CreatedDate] datetime2(3) NOT NULL CONSTRAINT [DF_CredProfile_CreatedDate] DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT [PK_CredentialProfile] PRIMARY KEY CLUSTERED ([CredentialProfileID]),
            CONSTRAINT [UQ_CredentialProfile_Alias] UNIQUE ([ProfileAlias]),
            CONSTRAINT [CK_CredProfile_Mode] CHECK ([Mode] IN (N'ConnectionStringAuth', N'Integrated', N'InlineConnectionString', N'InjectedToken')),
            CONSTRAINT [CK_CredProfile_SecretRef] CHECK ([ClientSecretRef] IS NULL OR [ClientSecretRef] LIKE N'${%}'));
        """,

        """
        IF OBJECT_ID(N'[flw].[DataSource]', N'U') IS NULL
        CREATE TABLE [flw].[DataSource] (
            [DataSourceID] int IDENTITY(1,1) NOT NULL,
            [Alias] nvarchar(128) NOT NULL,
            [Kind] nvarchar(16) NOT NULL,
            [Host] nvarchar(256) NULL,
            [ConnectionRef] nvarchar(2000) NOT NULL,
            [IsSynapse] bit NOT NULL CONSTRAINT [DF_DataSource_IsSynapse] DEFAULT (0),
            [SupportsCrossDbRef] bit NOT NULL CONSTRAINT [DF_DataSource_SupportsCrossDbRef] DEFAULT (0),
            [IsLocal] bit NOT NULL CONSTRAINT [DF_DataSource_IsLocal] DEFAULT (0),
            [ActivityMonitoring] bit NOT NULL CONSTRAINT [DF_DataSource_ActivityMon] DEFAULT (0),
            [StorageAccountName] nvarchar(128) NULL,
            [BlobContainer] nvarchar(128) NULL,
            [CredentialProfileID] int NULL,
            [CreatedDate] datetime2(3) NOT NULL CONSTRAINT [DF_DataSource_CreatedDate] DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT [PK_DataSource] PRIMARY KEY CLUSTERED ([DataSourceID]),
            CONSTRAINT [UQ_DataSource_Alias] UNIQUE ([Alias]),
            CONSTRAINT [CK_DataSource_Kind] CHECK ([Kind] IN (N'MSSQL', N'AZDB', N'MySQL', N'PostgreSQL')),
            CONSTRAINT [CK_DataSource_Secretless] CHECK (
                [ConnectionRef] LIKE N'${%}'
                OR ([ConnectionRef] NOT LIKE N'%password=%' AND [ConnectionRef] NOT LIKE N'%pwd=%' AND [ConnectionRef] NOT LIKE N'%clientsecret=%')),
            CONSTRAINT [FK_DataSource_CredentialProfile] FOREIGN KEY ([CredentialProfileID]) REFERENCES [flw].[CredentialProfile] ([CredentialProfileID]));
        """,

        """
        IF OBJECT_ID(N'[flw].[Assertion]', N'U') IS NULL
        CREATE TABLE [flw].[Assertion] (
            [AssertionID] int IDENTITY(1,1) NOT NULL,
            [AssertionName] nvarchar(128) NOT NULL,
            [AssertionExp] nvarchar(max) NOT NULL,
            [IsActive] bit NOT NULL CONSTRAINT [DF_Assertion_IsActive] DEFAULT (1),
            CONSTRAINT [PK_Assertion] PRIMARY KEY CLUSTERED ([AssertionID]),
            CONSTRAINT [UQ_Assertion_Name] UNIQUE ([AssertionName]));
        """,

        """
        IF OBJECT_ID(N'[flw].[Ingestion]', N'U') IS NULL
        CREATE TABLE [flw].[Ingestion] (
            [FlowID] int NOT NULL,
            [Batch] nvarchar(250) NULL,
            [SysAlias] nvarchar(250) NULL,
            [srcServer] nvarchar(128) NULL,
            [srcDBSchTbl] nvarchar(512) NULL,
            [trgServer] nvarchar(128) NULL,
            [trgDBSchTbl] nvarchar(512) NULL,
            [trgDesiredIndex] nvarchar(max) NULL,
            [DeactivateFromBatch] bit NULL,
            [StreamData] bit NULL,
            [NoOfThreads] int NULL,
            [KeyColumns] nvarchar(max) NULL,
            [IncrementalColumns] nvarchar(max) NULL,
            [IncrementalClauseExp] nvarchar(max) NULL,
            [DateColumn] nvarchar(128) NULL,
            [DataSetColumn] nvarchar(128) NULL,
            [NoOfOverlapDays] int NULL,
            [FetchMinValuesFromSrc] bit NULL,
            [SkipUpdateExsisting] bit NULL,
            [SkipInsertNew] bit NULL,
            [FullLoad] int NULL,
            [TruncateTrg] bit NULL,
            [TruncatePreTableOnCompletion] bit NULL,
            [srcFilter] nvarchar(max) NULL,
            [srcFilterIsAppend] bit NULL,
            [IdentityColumn] nvarchar(128) NULL,
            [HashKeyColumns] nvarchar(max) NULL,
            [HashKeyType] nvarchar(50) NULL,
            [IgnoreColumns] nvarchar(max) NULL,
            [IgnoreColumnsInHashkey] nvarchar(max) NULL,
            [SysColumns] nvarchar(512) NULL,
            [ColumnStoreIndexOnTrg] bit NULL,
            [SyncSchema] bit NULL,
            [OnErrorResume] bit NULL,
            [OnSyncCleanColumnName] bit NULL,
            [ReplaceInvalidCharsWith] nvarchar(50) NULL,
            [OnSyncConvertUnicodeDataType] bit NULL,
            [CleanColumnNameSQLRegExp] nvarchar(max) NULL,
            [trgVersioning] bit NULL,
            [InsertUnknownDimRow] bit NULL,
            [TokenVersioning] bit NULL,
            [TokenRetentionDays] int NULL,
            [PreProcessOnTrg] nvarchar(max) NULL,
            [PostProcessOnTrg] nvarchar(max) NULL,
            [PreInvokeAlias] nvarchar(250) NULL,
            [PostInvokeAlias] nvarchar(250) NULL,
            [Assertions] nvarchar(max) NULL,
            [MatchKeysInSrcTrg] bit NULL,
            [UseBatchUpsertToAvoideLockEscalation] bit NULL,
            [BatchUpsertRowCount] int NULL,
            [InitLoad] bit NULL,
            [InitLoadFromDate] date NULL,
            [InitLoadToDate] date NULL,
            [InitLoadBatchBy] varchar(1) NULL,
            [InitLoadBatchSize] int NULL,
            [InitLoadKeyColumn] nvarchar(250) NULL,
            [InitLoadKeyMaxValue] int NULL,
            [BatchOrderBy] int NULL,
            [FlowType] varchar(25) NULL,
            [Description] nvarchar(max) NULL,
            [FromObjectMK] int NULL,
            [ToObjectMK] int NULL,
            [CreatedBy] nvarchar(128) NULL,
            [CreatedDate] datetime2(3) NULL,
            CONSTRAINT [PK_flw_Ingestion] PRIMARY KEY CLUSTERED ([FlowID]));
        """,

        """
        IF OBJECT_ID(N'[flw].[IngestionVirtual]', N'U') IS NULL
        CREATE TABLE [flw].[IngestionVirtual] (
            [VirtualID] int IDENTITY(1,1) NOT NULL,
            [FlowID] int NOT NULL,
            [ColumnName] nvarchar(128) NULL,
            [DataType] nvarchar(128) NULL,
            [DataTypeExp] nvarchar(max) NULL,
            [SelectExp] nvarchar(max) NULL,
            CONSTRAINT [PK_flw_IngestionVirtual] PRIMARY KEY CLUSTERED ([VirtualID]));
        """,

        """
        IF OBJECT_ID(N'[flw].[SurrogateKey]', N'U') IS NULL
        CREATE TABLE [flw].[SurrogateKey] (
            [SurrogateKeyID] int IDENTITY(1,1) NOT NULL,
            [FlowID] int NOT NULL,
            [SurrogateServer] nvarchar(128) NULL,
            [SurrogateDbSchTbl] nvarchar(512) NOT NULL,
            [SurrogateColumn] nvarchar(128) NOT NULL,
            [KeyColumns] nvarchar(max) NOT NULL,
            [sKeyColumns] nvarchar(max) NULL,
            [PreProcess] nvarchar(max) NULL,
            [PostProcess] nvarchar(max) NULL,
            [ToObjectMK] int NULL,
            CONSTRAINT [PK_flw_SurrogateKey] PRIMARY KEY CLUSTERED ([SurrogateKeyID]));
        """,

        """
        IF OBJECT_ID(N'[flw].[StoredProcedure]', N'U') IS NULL
        CREATE TABLE [flw].[StoredProcedure] (
            [FlowID] int NOT NULL,
            [Batch] nvarchar(70) NULL,
            [SysAlias] nvarchar(70) NOT NULL,
            [trgServer] nvarchar(250) NOT NULL,
            [trgDBSchSP] nvarchar(250) NOT NULL,
            [OnErrorResume] bit NULL,
            [PostInvokeAlias] nvarchar(250) NULL,
            [Description] nvarchar(2048) NULL,
            [FlowType] varchar(25) NULL,
            [DeactivateFromBatch] bit NULL,
            [FromObjectMK] int NULL,
            [ToObjectMK] int NULL,
            [CreatedBy] nvarchar(250) NULL,
            [CreatedDate] datetime2(3) NULL,
            CONSTRAINT [PK_flw_StoredProcedure] PRIMARY KEY CLUSTERED ([FlowID]));
        """,

        """
        IF OBJECT_ID(N'[flw].[Export]', N'U') IS NULL
        CREATE TABLE [flw].[Export] (
            [FlowID] int NOT NULL,
            [Batch] nvarchar(250) NULL,
            [SysAlias] nvarchar(70) NOT NULL,
            [srcServer] nvarchar(50) NULL,
            [srcDBSchTbl] nvarchar(250) NULL,
            [srcWithHint] nvarchar(250) NULL,
            [srcFilter] nvarchar(1024) NULL,
            [IncrementalColumn] nvarchar(70) NULL,
            [DateColumn] nvarchar(70) NULL,
            [NoOfOverlapDays] int NULL,
            [FromDate] date NULL,
            [ToDate] date NULL,
            [ExportBy] char(1) NULL,
            [ExportSize] int NULL,
            [ServicePrincipalAlias] nvarchar(70) NULL,
            [trgPath] nvarchar(250) NULL,
            [trgFileName] nvarchar(250) NULL,
            [trgFiletype] nvarchar(250) NULL,
            [trgEncoding] nvarchar(25) NULL,
            [CompressionType] nvarchar(25) NULL,
            [ColumnDelimiter] nvarchar(2) NULL,
            [TextQualifier] nvarchar(2) NULL,
            [AddTimeStampToFileName] bit NULL,
            [Subfolderpattern] nvarchar(25) NULL,
            [NoOfThreads] int NULL,
            [ZipTrg] bit NULL,
            [OnErrorResume] bit NULL,
            [PostInvokeAlias] nvarchar(250) NULL,
            [DeactivateFromBatch] bit NULL,
            [FlowType] varchar(25) NULL,
            [FromObjectMK] int NULL,
            [ToObjectMK] int NULL,
            [CreatedBy] nvarchar(250) NULL,
            [CreatedDate] datetime2(3) NULL,
            CONSTRAINT [PK_flw_Export] PRIMARY KEY CLUSTERED ([FlowID]));
        """,

        """
        IF OBJECT_ID(N'[flw].[Invoke]', N'U') IS NULL
        CREATE TABLE [flw].[Invoke] (
            [FlowID] int NOT NULL,
            [Batch] nvarchar(70) NULL,
            [SysAlias] nvarchar(70) NULL,
            [InvokeAlias] nvarchar(250) NOT NULL,
            [InvokeType] varchar(25) NOT NULL CONSTRAINT [DF_flw_Invoke_InvokeType] DEFAULT (N'aut'),
            [PipelineName] nvarchar(250) NULL,
            [RunbookName] nvarchar(250) NULL,
            [ParameterJSON] nvarchar(2000) NULL,
            [trgServicePrincipalAlias] nvarchar(128) NULL,
            [srcServicePrincipalAlias] nvarchar(128) NULL,
            [OnErrorResume] bit NULL,
            [DeactivateFromBatch] bit NULL,
            [ToObjectMK] int NULL,
            [CreatedBy] nvarchar(250) NULL,
            [CreatedDate] datetime2(3) NULL,
            CONSTRAINT [PK_flw_Invoke] PRIMARY KEY CLUSTERED ([FlowID]),
            CONSTRAINT [UQ_flw_Invoke_Alias] UNIQUE ([InvokeAlias]),
            CONSTRAINT [CK_flw_Invoke_Type] CHECK ([InvokeType] IN (N'adf', N'aut')));
        """,

        """
        IF OBJECT_ID(N'[flw].[MatchKey]', N'U') IS NULL
        CREATE TABLE [flw].[MatchKey] (
            [MatchKeyID] int IDENTITY(1,1) NOT NULL,
            [FlowID] int NOT NULL,
            [Batch] nvarchar(70) NULL,
            [SysAlias] nvarchar(70) NULL,
            [srcServer] nvarchar(128) NULL,
            [srcDatabase] nvarchar(128) NULL,
            [srcSchema] nvarchar(128) NULL,
            [srcObject] nvarchar(250) NULL,
            [trgServer] nvarchar(128) NULL,
            [trgDBSchTbl] nvarchar(512) NULL,
            [DeactivateFromBatch] bit NULL,
            [KeyColumns] nvarchar(max) NULL,
            [DateColumn] nvarchar(128) NULL,
            [ActionType] nvarchar(25) NULL,
            [ActionThresholdPercent] int NULL,
            [IgnoreDeletedRowsAfter] int NULL,
            [srcFilter] nvarchar(max) NULL,
            [trgFilter] nvarchar(max) NULL,
            [OnErrorResume] bit NULL,
            [PreProcessOnTrg] nvarchar(max) NULL,
            [PostProcessOnTrg] nvarchar(max) NULL,
            [Description] nvarchar(max) NULL,
            [ToObjectMK] int NULL,
            [CreatedBy] nvarchar(128) NULL,
            [CreatedDate] datetime2(3) NULL,
            CONSTRAINT [PK_flw_MatchKey] PRIMARY KEY CLUSTERED ([MatchKeyID]),
            CONSTRAINT [CK_flw_MatchKey_ActionType] CHECK ([ActionType] IS NULL OR [ActionType] IN (N'Tag', N'Delete')));
        """,

        """
        IF OBJECT_ID(N'[flw].[ServicePrincipal]', N'U') IS NULL
        CREATE TABLE [flw].[ServicePrincipal] (
            [ServicePrincipalID] int IDENTITY(1,1) NOT NULL,
            [ServicePrincipalAlias] nvarchar(128) NOT NULL,
            [TenantId] nvarchar(128) NULL,
            [ClientId] nvarchar(128) NULL,
            [ClientSecretRef] nvarchar(256) NULL,
            [SubscriptionId] nvarchar(128) NULL,
            [ResourceGroup] nvarchar(256) NULL,
            [DataFactoryName] nvarchar(256) NULL,
            [AutomationAccountName] nvarchar(256) NULL,
            [KeyVaultName] nvarchar(128) NULL,
            [CreatedDate] datetime2(3) NOT NULL CONSTRAINT [DF_ServicePrincipal_CreatedDate] DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT [PK_ServicePrincipal] PRIMARY KEY CLUSTERED ([ServicePrincipalID]),
            CONSTRAINT [UQ_ServicePrincipal_Alias] UNIQUE ([ServicePrincipalAlias]),
            CONSTRAINT [CK_ServicePrincipal_SecretRef] CHECK ([ClientSecretRef] IS NULL OR [ClientSecretRef] LIKE N'${%}'));
        """,
    ];
}
