using System;
using System.Collections.Generic;

namespace SQLFlowCore.Services.UniqueKeyDetector
{
    public enum AnalysisMode
    {
        // Phase 1: Basic, Check if likely unique columns are unique
        Basic = 1,

        // Phase 2: Standard, Try combinations of likely unique columns
        Standard = 2,

        // Phase 3: Extended, Try extended combinations with unique columns
        Extended = 3
    }

    public class AnalysisModeHelper
    {
        public static AnalysisMode ConvertStringToAnalysisMode(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                throw new ArgumentException("Input cannot be null or whitespace.", nameof(input));
            }

            if (Enum.TryParse(input, true, out AnalysisMode phase))
            {
                return phase;
            }
            else
            {
                throw new ArgumentException($"Invalid value for AnalysisMode: {input}", nameof(input));
            }
        }

        public static AnalysisMode ConvertIntToAnalysisMode(int input)
        {
            // Check if the value is defined in the enum
            if (Enum.IsDefined(typeof(AnalysisMode), input))
            {
                return (AnalysisMode)input;
            }
            else
            {
                throw new ArgumentException($"Invalid value for AnalysisMode: {input}", nameof(input));
            }
        }


        public static IEnumerable<AnalysisMode> GetAllAnalysisPhases()
        {
            foreach (AnalysisMode phase in Enum.GetValues(typeof(AnalysisMode)))
            {
                yield return phase;
            }
        }

    }

}
