using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;

using SQLFlowUi.Data;

namespace SQLFlowUi.Controllers
{
    public partial class ExportsqlflowProdController : ExportController
    {
        private readonly sqlflowProdContext context;
        private readonly sqlflowProdService service;

        public ExportsqlflowProdController(sqlflowProdContext context, sqlflowProdService service)
        {
            this.service = service;
            this.context = context;
        }

        [HttpGet("/export/sqlflowProd/assertion/csv")]
        [HttpGet("/export/sqlflowProd/assertion/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportAssertionToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetAssertion(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/assertion/excel")]
        [HttpGet("/export/sqlflowProd/assertion/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportAssertionToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetAssertion(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/datasubscriber/csv")]
        [HttpGet("/export/sqlflowProd/datasubscriber/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportDataSubscriberToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetDataSubscriber(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/datasubscriber/excel")]
        [HttpGet("/export/sqlflowProd/datasubscriber/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportDataSubscriberToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetDataSubscriber(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/datasubscriberquery/csv")]
        [HttpGet("/export/sqlflowProd/datasubscriberquery/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportDataSubscriberQueryToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetDataSubscriberQuery(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/datasubscriberquery/excel")]
        [HttpGet("/export/sqlflowProd/datasubscriberquery/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportDataSubscriberQueryToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetDataSubscriberQuery(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/export/csv")]
        [HttpGet("/export/sqlflowProd/export/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportExportToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetExport(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/export/excel")]
        [HttpGet("/export/sqlflowProd/export/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportExportToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetExport(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/geocoding/csv")]
        [HttpGet("/export/sqlflowProd/geocoding/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportGeoCodingToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetGeoCoding(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/geocoding/excel")]
        [HttpGet("/export/sqlflowProd/geocoding/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportGeoCodingToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetGeoCoding(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/healthcheck/csv")]
        [HttpGet("/export/sqlflowProd/healthcheck/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportHealthCheckToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetHealthCheck(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/healthcheck/excel")]
        [HttpGet("/export/sqlflowProd/healthcheck/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportHealthCheckToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetHealthCheck(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/ingestion/csv")]
        [HttpGet("/export/sqlflowProd/ingestion/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportIngestionToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetIngestion(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/ingestion/excel")]
        [HttpGet("/export/sqlflowProd/ingestion/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportIngestionToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetIngestion(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/ingestiontokenexp/csv")]
        [HttpGet("/export/sqlflowProd/ingestiontokenexp/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportIngestionTokenExpToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetIngestionTokenExp(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/ingestiontokenexp/excel")]
        [HttpGet("/export/sqlflowProd/ingestiontokenexp/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportIngestionTokenExpToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetIngestionTokenExp(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/ingestiontokenize/csv")]
        [HttpGet("/export/sqlflowProd/ingestiontokenize/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportIngestionTokenizeToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetIngestionTokenize(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/ingestiontokenize/excel")]
        [HttpGet("/export/sqlflowProd/ingestiontokenize/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportIngestionTokenizeToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetIngestionTokenize(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/ingestiontransfrom/csv")]
        [HttpGet("/export/sqlflowProd/ingestiontransfrom/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportIngestionTransfromToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetIngestionTransfrom(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/ingestiontransfrom/excel")]
        [HttpGet("/export/sqlflowProd/ingestiontransfrom/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportIngestionTransfromToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetIngestionTransfrom(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/ingestionvirtual/csv")]
        [HttpGet("/export/sqlflowProd/ingestionvirtual/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportIngestionVirtualToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetIngestionVirtual(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/ingestionvirtual/excel")]
        [HttpGet("/export/sqlflowProd/ingestionvirtual/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportIngestionVirtualToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetIngestionVirtual(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/invoke/csv")]
        [HttpGet("/export/sqlflowProd/invoke/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportInvokeToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetInvoke(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/invoke/excel")]
        [HttpGet("/export/sqlflowProd/invoke/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportInvokeToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetInvoke(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/lineageedge/csv")]
        [HttpGet("/export/sqlflowProd/lineageedge/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportLineageEdgeToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetLineageEdge(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/lineageedge/excel")]
        [HttpGet("/export/sqlflowProd/lineageedge/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportLineageEdgeToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetLineageEdge(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/lineagemap/csv")]
        [HttpGet("/export/sqlflowProd/lineagemap/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportLineageMapToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetLineageMap(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/lineagemap/excel")]
        [HttpGet("/export/sqlflowProd/lineagemap/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportLineageMapToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetLineageMap(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/lineageobjectmk/csv")]
        [HttpGet("/export/sqlflowProd/lineageobjectmk/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportLineageObjectMKToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetLineageObjectMK(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/lineageobjectmk/excel")]
        [HttpGet("/export/sqlflowProd/lineageobjectmk/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportLineageObjectMKToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetLineageObjectMK(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/lineageobjectrelation/csv")]
        [HttpGet("/export/sqlflowProd/lineageobjectrelation/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportLineageObjectRelationToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetLineageObjectRelation(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/lineageobjectrelation/excel")]
        [HttpGet("/export/sqlflowProd/lineageobjectrelation/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportLineageObjectRelationToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetLineageObjectRelation(), Request.Query, false), fileName);
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

        [HttpGet("/export/sqlflowProd/parameter/csv")]
        [HttpGet("/export/sqlflowProd/parameter/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportParameterToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetParameter(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/parameter/excel")]
        [HttpGet("/export/sqlflowProd/parameter/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportParameterToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetParameter(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/preingestionado/csv")]
        [HttpGet("/export/sqlflowProd/preingestionado/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionADOToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetPreIngestionADO(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/preingestionado/excel")]
        [HttpGet("/export/sqlflowProd/preingestionado/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionADOToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetPreIngestionADO(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/preingestionadovirtual/csv")]
        [HttpGet("/export/sqlflowProd/preingestionadovirtual/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionADOVirtualToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetPreIngestionADOVirtual(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/preingestionadovirtual/excel")]
        [HttpGet("/export/sqlflowProd/preingestionadovirtual/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionADOVirtualToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetPreIngestionADOVirtual(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/preingestioncsv/csv")]
        [HttpGet("/export/sqlflowProd/preingestioncsv/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionCSVToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetPreIngestionCSV(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/preingestioncsv/excel")]
        [HttpGet("/export/sqlflowProd/preingestioncsv/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionCSVToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetPreIngestionCSV(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/preingestionjsn/csv")]
        [HttpGet("/export/sqlflowProd/preingestionjsn/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionJSNToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetPreIngestionJSN(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/preingestionjsn/excel")]
        [HttpGet("/export/sqlflowProd/preingestionjsn/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionJSNToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetPreIngestionJSN(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/preingestionprc/csv")]
        [HttpGet("/export/sqlflowProd/preingestionprc/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionPRCToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetPreIngestionPRC(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/preingestionprc/excel")]
        [HttpGet("/export/sqlflowProd/preingestionprc/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionPRCToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetPreIngestionPRC(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/preingestionprq/csv")]
        [HttpGet("/export/sqlflowProd/preingestionprq/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionPRQToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetPreIngestionPRQ(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/preingestionprq/excel")]
        [HttpGet("/export/sqlflowProd/preingestionprq/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionPRQToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetPreIngestionPRQ(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/preingestiontransfrom/csv")]
        [HttpGet("/export/sqlflowProd/preingestiontransfrom/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionTransfromToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetPreIngestionTransfrom(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/preingestiontransfrom/excel")]
        [HttpGet("/export/sqlflowProd/preingestiontransfrom/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionTransfromToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetPreIngestionTransfrom(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/preingestionxls/csv")]
        [HttpGet("/export/sqlflowProd/preingestionxls/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionXLSToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetPreIngestionXLS(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/preingestionxls/excel")]
        [HttpGet("/export/sqlflowProd/preingestionxls/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionXLSToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetPreIngestionXLS(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/preingestionxml/csv")]
        [HttpGet("/export/sqlflowProd/preingestionxml/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionXMLToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetPreIngestionXML(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/preingestionxml/excel")]
        [HttpGet("/export/sqlflowProd/preingestionxml/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportPreIngestionXMLToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetPreIngestionXML(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/storedprocedure/csv")]
        [HttpGet("/export/sqlflowProd/storedprocedure/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportStoredProcedureToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetStoredProcedure(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/storedprocedure/excel")]
        [HttpGet("/export/sqlflowProd/storedprocedure/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportStoredProcedureToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetStoredProcedure(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/surrogatekey/csv")]
        [HttpGet("/export/sqlflowProd/surrogatekey/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSurrogateKeyToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSurrogateKey(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/surrogatekey/excel")]
        [HttpGet("/export/sqlflowProd/surrogatekey/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSurrogateKeyToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSurrogateKey(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysaiprompt/csv")]
        [HttpGet("/export/sqlflowProd/sysaiprompt/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysAIPromptToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysAIPrompt(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysaiprompt/excel")]
        [HttpGet("/export/sqlflowProd/sysaiprompt/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysAIPromptToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysAIPrompt(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysalias/csv")]
        [HttpGet("/export/sqlflowProd/sysalias/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysAliasToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysAlias(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysalias/excel")]
        [HttpGet("/export/sqlflowProd/sysalias/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysAliasToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysAlias(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysapikey/csv")]
        [HttpGet("/export/sqlflowProd/sysapikey/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysAPIKeyToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysAPIKey(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysapikey/excel")]
        [HttpGet("/export/sqlflowProd/sysapikey/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysAPIKeyToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysAPIKey(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysbatch/csv")]
        [HttpGet("/export/sqlflowProd/sysbatch/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysBatchToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysBatch(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysbatch/excel")]
        [HttpGet("/export/sqlflowProd/sysbatch/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysBatchToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysBatch(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syscfg/csv")]
        [HttpGet("/export/sqlflowProd/syscfg/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysCFGToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysCFG(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syscfg/excel")]
        [HttpGet("/export/sqlflowProd/syscfg/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysCFGToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysCFG(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syscheckdatatypes/csv")]
        [HttpGet("/export/sqlflowProd/syscheckdatatypes/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysCheckDataTypesToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysCheckDataTypes(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syscheckdatatypes/excel")]
        [HttpGet("/export/sqlflowProd/syscheckdatatypes/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysCheckDataTypesToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysCheckDataTypes(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syscolumn/csv")]
        [HttpGet("/export/sqlflowProd/syscolumn/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysColumnToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysColumn(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syscolumn/excel")]
        [HttpGet("/export/sqlflowProd/syscolumn/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysColumnToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysColumn(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysdatasource/csv")]
        [HttpGet("/export/sqlflowProd/sysdatasource/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysDataSourceToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysDataSource(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysdatasource/excel")]
        [HttpGet("/export/sqlflowProd/sysdatasource/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysDataSourceToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysDataSource(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysdatetimeformat/csv")]
        [HttpGet("/export/sqlflowProd/sysdatetimeformat/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysDateTimeFormatToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysDateTimeFormat(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysdatetimeformat/excel")]
        [HttpGet("/export/sqlflowProd/sysdatetimeformat/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysDateTimeFormatToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysDateTimeFormat(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysdatetimestyle/csv")]
        [HttpGet("/export/sqlflowProd/sysdatetimestyle/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysDateTimeStyleToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysDateTimeStyle(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysdatetimestyle/excel")]
        [HttpGet("/export/sqlflowProd/sysdatetimestyle/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysDateTimeStyleToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysDateTimeStyle(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysdoc/csv")]
        [HttpGet("/export/sqlflowProd/sysdoc/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysDocToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysDoc(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysdoc/excel")]
        [HttpGet("/export/sqlflowProd/sysdoc/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysDocToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysDoc(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysdocnote/csv")]
        [HttpGet("/export/sqlflowProd/sysdocnote/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysDocNoteToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysDocNote(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysdocnote/excel")]
        [HttpGet("/export/sqlflowProd/sysdocnote/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysDocNoteToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysDocNote(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysdocrelation/csv")]
        [HttpGet("/export/sqlflowProd/sysdocrelation/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysDocRelationToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysDocRelation(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysdocrelation/excel")]
        [HttpGet("/export/sqlflowProd/sysdocrelation/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysDocRelationToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysDocRelation(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syserror/csv")]
        [HttpGet("/export/sqlflowProd/syserror/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysErrorToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysError(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syserror/excel")]
        [HttpGet("/export/sqlflowProd/syserror/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysErrorToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysError(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysflowdep/csv")]
        [HttpGet("/export/sqlflowProd/sysflowdep/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysFlowDepToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysFlowDep(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysflowdep/excel")]
        [HttpGet("/export/sqlflowProd/sysflowdep/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysFlowDepToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysFlowDep(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysflownote/csv")]
        [HttpGet("/export/sqlflowProd/sysflownote/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysFlowNoteToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysFlowNote(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysflownote/excel")]
        [HttpGet("/export/sqlflowProd/sysflownote/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysFlowNoteToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysFlowNote(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syslog/csv")]
        [HttpGet("/export/sqlflowProd/syslog/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysLogToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysLog(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syslog/excel")]
        [HttpGet("/export/sqlflowProd/syslog/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysLogToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysLog(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syslogassertion/csv")]
        [HttpGet("/export/sqlflowProd/syslogassertion/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysLogAssertionToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysLogAssertion(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syslogassertion/excel")]
        [HttpGet("/export/sqlflowProd/syslogassertion/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysLogAssertionToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysLogAssertion(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syslogbatch/csv")]
        [HttpGet("/export/sqlflowProd/syslogbatch/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysLogBatchToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysLogBatch(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syslogbatch/excel")]
        [HttpGet("/export/sqlflowProd/syslogbatch/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysLogBatchToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysLogBatch(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syslogexport/csv")]
        [HttpGet("/export/sqlflowProd/syslogexport/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysLogExportToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysLogExport(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syslogexport/excel")]
        [HttpGet("/export/sqlflowProd/syslogexport/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysLogExportToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysLogExport(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syslogfile/csv")]
        [HttpGet("/export/sqlflowProd/syslogfile/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysLogFileToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysLogFile(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syslogfile/excel")]
        [HttpGet("/export/sqlflowProd/syslogfile/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysLogFileToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysLogFile(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syslogfileevent/csv")]
        [HttpGet("/export/sqlflowProd/syslogfileevent/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysLogFileEventToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysLogFileEvent(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syslogfileevent/excel")]
        [HttpGet("/export/sqlflowProd/syslogfileevent/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysLogFileEventToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysLogFileEvent(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syslogmatchkey/csv")]
        [HttpGet("/export/sqlflowProd/syslogmatchkey/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysLogMatchKeyToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysLogMatchKey(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syslogmatchkey/excel")]
        [HttpGet("/export/sqlflowProd/syslogmatchkey/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysLogMatchKeyToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysLogMatchKey(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysperiod/csv")]
        [HttpGet("/export/sqlflowProd/sysperiod/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysPeriodToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysPeriod(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysperiod/excel")]
        [HttpGet("/export/sqlflowProd/sysperiod/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysPeriodToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysPeriod(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysserviceprincipal/csv")]
        [HttpGet("/export/sqlflowProd/sysserviceprincipal/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysServicePrincipalToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysServicePrincipal(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysserviceprincipal/excel")]
        [HttpGet("/export/sqlflowProd/sysserviceprincipal/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysServicePrincipalToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysServicePrincipal(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syssourcecontrol/csv")]
        [HttpGet("/export/sqlflowProd/syssourcecontrol/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysSourceControlToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysSourceControl(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syssourcecontrol/excel")]
        [HttpGet("/export/sqlflowProd/syssourcecontrol/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysSourceControlToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysSourceControl(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syssourcecontroltype/csv")]
        [HttpGet("/export/sqlflowProd/syssourcecontroltype/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysSourceControlTypeToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysSourceControlType(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/syssourcecontroltype/excel")]
        [HttpGet("/export/sqlflowProd/syssourcecontroltype/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysSourceControlTypeToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysSourceControlType(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysstats/csv")]
        [HttpGet("/export/sqlflowProd/sysstats/csv(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysStatsToCSV(string fileName = null)
        {
            return ToCSV(ApplyQuery(await service.GetSysStats(), Request.Query, false), fileName);
        }

        [HttpGet("/export/sqlflowProd/sysstats/excel")]
        [HttpGet("/export/sqlflowProd/sysstats/excel(fileName='{fileName}')")]
        public async Task<FileStreamResult> ExportSysStatsToExcel(string fileName = null)
        {
            return ToExcel(ApplyQuery(await service.GetSysStats(), Request.Query, false), fileName);
        }
    }
}
