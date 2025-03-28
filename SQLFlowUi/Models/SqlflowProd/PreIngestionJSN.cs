using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("PreIngestionJSN", Schema = "flw")]
    public partial class PreIngestionJSN
    {
        [Key]
        public int FlowID { get; set; }

        public string Batch { get; set; }

        public string SysAlias { get; set; }

        public string ServicePrincipalAlias { get; set; }

        [Required]
        public string srcPath { get; set; }

        public string srcPathMask { get; set; }

        [Required]
        public string srcFile { get; set; }

        public string JsonToDataTableCode { get; set; }

        public string trgServer { get; set; }

        public string trgDBSchTbl { get; set; }

        public string trgDesiredIndex { get; set; }

        public bool? altTrgIsEmbedded { get; set; }

        public string altTrgDBSchTbl { get; set; }

        public string altSrcDBSchTbl { get; set; }

        public string preFilter { get; set; }

        public bool? SearchSubDirectories { get; set; }

        public string copyToPath { get; set; }

        public bool? srcDeleteIngested { get; set; }

        public bool? srcDeleteAtPath { get; set; }

        public string zipToPath { get; set; }

        public bool? SyncSchema { get; set; }

        public int? ExpectedColumnCount { get; set; }

        public string DefaultColDataType { get; set; }

        public bool? FetchDataTypes { get; set; }

        public bool? OnErrorResume { get; set; }

        public int? NoOfThreads { get; set; }

        public string PreProcessOnTrg { get; set; }

        public string PostProcessOnTrg { get; set; }

        public string PreInvokeAlias { get; set; }

        public string InitFromFileDate { get; set; }

        public string InitToFileDate { get; set; }

        public int? BatchOrderBy { get; set; }

        public bool? DeactivateFromBatch { get; set; }

        public bool? EnableEventExecution { get; set; }

        public string FlowType { get; set; }

        public int? FromObjectMK { get; set; }

        public int? ToObjectMK { get; set; }

        public bool? ShowPathWithFileName { get; set; }

        public string CreatedBy { get; set; }

        public DateTime? CreatedDate { get; set; }
    }
}