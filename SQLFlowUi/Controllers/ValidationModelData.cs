using Newtonsoft.Json;

namespace SQLFlowUi.Controllers
{
    public class ValidationMetrics
    {
        [JsonProperty("MeanAbsoluteError")]
        public double MeanAbsoluteError { get; set; }

        [JsonProperty("MeanSquaredError")]
        public double MeanSquaredError { get; set; }

        [JsonProperty("RootMeanSquaredError")]
        public double RootMeanSquaredError { get; set; }

        [JsonProperty("LossFunction")]
        public double LossFunction { get; set; }

        [JsonProperty("RSquared")]
        public double RSquared { get; set; }
    }

    public class Model
    {
        [JsonProperty("IsRowToRowMapper")]
        public bool IsRowToRowMapper { get; set; }
    }

    public class ValidationModelData
    {
        [JsonProperty("ValidationMetrics")]
        public ValidationMetrics ValidationMetrics { get; set; }
         
        [JsonProperty("Model")]
        public Model Model { get; set; }

        [JsonProperty("Exception")]
        public object Exception { get; set; } // Assuming it's an object; modify as needed

        [JsonProperty("TrainerName")]
        public string TrainerName { get; set; }

        [JsonProperty("RuntimeInSeconds")]
        public double RuntimeInSeconds { get; set; }

        [JsonProperty("Estimator")]
        public object Estimator { get; set; } // Assuming it's an object; modify as needed
    }
}
