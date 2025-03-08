using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("Export", Schema = "flw")]
    public partial class Export
    {
        [Key]
        public int FlowID { get; set; }

        public string Batch { get; set; }

        [Required]
        public string SysAlias { get; set; }

        public string srcServer { get; set; }

        public string srcDBSchTbl { get; set; }

        public string srcWithHint { get; set; }

        public string srcFilter { get; set; }

        public string IncrementalColumn { get; set; }

        public string DateColumn { get; set; }

        public int? NoOfOverlapDays { get; set; }

        public DateTime? FromDate { get; set; }

        public DateTime? ToDate { get; set; }

        public string ExportBy { get; set; }

        public int? ExportSize { get; set; }

        public string ServicePrincipalAlias { get; set; }

        public string trgPath { get; set; }

        public string trgFileName { get; set; }

        public string trgFiletype { get; set; }

        public string trgEncoding { get; set; }

        public string CompressionType { get; set; }

        public string ColumnDelimiter { get; set; }

        public string TextQualifier { get; set; }

        public bool? AddTimeStampToFileName { get; set; }

        public string Subfolderpattern { get; set; }

        public int? NoOfThreads { get; set; }

        public bool? ZipTrg { get; set; }

        public bool? OnErrorResume { get; set; }

        public string PostInvokeAlias { get; set; }

        public bool? DeactivateFromBatch { get; set; }

        public string FlowType { get; set; }

        public int? FromObjectMK { get; set; }

        public int? ToObjectMK { get; set; }

        public string CreatedBy { get; set; }

        public DateTime? CreatedDate { get; set; }
    }
}