using System.ComponentModel;

namespace SQLFlowApi.RequestType
{
    /// <summary>
    /// Specifies the output handling options for a system documentation prompt request.
    /// </summary>
    public enum SysDocPromptOutput
    {
        /// <summary>
        /// Indicates that the output of the system documentation prompt request should be saved to the database.
        /// </summary>
        /// <remarks>
        /// When this option is selected, the output of the system documentation prompt request will be stored in the database for future reference or analysis.
        /// </remarks>
        SaveToDb,
        /// <summary>
        /// Specifies that the output of a system documentation prompt request should be directed to the prompt.
        /// </summary>
        OutputPrompt,
        /// <summary>
        /// Indicates that the results of the system documentation prompt request should be output directly.
        /// </summary>
        Outputresults
    }

    /// <summary>
    /// Represents the parameters for a system documentation prompt request.
    /// </summary>
    /// <remarks>
    /// This class is used to configure and execute a system documentation prompt request in the SQLFlow API. 
    /// It includes settings for the object name, output type, usage of database payload, model, maximum tokens, 
    /// temperature, top_p, frequency penalty, presence penalty, and the prompt itself.
    /// </remarks>
    public class SysDocPromptRequest
    {
        private const string DefaultObjectName = "";
        private const bool DefaultUseDbPayload = true;
        private const string DefaultPrompt = "";
        private const string DefaultModel = "gpt-4-0125-preview";
        private const int DefaultMaxTokens = 4095;
        private const double DefaultTemperature = 0.7;
        private const double DefaultTopP = 1;
        private const double DefaultFrequencyPenalty = 0.0;
        private const double DefaultPresencePenalty = 0.0;
        /// <summary>
        /// Gets or sets the object name for which we should generate documentation.
        /// </summary>
        [DefaultValue(DefaultObjectName)]
        public string Objectname { get; set; } = DefaultObjectName;

        /// <summary>
        /// Gets or sets the useDbPayload.
        /// </summary>
        [DefaultValue(DefaultUseDbPayload)]
        public bool UseDbPayload { get; set; } = DefaultUseDbPayload;
        /// <summary>
        /// Gets or sets the model name.
        /// </summary>
        [DefaultValue(DefaultModel)]
        public string Model { get; set; } = DefaultModel;
        /// <summary>
        /// Gets or sets the maximum number of tokens.
        /// </summary>
        [DefaultValue(DefaultMaxTokens)]
        public int? Max_tokens { get; set; } = DefaultMaxTokens;
        /// <summary>
        /// Gets or sets the temperature, usually ranges from 0 to 1.
        /// </summary>
        [DefaultValue(DefaultTemperature)]
        public double? Temperature { get; set; } = DefaultTemperature;
        /// <summary>
        /// Gets or sets the top_p, usually ranges from 0 to 1.
        /// </summary>
        [DefaultValue(DefaultTopP)]
        public double? Top_p { get; set; } = DefaultTopP;
        /// <summary>
        /// Gets or sets the frequency penalty, usually ranges from 0 to 2.
        /// </summary>
        [DefaultValue(DefaultFrequencyPenalty)]
        public double? Frequency_penalty { get; set; } = DefaultFrequencyPenalty;
        /// <summary>
        /// Gets or sets the presence penalty, usually ranges from 0 to 2.
        /// </summary>
        [DefaultValue(DefaultPresencePenalty)]
        public double? Presence_penalty { get; set; } = DefaultPresencePenalty;
        /// <summary>
        /// Gets or sets the prompt.
        /// </summary>
        [DefaultValue(DefaultPrompt)]
        public string Prompt { get; set; } = DefaultPrompt;
    }
}
