using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysDetectUniqueKey", Schema = "flw")]
    public partial class SysDetectUniqueKey
    {
        public int NumberOfRowsToSample { get; set; } = 100000;

        public int TotalUniqueKeysSought { get; set; } = 3;
        public int MaxKeyCombinationSize { get; set; } = 3;

        public decimal RedundantColSimilarityThreshold { get; set; } = 0.95m;
        public decimal SelectRatioFromTopUniquenessScore { get; set; } = 0.20m;

        public int MaxDegreeOfParallelism { get; set; } = 6;

        public string AnalysisMode { get; set; } = "Standard";
        
        public bool ExecuteProofQuery { get; set; } = false;

        public bool EarlyExitOnFound { get; set; } = true;

        
    }
}