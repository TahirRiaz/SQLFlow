using Microsoft.EntityFrameworkCore;

namespace SQLFlowUi.Data
{
    public partial class sqlflowProdContext : DbContext
    {
        public sqlflowProdContext()
        {
        }

        public sqlflowProdContext(DbContextOptions<sqlflowProdContext> options) : base(options)
        {
           
        }

        partial void OnModelBuilding(ModelBuilder builder);

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<SQLFlowUi.Models.sqlflowProd.GetApiKey>().HasNoKey();
            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysDateTimeStyle>().HasNoKey();
            //builder.Entity<SQLFlowUi.Models.sqlflowProd.FlowHealthCheck>().HasNoKey();
            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysDetectUniqueKey>().HasNoKey();

            //builder.Entity<SQLFlowUi.Models.sqlflowProd.FlowDS>().HasNoKey();
            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysDetectUniqueKey>()
                .Property(e => e.RedundantColSimilarityThreshold)
                .HasColumnType("decimal(18,6)");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysDetectUniqueKey>()
                .Property(e => e.SelectRatioFromTopUniquenessScore)
                .HasColumnType("decimal(18,6)");

            // SysLog decimal properties
            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysLog>()
                .Property(e => e.FlowRate)
                .HasColumnType("decimal(18,6)");

            // SysLogExport decimal properties
            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysLogExport>()
                .Property(e => e.FileSize_DW)
                .HasColumnType("decimal(18,6)");

            // SysLogFile decimal properties
            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysLogFile>()
                .Property(e => e.FileSize_DW)
                .HasColumnType("decimal(18,6)");

