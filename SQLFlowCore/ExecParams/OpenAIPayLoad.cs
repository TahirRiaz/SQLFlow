namespace SQLFlowCore.ExecParams
{
    public class OpenAIPayLoad
    {
        // Default model can be set to a specific model name. Update as needed.
        public string model { get; set; } = "gpt-4-0125-preview";

        // Default max_tokens. This can be set to a typical value like 100.
        public int? max_tokens { get; set; } = 4095;

        // Default temperature, usually ranges from 0 to 1. 0.7 is a common default.
        public double? temperature { get; set; } = 0.7;

        // Default top_p, usually ranges from 0 to 1. 1 is a common default.
        public double? top_p { get; set; } = 1;

        // Default frequency_penalty, usually ranges from 0 to 2. 0 is a common default.
        public double? frequency_penalty { get; set; } = 0.0;

        // Default presence_penalty, usually ranges from 0 to 2. 0 is a common default.
        public double? presence_penalty { get; set; } = 0.0;

        // Default model can be set to a specific model name. Update as needed.
        public string prompt { get; set; } = "";
        
        // Default model can be set to a specific model name. Update as needed.
        public string promptoutput { get; set; }

    }

}
