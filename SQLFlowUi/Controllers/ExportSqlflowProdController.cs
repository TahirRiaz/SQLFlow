using Microsoft.AspNetCore.Mvc;
using SQLFlowUi.Data;

namespace SQLFlowUi.Controllers
{
    public partial class ExportSqlflowProdController : ExportController
    {
        private readonly sqlflowProdContext context;
        private readonly sqlflowProdService service;

        public ExportSqlflowProdController(sqlflowProdContext context, sqlflowProdService service)
        {
            this.service = service;
            this.context = context;
        }

        [HttpGet("/export/sqlflowProd/matchkey/csv")]
        [HttpGet("/export/sqlflowProd/matchkey/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportMatchKeyToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetMatchKey(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/matchkey/excel")]
        [HttpGet("/export/sqlflowProd/matchkey/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportMatchKeyToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetMatchKey(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/assertion/csv")]
        [HttpGet("/export/SqlflowProd/assertion/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportAssertionToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetAssertion(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/assertion/excel")]
        [HttpGet("/export/SqlflowProd/assertion/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportAssertionToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetAssertion(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/datasubscriber/csv")]
        [HttpGet("/export/SqlflowProd/datasubscriber/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportDataSubscriberToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetDataSubscriber(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/datasubscriber/excel")]
        [HttpGet("/export/SqlflowProd/datasubscriber/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportDataSubscriberToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetDataSubscriber(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/datasubscriberquery/csv")]
        [HttpGet("/export/SqlflowProd/datasubscriberquery/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportDataSubscriberQueryToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetDataSubscriberQuery(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/datasubscriberquery/excel")]
        [HttpGet("/export/SqlflowProd/datasubscriberquery/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportDataSubscriberQueryToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetDataSubscriberQuery(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/export/csv")]
        [HttpGet("/export/SqlflowProd/export/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportExportToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetExport(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/export/excel")]
        [HttpGet("/export/SqlflowProd/export/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportExportToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetExport(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/geocoding/csv")]
        [HttpGet("/export/SqlflowProd/geocoding/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportGeoCodingToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetGeoCoding(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/geocoding/excel")]
        [HttpGet("/export/SqlflowProd/geocoding/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportGeoCodingToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetGeoCoding(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/healthcheck/csv")]
        [HttpGet("/export/SqlflowProd/healthcheck/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportHealthCheckToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetHealthCheck(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/healthcheck/excel")]
        [HttpGet("/export/SqlflowProd/healthcheck/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportHealthCheckToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetHealthCheck(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/ingestion/csv")]
        [HttpGet("/export/SqlflowProd/ingestion/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportIngestionToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetIngestion(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/ingestion/excel")]
        [HttpGet("/export/SqlflowProd/ingestion/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportIngestionToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetIngestion(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/ingestiontokenexp/csv")]
        [HttpGet("/export/SqlflowProd/ingestiontokenexp/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportIngestionTokenExpToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetIngestionTokenExp(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/ingestiontokenexp/excel")]
        [HttpGet("/export/SqlflowProd/ingestiontokenexp/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportIngestionTokenExpToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetIngestionTokenExp(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/ingestiontokenize/csv")]
        [HttpGet("/export/SqlflowProd/ingestiontokenize/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportIngestionTokenizeToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetIngestionTokenize(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/ingestiontokenize/excel")]
        [HttpGet("/export/SqlflowProd/ingestiontokenize/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportIngestionTokenizeToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetIngestionTokenize(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/ingestiontransfrom/csv")]
        [HttpGet("/export/SqlflowProd/ingestiontransfrom/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportIngestionTransfromToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetIngestionTransfrom(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/ingestiontransfrom/excel")]
        [HttpGet("/export/SqlflowProd/ingestiontransfrom/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportIngestionTransfromToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetIngestionTransfrom(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/ingestionvirtual/csv")]
        [HttpGet("/export/SqlflowProd/ingestionvirtual/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportIngestionVirtualToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetIngestionVirtual(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/ingestionvirtual/excel")]
        [HttpGet("/export/SqlflowProd/ingestionvirtual/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportIngestionVirtualToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetIngestionVirtual(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/invoke/csv")]
        [HttpGet("/export/SqlflowProd/invoke/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportInvokeToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetInvoke(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/invoke/excel")]
        [HttpGet("/export/SqlflowProd/invoke/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportInvokeToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetInvoke(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/lineageedge/csv")]
        [HttpGet("/export/SqlflowProd/lineageedge/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportLineageEdgeToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetLineageEdge(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/lineageedge/excel")]
        [HttpGet("/export/SqlflowProd/lineageedge/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportLineageEdgeToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetLineageEdge(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/lineagemap/csv")]
        [HttpGet("/export/SqlflowProd/lineagemap/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportLineageMapToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetLineageMap(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/lineagemap/excel")]
        [HttpGet("/export/SqlflowProd/lineagemap/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportLineageMapToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetLineageMap(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/lineageobjectmk/csv")]
        [HttpGet("/export/SqlflowProd/lineageobjectmk/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportLineageObjectMKToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetLineageObjectMK(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/lineageobjectmk/excel")]
        [HttpGet("/export/SqlflowProd/lineageobjectmk/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportLineageObjectMKToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetLineageObjectMK(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/lineageobjectrelation/csv")]
        [HttpGet("/export/SqlflowProd/lineageobjectrelation/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportLineageObjectRelationToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetLineageObjectRelation(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/lineageobjectrelation/excel")]
        [HttpGet("/export/SqlflowProd/lineageobjectrelation/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportLineageObjectRelationToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetLineageObjectRelation(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/parameter/csv")]
        [HttpGet("/export/SqlflowProd/parameter/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportParameterToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetParameter(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/parameter/excel")]
        [HttpGet("/export/SqlflowProd/parameter/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportParameterToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetParameter(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/preingestionado/csv")]
        [HttpGet("/export/SqlflowProd/preingestionado/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionADOToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetPreIngestionADO(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/preingestionado/excel")]
        [HttpGet("/export/SqlflowProd/preingestionado/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionADOToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetPreIngestionADO(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/preingestionadovirtual/csv")]
        [HttpGet("/export/SqlflowProd/preingestionadovirtual/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionADOVirtualToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetPreIngestionADOVirtual(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/preingestionadovirtual/excel")]
        [HttpGet("/export/SqlflowProd/preingestionadovirtual/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionADOVirtualToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetPreIngestionADOVirtual(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/preingestioncsv/csv")]
        [HttpGet("/export/SqlflowProd/preingestioncsv/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionCSVToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetPreIngestionCSV(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/preingestioncsv/excel")]
        [HttpGet("/export/SqlflowProd/preingestioncsv/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionCSVToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetPreIngestionCSV(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/preingestionjsn/csv")]
        [HttpGet("/export/SqlflowProd/preingestionjsn/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionJSNToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetPreIngestionJSN(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/preingestionjsn/excel")]
        [HttpGet("/export/SqlflowProd/preingestionjsn/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionJSNToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetPreIngestionJSN(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/preingestionprc/csv")]
        [HttpGet("/export/SqlflowProd/preingestionprc/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionPRCToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetPreIngestionPRC(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/preingestionprc/excel")]
        [HttpGet("/export/SqlflowProd/preingestionprc/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionPRCToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetPreIngestionPRC(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/preingestionprq/csv")]
        [HttpGet("/export/SqlflowProd/preingestionprq/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionPRQToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetPreIngestionPRQ(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/preingestionprq/excel")]
        [HttpGet("/export/SqlflowProd/preingestionprq/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionPRQToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetPreIngestionPRQ(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/preingestiontransfrom/csv")]
        [HttpGet("/export/SqlflowProd/preingestiontransfrom/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionTransfromToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetPreIngestionTransfrom(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/preingestiontransfrom/excel")]
        [HttpGet("/export/SqlflowProd/preingestiontransfrom/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionTransfromToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetPreIngestionTransfrom(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/preingestionxls/csv")]
        [HttpGet("/export/SqlflowProd/preingestionxls/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionXLSToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetPreIngestionXLS(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/preingestionxls/excel")]
        [HttpGet("/export/SqlflowProd/preingestionxls/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionXLSToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetPreIngestionXLS(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/preingestionxml/csv")]
        [HttpGet("/export/SqlflowProd/preingestionxml/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionXMLToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetPreIngestionXML(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/preingestionxml/excel")]
        [HttpGet("/export/SqlflowProd/preingestionxml/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionXMLToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetPreIngestionXML(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/storedprocedure/csv")]
        [HttpGet("/export/SqlflowProd/storedprocedure/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportStoredProcedureToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetStoredProcedure(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/storedprocedure/excel")]
        [HttpGet("/export/SqlflowProd/storedprocedure/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportStoredProcedureToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetStoredProcedure(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/surrogatekey/csv")]
        [HttpGet("/export/SqlflowProd/surrogatekey/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSurrogateKeyToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSurrogateKey(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/surrogatekey/excel")]
        [HttpGet("/export/SqlflowProd/surrogatekey/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSurrogateKeyToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSurrogateKey(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysaiprompt/csv")]
        [HttpGet("/export/SqlflowProd/sysaiprompt/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysAIPromptToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysAIPrompt(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysaiprompt/excel")]
        [HttpGet("/export/SqlflowProd/sysaiprompt/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysAIPromptToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysAIPrompt(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysalias/csv")]
        [HttpGet("/export/SqlflowProd/sysalias/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysAliasToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysAlias(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysalias/excel")]
        [HttpGet("/export/SqlflowProd/sysalias/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysAliasToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysAlias(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysapikey/csv")]
        [HttpGet("/export/SqlflowProd/sysapikey/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysAPIKeyToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysAPIKey(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysapikey/excel")]
        [HttpGet("/export/SqlflowProd/sysapikey/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysAPIKeyToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysAPIKey(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysbatch/csv")]
        [HttpGet("/export/SqlflowProd/sysbatch/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysBatchToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysBatch(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysbatch/excel")]
        [HttpGet("/export/SqlflowProd/sysbatch/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysBatchToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysBatch(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/syscfg/csv")]
        [HttpGet("/export/SqlflowProd/syscfg/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysCFGToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysCFG(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/syscfg/excel")]
        [HttpGet("/export/SqlflowProd/syscfg/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysCFGToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysCFG(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/syscolumn/csv")]
        [HttpGet("/export/SqlflowProd/syscolumn/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysColumnToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysColumn(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/syscolumn/excel")]
        [HttpGet("/export/SqlflowProd/syscolumn/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysColumnToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysColumn(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysdatasource/csv")]
        [HttpGet("/export/SqlflowProd/sysdatasource/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysDataSourceToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysDataSource(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysdatasource/excel")]
        [HttpGet("/export/SqlflowProd/sysdatasource/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysDataSourceToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysDataSource(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysdatetimeformat/csv")]
        [HttpGet("/export/SqlflowProd/sysdatetimeformat/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysDateTimeFormatToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysDateTimeFormat(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysdatetimeformat/excel")]
        [HttpGet("/export/SqlflowProd/sysdatetimeformat/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysDateTimeFormatToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysDateTimeFormat(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysdatetimestyle/csv")]
        [HttpGet("/export/SqlflowProd/sysdatetimestyle/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysDateTimeStyleToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysDateTimeStyle(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysdatetimestyle/excel")]
        [HttpGet("/export/SqlflowProd/sysdatetimestyle/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysDateTimeStyleToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysDateTimeStyle(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysdoc/csv")]
        [HttpGet("/export/SqlflowProd/sysdoc/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysDocToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysDoc(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysdoc/excel")]
        [HttpGet("/export/SqlflowProd/sysdoc/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysDocToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysDoc(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysdocnote/csv")]
        [HttpGet("/export/SqlflowProd/sysdocnote/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysDocNoteToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysDocNote(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysdocnote/excel")]
        [HttpGet("/export/SqlflowProd/sysdocnote/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysDocNoteToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysDocNote(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysdocrelation/csv")]
        [HttpGet("/export/SqlflowProd/sysdocrelation/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysDocRelationToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysDocRelation(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysdocrelation/excel")]
        [HttpGet("/export/SqlflowProd/sysdocrelation/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysDocRelationToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysDocRelation(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/syserror/csv")]
        [HttpGet("/export/SqlflowProd/syserror/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysErrorToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysError(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/syserror/excel")]
        [HttpGet("/export/SqlflowProd/syserror/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysErrorToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysError(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysflowdep/csv")]
        [HttpGet("/export/SqlflowProd/sysflowdep/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysFlowDepToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysFlowDep(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysflowdep/excel")]
        [HttpGet("/export/SqlflowProd/sysflowdep/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysFlowDepToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysFlowDep(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysflownote/csv")]
        [HttpGet("/export/SqlflowProd/sysflownote/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysFlowNoteToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysFlowNote(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysflownote/excel")]
        [HttpGet("/export/SqlflowProd/sysflownote/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysFlowNoteToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysFlowNote(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/syslog/csv")]
        [HttpGet("/export/SqlflowProd/syslog/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysLogToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysLog(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/syslog/excel")]
        [HttpGet("/export/SqlflowProd/syslog/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysLogToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysLog(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/syslogassertion/csv")]
        [HttpGet("/export/SqlflowProd/syslogassertion/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysLogAssertionToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysLogAssertion(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/syslogassertion/excel")]
        [HttpGet("/export/SqlflowProd/syslogassertion/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysLogAssertionToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysLogAssertion(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/syslogbatch/csv")]
        [HttpGet("/export/SqlflowProd/syslogbatch/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysLogBatchToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysLogBatch(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/syslogbatch/excel")]
        [HttpGet("/export/SqlflowProd/syslogbatch/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysLogBatchToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysLogBatch(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/syslogexport/csv")]
        [HttpGet("/export/SqlflowProd/syslogexport/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysLogExportToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysLogExport(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/syslogexport/excel")]
        [HttpGet("/export/SqlflowProd/syslogexport/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysLogExportToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysLogExport(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/syslogfile/csv")]
        [HttpGet("/export/SqlflowProd/syslogfile/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysLogFileToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysLogFile(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/syslogfile/excel")]
        [HttpGet("/export/SqlflowProd/syslogfile/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysLogFileToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysLogFile(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysserviceprincipal/csv")]
        [HttpGet("/export/SqlflowProd/sysserviceprincipal/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysServicePrincipalToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysServicePrincipal(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysserviceprincipal/excel")]
        [HttpGet("/export/SqlflowProd/sysserviceprincipal/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysServicePrincipalToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysServicePrincipal(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/syssourcecontrol/csv")]
        [HttpGet("/export/SqlflowProd/syssourcecontrol/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysSourceControlToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysSourceControl(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/syssourcecontrol/excel")]
        [HttpGet("/export/SqlflowProd/syssourcecontrol/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysSourceControlToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysSourceControl(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/syssourcecontroltype/csv")]
        [HttpGet("/export/SqlflowProd/syssourcecontroltype/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysSourceControlTypeToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysSourceControlType(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/syssourcecontroltype/excel")]
        [HttpGet("/export/SqlflowProd/syssourcecontroltype/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysSourceControlTypeToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysSourceControlType(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysstats/csv")]
        [HttpGet("/export/SqlflowProd/sysstats/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysStatsToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysStats(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/sysstats/excel")]
        [HttpGet("/export/SqlflowProd/sysstats/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysStatsToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysStats(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/reportbatchstartend/csv")]
        [HttpGet("/export/SqlflowProd/reportbatchstartend/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExporttempToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetReportBatchStartEnd(), Request.Query, false), fileName);
        }

        [HttpGet("/export/SqlflowProd/reportbatchstartend/excel")]
        [HttpGet("/export/SqlflowProd/reportbatchstartend/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExporttempToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetReportBatchStartEnd(), Request.Query, false), fileName);
        }
    }
}
