using SQLFlowUi.Models.sqlflowProd;

namespace SQLFlowUi.Controllers
{
    public class HealthCheckJson
    {

        public static IEnumerable<IGrouping<int, SQLFlowUi.Models.sqlflowProd.FlowHealthCheck>> GroupDataByYear(IEnumerable<FlowHealthCheck> data)
        {
            // Group the data by year
            var groupedData = data
                .Where(x => x.Date.HasValue) // Ensure the Date is not null
                .GroupBy(x => x.Date.Value.Year); // Group by year

            return groupedData;
        }

        public static ChartOptions CreateChartOptions(IEnumerable<FlowHealthCheck> data)
        {
            // First, order the data by Date
            var orderedData = data.OrderBy(x => x.Date).ToList();

            var chartOptions = new ChartOptions
            {
                series = new List<Series>
                {
                    new Series
                    {
                        name = "BaseValue",
                        type = "column",
                        data = orderedData.Select(x => x.BaseValue).ToList()
                    },
                    new Series
                    {
                        name = "PredictedValue",
                        type = "column",
                        data = orderedData.Select(x => x.PredictedValue).ToList()
                    }
                },
                chart = new Chart
                {
                    height = 480,
                    type = "line"
                },
                stroke = new Stroke
                {
                    width = new List<int> { 0, 4 }
                },
                title = new Title
                {
                    text = "BaseValue vs PredictedValue"
                },
                dataLabels = new DataLabels
                {
                    enabled = false,
                    enabledOnSeries = new List<int> { 1 }
                },
                labels = orderedData.Select(x => x.Date.HasValue ? x.Date.Value.ToString("yyyy-MM-dd") : "").Distinct().ToList(),
                xaxis = new Xaxis
                { 
                    type = "datetime",
                    range = 14
                },
                yaxis = new List<Yaxis>
                {
                    new Yaxis
                    {
                        title = new Title { text = "Value" }
                    }
                }
            };

            return chartOptions;
        }
    }


    public class ChartOptions
    {
        public List<Series> series { get; set; }
        public Chart chart { get; set; }
        public Stroke stroke { get; set; }
        public Title title { get; set; }
        public DataLabels dataLabels { get; set; }
        public List<string> labels { get; set; }
        public Xaxis xaxis { get; set; }
        public List<Yaxis> yaxis { get; set; }
    }

    public class Series
    {
        public string name { get; set; }
        public string type { get; set; }
        public List<int?> data { get; set; }
    }

    public class Chart
    {
        public int height { get; set; }
        public string type { get; set; }
    }

    public class Stroke
    {
        public List<int> width { get; set; }
    }

    public class Title
    {
        public string text { get; set; }
    }

    public class DataLabels
    {
        public bool enabled { get; set; }
        public List<int> enabledOnSeries { get; set; }
    }

    public class Xaxis
    {
        public string type { get; set; }
        
        public int range { get; set; }
        
    }

    public class Yaxis
    {
        public Title title { get; set; }
        public bool? opposite { get; set; }
    }
}
