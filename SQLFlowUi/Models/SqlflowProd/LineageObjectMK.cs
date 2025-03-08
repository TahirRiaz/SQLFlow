using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("LineageObjectMK", Schema = "flw")]
    public partial class LineageObjectMK
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int ObjectMK { get; set; }

        [Required]
        public string SysAlias { get; set; }

        [Required]
        public string ObjectName { get; set; }

        public string ObjectType { get; set; }

        public string ObjectSource { get; set; }

        public int? ObjectID { get; set; }

        public int? ObjectDbID { get; set; }

        public bool? IsFlowObject { get; set; }

        public bool? NotInUse { get; set; }

        public bool? IsDependencyObject { get; set; }

        public string BeforeDependency { get; set; }

        public string AfterDependency { get; set; }

        public string ObjectDef { get; set; }

        public string RelationJson { get; set; }

        public string CurrentIndexes { get; set; }
    }
}