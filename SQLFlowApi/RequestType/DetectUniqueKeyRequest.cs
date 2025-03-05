using System.ComponentModel;
using SQLFlowCore.Services.UniqueKeyDetector;

namespace SQLFlowApi.RequestType
{
    /// <summary>
    /// Represents a request to detect unique keys in a SQL database.
    /// </summary>
    /// <remarks>
    /// This class is used to initiate a request for detecting unique keys in a given SQL database. 
    /// The actual implementation might depend on the specific database system being used.
    /// </remarks>
    public class DetectUniqueKeyRequest
    {
        /// <summary>
        /// Identifier for the flow.
        /// </summary>
        [DefaultValue(1)]
        public int FlowId { get; set; } = 1;

        /// <summary>
        /// List of columns to be considered.
        /// </summary>
        [DefaultValue("")]
        public string ColList { get; set; } = "";

        /// <summary>
        /// Number of rows to sample for detecting unique keys.
        /// </summary>
        [DefaultValue(100)]
        public int NumberOfRowsToSample { get; set; } = 100;

        /// <summary>
        /// Total number of unique keys sought.
        /// </summary>
        [DefaultValue(5)]
        public int TotalUniqueKeysSought { get; set; } = 3;

        /// <summary>
        /// Maximum size of key combinations to consider.
        /// </summary>
        [DefaultValue(3)]
        public int MaxKeyCombinationSize { get; set; } = 3;

        /// <summary>
        /// Threshold for considering a column redundant based on similarity.
        /// </summary>
        [DefaultValue(0.96)]
        public double RedundantColSimilarityThreshold { get; set; } = 0.96;

        /// <summary>
        /// Uniqueness threshold for considering a key as likely unique.
        /// </summary>
        [DefaultValue(0.20)]
        public double SelectRatioFromTopUniquenessScore { get; set; } = 0.20;


        /// <summary>
        /// Gets or sets the mode of analysis for the unique key detection request.
        /// </summary>
        /// <value>
        /// The mode of analysis, represented by the <see cref="SQLFlowCore.Services.UniqueKeyDetector.AnalysisMode"/> enumeration.
        /// </value>
        /// <remarks>
        /// The analysis mode determines the level of detail and complexity of the unique key detection process. 
        /// The available modes are Basic, Standard, and Extended, each offering a different balance between performance and thoroughness.
        /// </remarks>
        [DefaultValue(AnalysisMode.Standard)]
        public AnalysisMode AnalysisMode { get; set; } = AnalysisMode.Standard;

        
        /// <summary>
        /// Whether to execute the proof query.
        /// </summary>
        [DefaultValue(false)]
        public bool ExecuteProofQuery { get; set; } = false;


        /// <summary>
        /// Gets or sets a value indicating whether the unique key detection process should exit early if a unique key is found.
        /// </summary>
        /// <value>
        ///   <c>true</c> if the detection process should exit early when a unique key is found; otherwise, <c>false</c>.
        /// </value>
        /// <remarks>
        /// If this property is set to <c>true</c>, the detection process will stop as soon as a unique key is found, 
        /// potentially improving performance for large databases. If it is set to <c>false</c>, the process will continue 
        /// until all possible unique keys have been evaluated.
        /// </remarks>
        [DefaultValue(true)]
        public bool EarlyExitOnFound { get; set; } = true;
        

        /// <summary>
        /// Debug flag (0 for off, 1 for on).
        /// </summary>
        [DefaultValue(0)]
        public int Dbg { get; set; } = 0;


        /// <summary>
        /// Gets or sets the cancellation token ID for the request.
        /// </summary>
        /// <value>
        /// The cancellation token ID is a string value that can be used to cancel the request to detect unique keys. 
        /// The default value is an empty string.
        /// </value>
        /// <remarks>
        /// If a cancellation token ID is provided, it can be used to cancel the request before it completes. 
        /// This can be useful in scenarios where the operation is expected to take a long time and there is a need for the user to be able to cancel it.
        /// </remarks>
        [DefaultValue("")]
        public string cancelTokenId { get; set; } = "";
    }
}
