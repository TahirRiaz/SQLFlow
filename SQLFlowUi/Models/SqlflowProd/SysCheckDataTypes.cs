using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysCheckDataTypes", Schema = "flw")]
    public partial class SysCheckDataTypes
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int RecID { get; set; }

        public string TableSchema { get; set; }

        [Required]
        public string TableName { get; set; }

        public string ColumnName { get; set; }

        public int? Ordinal { get; set; }

        public string DataType { get; set; }

        public string DataTypeExp { get; set; }

        public string NewDataTypeExp { get; set; }

        public string NewMaxDataTypeExp { get; set; }

        public string MinValue { get; set; }

        public string MaxValue { get; set; }

        public string RandValue { get; set; }

        public int? ValueWeight { get; set; }

        [Required]
        public int MinLength { get; set; }

        [Required]
        public int MaxLength { get; set; }

        public string SelectExp { get; set; }

        public int? CommaCount { get; set; }

        public int? DotCount { get; set; }

        public int? ColonCount { get; set; }

        public bool? IsDate { get; set; }

        public bool? IsDateTime { get; set; }

        public bool? IsTime { get; set; }

        public int? DateLocal { get; set; }

        public DateTime? ValAsDate { get; set; }

        public bool? IsNumeric { get; set; }

        public bool? IsString { get; set; }

        public int? DecimalPoints { get; set; }

        public string cmdSQL { get; set; }

        public string SQLFlowExp { get; set; }

        public string MaxSQLFlowExp { get; set; }
    }
}