            // SysStats decimal properties
            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysStats>()
                .Property(e => e.FlowRate)
                .HasColumnType("decimal(18,6)");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Export>()
              .Property(p => p.NoOfOverlapDays)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Export>()
              .Property(p => p.ExportBy)
              .HasDefaultValueSql(@"('D')");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Export>()
              .Property(p => p.ExportSize)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Export>()
              .Property(p => p.trgFiletype)
              .HasDefaultValueSql(@"(N'csv')");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Export>()
              .Property(p => p.CompressionType)
              .HasDefaultValueSql(@"(N'gzip')");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Export>()
              .Property(p => p.ColumnDelimiter)
              .HasDefaultValueSql(@"(N';')");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Export>()
              .Property(p => p.TextQualifier)
              .HasDefaultValueSql(@"(N'""')");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Export>()
              .Property(p => p.AddTimeStampToFileName)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Export>()
              .Property(p => p.NoOfThreads)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Export>()
              .Property(p => p.ZipTrg)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Export>()
              .Property(p => p.OnErrorResume)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Export>()
              .Property(p => p.DeactivateFromBatch)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Export>()
              .Property(p => p.FlowType)
              .HasDefaultValueSql(@"(N'exp')");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Export>()
              .Property(p => p.CreatedBy)
              .HasDefaultValueSql(@"(suser_sname())");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Export>()
              .Property(p => p.CreatedDate)
              .HasDefaultValueSql(@"(getdate())");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Export>()
                .Property(e => e.FlowID).UseHiLo("FlowID", "flw");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
              .Property(p => p.DeactivateFromBatch)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
              .Property(p => p.StreamData)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
              .Property(p => p.NoOfThreads)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
              .Property(p => p.NoOfOverlapDays)
              .HasDefaultValueSql(@"((7))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
              .Property(p => p.FetchMinValuesFromSrc)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
              .Property(p => p.SkipUpdateExsisting)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
                .Property(p => p.TruncateTrg)
                .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
                .Property(p => p.TruncatePreTableOnCompletion)
                .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
                .Property(p => p.TokenVersioning)
                .HasDefaultValueSql(@"((0))");
            


            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
              .Property(p => p.SkipInsertNew)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
              .Property(p => p.FullLoad)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
              .Property(p => p.srcFilterIsAppend)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
              .Property(p => p.SysColumns)
              .HasDefaultValueSql(@"(N'InsertedDate_DW,UpdatedDate_DW')");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
              .Property(p => p.ColumnStoreIndexOnTrg)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
              .Property(p => p.SyncSchema)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
              .Property(p => p.OnErrorResume)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
              .Property(p => p.OnSyncCleanColumnName)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
              .Property(p => p.OnSyncConvertUnicodeDataType)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
              .Property(p => p.trgVersioning)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
              .Property(p => p.InsertUnknownDimRow)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
              .Property(p => p.FlowType)
              .HasDefaultValueSql(@"('ing')");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
              .Property(p => p.CreatedBy)
              .HasDefaultValueSql(@"(suser_sname())");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
              .Property(p => p.CreatedDate)
              .HasDefaultValueSql(@"(getdate())");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
                .Property(e => e.FlowID).UseHiLo("FlowID", "flw");


            builder.Entity<SQLFlowUi.Models.sqlflowProd.Invoke>()
              .Property(p => p.InvokeType)
              .HasDefaultValueSql(@"(N'aut')");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Invoke>()
              .Property(p => p.OnErrorResume)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Invoke>()
              .Property(p => p.DeactivateFromBatch)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Invoke>()
                .Property(e => e.FlowID).UseHiLo("FlowID","flw");
                
                
            builder.Entity<SQLFlowUi.Models.sqlflowProd.LineageObjectMK>()
              .Property(p => p.IsDependencyObject)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.LineageObjectMK>()
              .Property(p => p.AfterDependency)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Parameter>()
              .Property(p => p.PreFetch)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Parameter>()
              .Property(p => p.Defaultvalue)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionADO>()
              .Property(p => p.DeactivateFromBatch)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionADO>()
              .Property(p => p.StreamData)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionADO>()
              .Property(p => p.NoOfThreads)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionADO>()
              .Property(p => p.NoOfOverlapDays)
              .HasDefaultValueSql(@"((7))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionADO>()
              .Property(p => p.FetchMinValuesFromSysLog)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionADO>()
              .Property(p => p.FullLoad)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionADO>()
              .Property(p => p.TruncateTrg)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionADO>()
              .Property(p => p.srcFilterIsAppend)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionADO>()
              .Property(p => p.InitLoad)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionADO>()
              .Property(p => p.InitLoadBatchBy)
              .HasDefaultValueSql(@"('M')");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionADO>()
              .Property(p => p.InitLoadBatchSize)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionADO>()
              .Property(p => p.SyncSchema)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionADO>()
              .Property(p => p.OnErrorResume)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionADO>()
              .Property(p => p.FlowType)
              .HasDefaultValueSql(@"('ado')");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionADO>()
              .Property(p => p.CreatedBy)
              .HasDefaultValueSql(@"(suser_sname())");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionADO>()
              .Property(p => p.CreatedDate)
              .HasDefaultValueSql(@"(getdate())");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionADO>()
                .Property(e => e.FlowID).UseHiLo("FlowID", "flw");


            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV>()
              .Property(p => p.SearchSubDirectories)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV>()
              .Property(p => p.srcDeleteIngested)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV>()
              .Property(p => p.srcDeleteAtPath)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV>()
              .Property(p => p.SyncSchema)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV>()
              .Property(p => p.ExpectedColumnCount)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV>()
              .Property(p => p.FetchDataTypes)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV>()
              .Property(p => p.OnErrorResume)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV>()
              .Property(p => p.SkipEmptyRows)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV>()
              .Property(p => p.FirstRowSetsExpectedColumnCount)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV>()
              .Property(p => p.EscapeCharacter)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV>()
              .Property(p => p.SkipEndingDataRows)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV>()
              .Property(p => p.MaxBufferSize)
              .HasDefaultValueSql(@"((1024))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV>()
              .Property(p => p.MaxRows)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV>()
              .Property(p => p.NoOfThreads)
              .HasDefaultValueSql(@"((4))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV>()
              .Property(p => p.BatchOrderBy)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV>()
              .Property(p => p.FlowType)
              .HasDefaultValueSql(@"(N'CSV')");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV>()
              .Property(p => p.ShowPathWithFileName)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV>()
                .Property(p => p.DeactivateFromBatch)
                .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV>()
                .Property(p => p.EnableEventExecution)
                .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV>()
              .Property(p => p.CreatedBy)
              .HasDefaultValueSql(@"(suser_sname())");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV>()
              .Property(p => p.CreatedDate)
              .HasDefaultValueSql(@"(getdate())");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV>()
                .Property(e => e.FlowID).UseHiLo("FlowID", "flw");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.DataSubscriber>()
                .Property(e => e.FlowID).UseHiLo("FlowID", "flw");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN>()
              .Property(p => p.SearchSubDirectories)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN>()
              .Property(p => p.srcDeleteIngested)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN>()
              .Property(p => p.srcDeleteAtPath)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN>()
              .Property(p => p.SyncSchema)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN>()
              .Property(p => p.ExpectedColumnCount)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN>()
              .Property(p => p.FetchDataTypes)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN>()
              .Property(p => p.OnErrorResume)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN>()
              .Property(p => p.NoOfThreads)
              .HasDefaultValueSql(@"((4))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN>()
              .Property(p => p.DeactivateFromBatch)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN>()
                .Property(p => p.EnableEventExecution)
                .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN>()
              .Property(p => p.FlowType)
              .HasDefaultValueSql(@"(N'jsn')");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN>()
                .Property(p => p.JsonToDataTableCode)
                .HasDefaultValueSql(@"(N'')");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN>()
              .Property(p => p.ShowPathWithFileName)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN>()
              .Property(p => p.CreatedBy)
              .HasDefaultValueSql(@"(suser_sname())");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN>()
              .Property(p => p.CreatedDate)
              .HasDefaultValueSql(@"(getdate())");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN>()
                .Property(e => e.FlowID).UseHiLo("FlowID", "flw");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRC>()
              .Property(p => p.SyncSchema)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRC>()
              .Property(p => p.ExpectedColumnCount)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRC>()
              .Property(p => p.FetchDataTypes)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRC>()
              .Property(p => p.OnErrorResume)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRC>()
              .Property(p => p.NoOfThreads)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRC>()
              .Property(p => p.DeactivateFromBatch)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRC>()
              .Property(p => p.FlowType)
              .HasDefaultValueSql(@"(N'prc')");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRC>()
              .Property(p => p.ShowPathWithFileName)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRC>()
              .Property(p => p.CreatedBy)
              .HasDefaultValueSql(@"(suser_sname())");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRC>()
              .Property(p => p.CreatedDate)
              .HasDefaultValueSql(@"(getdate())");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRC>()
                .Property(e => e.FlowID).UseHiLo("FlowID", "flw");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ>()
              .Property(p => p.SearchSubDirectories)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ>()
              .Property(p => p.srcDeleteIngested)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ>()
              .Property(p => p.srcDeleteAtPath)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ>()
              .Property(p => p.SyncSchema)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ>()
              .Property(p => p.ExpectedColumnCount)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ>()
              .Property(p => p.FetchDataTypes)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ>()
              .Property(p => p.OnErrorResume)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ>()
              .Property(p => p.NoOfThreads)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ>()
              .Property(p => p.DeactivateFromBatch)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ>()
                .Property(p => p.EnableEventExecution)
                .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ>()
              .Property(p => p.FlowType)
              .HasDefaultValueSql(@"(N'prq')");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ>()
              .Property(p => p.ShowPathWithFileName)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ>()
              .Property(p => p.CreatedBy)
              .HasDefaultValueSql(@"(suser_sname())");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ>()
              .Property(p => p.CreatedDate)
              .HasDefaultValueSql(@"(getdate())");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ>()
                .Property(e => e.FlowID).UseHiLo("FlowID", "flw");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionTransfrom>()
              .Property(p => p.Virtual)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionTransfrom>()
              .Property(p => p.ExcludeColFromView)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS>()
              .Property(p => p.SearchSubDirectories)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS>()
              .Property(p => p.srcDeleteIngested)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS>()
              .Property(p => p.srcDeleteAtPath)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS>()
              .Property(p => p.UseSheetIndex)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS>()
              .Property(p => p.SyncSchema)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS>()
              .Property(p => p.ExpectedColumnCount)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS>()
              .Property(p => p.FetchDataTypes)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS>()
              .Property(p => p.OnErrorResume)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS>()
              .Property(p => p.NoOfThreads)
              .HasDefaultValueSql(@"((4))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS>()
              .Property(p => p.DeactivateFromBatch)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS>()
                .Property(p => p.EnableEventExecution)
                .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS>()
              .Property(p => p.FlowType)
              .HasDefaultValueSql(@"(N'xml')");



            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS>()
              .Property(p => p.ShowPathWithFileName)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS>()
              .Property(p => p.CreatedBy)
              .HasDefaultValueSql(@"(suser_sname())");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS>()
              .Property(p => p.CreatedDate)
              .HasDefaultValueSql(@"(getdate())");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS>()
                .Property(e => e.FlowID).UseHiLo("FlowID", "flw");


            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXML>()
              .Property(p => p.SearchSubDirectories)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXML>()
              .Property(p => p.srcDeleteIngested)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXML>()
              .Property(p => p.srcDeleteAtPath)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXML>()
              .Property(p => p.SyncSchema)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXML>()
              .Property(p => p.ExpectedColumnCount)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXML>()
              .Property(p => p.OnErrorResume)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXML>()
              .Property(p => p.NoOfThreads)
              .HasDefaultValueSql(@"((4))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXML>()
              .Property(p => p.DeactivateFromBatch)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXML>()
                .Property(p => p.EnableEventExecution)
                .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXML>()
              .Property(p => p.FlowType)
              .HasDefaultValueSql(@"(N'XML')");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXML>()
              .Property(p => p.ShowPathWithFileName)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXML>()
              .Property(p => p.CreatedBy)
              .HasDefaultValueSql(@"(suser_sname())");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXML>()
              .Property(p => p.CreatedDate)
              .HasDefaultValueSql(@"(getdate())");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXML>()
                .Property(p => p.XmlToDataTableCode)
                .HasDefaultValueSql(@"(N'')");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXML>()
                .Property(e => e.FlowID).UseHiLo("FlowID", "flw");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.StoredProcedure>()
              .Property(p => p.OnErrorResume)
              .HasDefaultValueSql(@"((1))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.StoredProcedure>()
              .Property(p => p.FlowType)
              .HasDefaultValueSql(@"('sp')");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.StoredProcedure>()
              .Property(p => p.DeactivateFromBatch)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.StoredProcedure>()
                .Property(e => e.FlowID).UseHiLo("FlowID", "flw");


            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysDataSource>()
              .Property(p => p.SupportsCrossDBRef)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysDataSource>()
              .Property(p => p.IsSynapse)
              .HasDefaultValueSql(@"((0))");



            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysDataSource>()
              .Property(p => p.IsLocal)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysLogBatch>()
              .Property(p => p.Status)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysLogBatch>()
              .Property(p => p.SourceIsAzCont)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysLogExport>()
              .Property(p => p.ExportDate)
              .HasDefaultValueSql(@"(getdate())");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysLogFile>()
              .Property(p => p.FileRowDate_DW)
              .HasDefaultValueSql(@"(getdate())");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysSourceControlType>()
              .Property(p => p.CreateWrkProjRepo)
              .HasDefaultValueSql(@"((0))");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysStats>()
              .Property(p => p.StatsDate)
              .HasDefaultValueSql(@"(getdate())");


            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysDateTimeFormat>()
              .Property(p => p.FormatLength)
              .HasComputedColumnSql(@"(len([Format]))")
              .ValueGeneratedOnAddOrUpdate()
              .Metadata.SetBeforeSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Export>()
              .Property(p => p.CreatedDate)
              .HasColumnType("datetime");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.Ingestion>()
              .Property(p => p.CreatedDate)
              .HasColumnType("datetime");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.LineageEdge>()
              .Property(p => p.CreateDate)
              .HasColumnType("datetime");


            builder.Entity<SQLFlowUi.Models.sqlflowProd.LineageMap>()
              .Property(p => p.LastExec)
              .HasColumnType("datetime");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionADO>()
              .Property(p => p.CreatedDate)
              .HasColumnType("datetime");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV>()
              .Property(p => p.CreatedDate)
              .HasColumnType("datetime");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN>()
              .Property(p => p.CreatedDate)
              .HasColumnType("datetime");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRC>()
              .Property(p => p.CreatedDate)
              .HasColumnType("datetime");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ>()
              .Property(p => p.CreatedDate)
              .HasColumnType("datetime");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS>()
              .Property(p => p.CreatedDate)
              .HasColumnType("datetime");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.PreIngestionXML>()
              .Property(p => p.CreatedDate)
              .HasColumnType("datetime");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysLog>()
              .Property(p => p.StartTime)
              .HasColumnType("datetime");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysLog>()
              .Property(p => p.EndTime)
              .HasColumnType("datetime");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysLogBatch>()
              .Property(p => p.BatchTime)
              .HasColumnType("datetime");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysLogBatch>()
              .Property(p => p.StartTime)
              .HasColumnType("datetime");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysLogBatch>()
              .Property(p => p.EndTime)
              .HasColumnType("datetime");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysLogExport>()
              .Property(p => p.ExportDate)
              .HasColumnType("datetime");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysLogFile>()
              .Property(p => p.FileRowDate_DW)
              .HasColumnType("datetime");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysStats>()
              .Property(p => p.StatsDate)
              .HasColumnType("datetime");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysStats>()
              .Property(p => p.StartTime)
              .HasColumnType("datetime");

            builder.Entity<SQLFlowUi.Models.sqlflowProd.SysStats>()
              .Property(p => p.EndTime)
              .HasColumnType("datetime");


            builder.Entity<SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery>()
                .HasOne(i => i.DataSubscriber)
                .WithMany(i => i.DataSubscriberQuery)
                .HasForeignKey(i => i.FlowID)
                .HasPrincipalKey(i => i.FlowID);


            this.OnModelBuilding(builder);
        }

        public DbSet<SQLFlowUi.Models.sqlflowProd.Export> Export { get; set; }

       

        public DbSet<SQLFlowUi.Models.sqlflowProd.GeoCoding> GeoCoding { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.Ingestion> Ingestion { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.IngestionTokenExp> IngestionTokenExp { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.IngestionTokenize> IngestionTokenize { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.IngestionTransfrom> IngestionTransfrom { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.IngestionVirtual> IngestionVirtual { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.Invoke> Invoke { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.LineageEdge> LineageEdge { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.LineageMap> LineageMap { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.LineageObjectMK> LineageObjectMK { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.Parameter> Parameter { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.PreIngestionADO> PreIngestionADO { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual> PreIngestionADOVirtual { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.PreIngestionCSV> PreIngestionCSV { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN> PreIngestionJSN { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.PreIngestionPRC> PreIngestionPRC { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.PreIngestionPRQ> PreIngestionPRQ { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.PreIngestionTransfrom> PreIngestionTransfrom { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.PreIngestionXLS> PreIngestionXLS { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.PreIngestionXML> PreIngestionXML { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.StoredProcedure> StoredProcedure { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SurrogateKey> SurrogateKey { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysAlias> SysAlias { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysBatch> SysBatch { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysCFG> SysCFG { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysColumn> SysColumn { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysCompressionType> SysCompressionType { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysDataSource> SysDataSource { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysDateTimeFormat> SysDateTimeFormat { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysDateTimeStyle> SysDateTimeStyle { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysDoc> SysDoc { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysDocSubSet> SysDocSubset { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysError> SysError { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysFlowDep> SysFlowDep { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysHashKeyType> SysHashKeyType { get; set; }

        
        public DbSet<SQLFlowUi.Models.sqlflowProd.SysLog> SysLog { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysLogBatch> SysLogBatch { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysLogExport> SysLogExport { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysLogFile> SysLogFile { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysServicePrincipal> SysServicePrincipal { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysSourceControl> SysSourceControl { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysSourceControlType> SysSourceControlType { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysStats> SysStats { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysExportBy> SysExportBy { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysSubFolderPattern> SysSubFolderPatterns { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysFileEncoding> SysFileEncodings { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.FlowDS> FlowDs { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.GetApiKey> GetGoogleApiKeys { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.ReportBatchStartEnd> ReportBatchStartEnd { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.HealthCheck> HealthCheck { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.FlowHealthCheck> ReportFlowHealthCheck { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.Assertion> Assertion { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysLogAssertion> SysLogAssertion { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.ReportAssertion> ReportAssertion { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysAIPrompt> SysAIPrompt { get; set; }


        public DbSet<SQLFlowUi.Models.sqlflowProd.LineageObjectRelation> LineageObjectRelation { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysDocRelation> SysDocRelation { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysAPIKey> SysAPIKey { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysDocNote> SysDocNote { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysFlowNote> SysFlowNote { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.DataSubscriber> DataSubscriber { get; set; }
        public DbSet<SQLFlowUi.Models.sqlflowProd.SysDataSubscriberType> DataSubscriberType { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysMatchKeyDeletedRowHandeling> SysMatchKeyDeletedRowHandeling { get; set; }
        public DbSet<SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery> DataSubscriberQuery { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysFlowType> SysFlowType { get; set; }
        public DbSet<SQLFlowUi.Models.sqlflowProd.SysOpenAIModel> SysOpenAIModel { get; set; }
         
        public DbSet<SQLFlowUi.Models.sqlflowProd.SysFlowNoteType> SysFlowNoteType { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.SysDetectUniqueKey> SysDetectUniqueKey { get; set; }

        public DbSet<SQLFlowUi.Models.sqlflowProd.MatchKey> MatchKey { get; set; }
        
        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            configurationBuilder.Conventions.Add(_ => new BlankTriggerAddingConvention());
        }
    
    }
}