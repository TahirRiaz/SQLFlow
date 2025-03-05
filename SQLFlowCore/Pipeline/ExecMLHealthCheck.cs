using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.AutoML;
using System.Text.Json;
using SQLFlowCore.Engine.Utils.DataExt;
using SQLFlowCore.Args;
using SQLFlowCore.Common;
using SQLFlowCore.Services.AzureResources;

namespace SQLFlowCore.Pipeline
{
    /// <summary>
    /// The ExecMLHealthCheck class provides functionality for executing machine learning health checks in the SQLFlow application.
    /// </summary>
    public static class ExecMLHealthCheck
    {
        private static readonly EventArgsSchema schArgs = new();
        private const int DefaultMovingAverageWindow = 7;
        private const float DefaultAnomalyThresholdStdDev = 2.0f;
        private const string LogSeparator = "----------------------------------------";

        #region ExecMLHealthCheck

        /// <summary>
        /// Executes the health check process.
        /// </summary>
        /// <param name="sqlFlowConString">The SQLFlow connection string.</param>
        /// <param name="flowId">The flow identifier.</param>
        /// <param name="runModelSelection">The run model selection parameter.</param>
        /// <param name="dbg">The debug parameter.</param>
        /// <returns>A string representing the result of the health check process.</returns>
        public static string Exec(string sqlFlowConString, int flowId, int runModelSelection, int dbg)
        {
            var logStack = new StringBuilder();
            var codeStack = new StringBuilder();

            logStack.AppendLine($"Starting health check execution at {DateTime.Now}");
            logStack.AppendLine(LogSeparator);

            // Validate input parameters
            if (string.IsNullOrWhiteSpace(sqlFlowConString))
            {
                throw new ArgumentException("SQLFlow connection string cannot be null or empty", nameof(sqlFlowConString));
            }

            if (flowId <= 0)
            {
                throw new ArgumentException("Flow ID must be a positive integer", nameof(flowId));
            }

            // Parse and validate connection string
            ConStringParser conStringParser = new(sqlFlowConString)
            {
                ConBuilderMsSql =
                {
                    ApplicationName = "SQLFlow App"
                }
            };
            sqlFlowConString = conStringParser.ConBuilderMsSql.ConnectionString;

            using var sqlFlowCon = new SqlConnection(sqlFlowConString);
            try
            {
                sqlFlowCon.Open();
                logStack.AppendLine("Connected to SQLFlow database successfully");

                var healthCheckParameters = GetHealthCheckParameters(sqlFlowCon, flowId, dbg, codeStack);

                foreach (var parameters in healthCheckParameters)
                {
                    logStack.AppendLine($"Processing health check ID: {parameters.HealthCheckID}");
                    logStack.AppendLine($"Target object: {parameters.TargetDBSchTbl}");
                    logStack.AppendLine($"Date column: {parameters.DateColumn}");

                    string targetConString = ResolveTargetConnectionString(parameters, logStack);

                    if (string.IsNullOrWhiteSpace(parameters.HealthCheckCommand))
                    {
                        logStack.AppendLine("Health check command is empty, skipping this health check.");
                        continue;
                    }

                    ExecuteHealthCheck(
                        sqlFlowCon,
                        targetConString,
                        parameters,
                        runModelSelection,
                        logStack);
                }

                return logStack.ToString();
            }
            catch (Exception ex)
            {
                logStack.AppendLine($"Error in health check execution: {ex.Message}");
                logStack.AppendLine($"Stack trace: {ex.StackTrace}");

                // Rethrow with additional context
                throw new ApplicationException($"Health check execution failed for flow ID {flowId}", ex);
            }
        }

        /// <summary>
        /// Retrieves health check parameters from the database.
        /// </summary>
        private static List<HealthCheckParameters> GetHealthCheckParameters(
            SqlConnection connection,
            int flowId,
            int debugLevel,
            StringBuilder codeStack)
        {
            var parameters = new List<HealthCheckParameters>();

            string flowParamCmd = $"exec [flw].[GetRVHealthCheck] @FlowID = {flowId}";
            if (debugLevel > 1)
            {
                codeStack.AppendLine(CodeStackSection("GetRVHealthCheck Runtime Values HealthCheck:", flowParamCmd));
            }

            using var ds = CommonDB.GetDataSetFromSP(connection, flowParamCmd, 360);
            if (ds?.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0)
            {
                foreach (DataRow row in ds.Tables[0].Rows)
                {
                    parameters.Add(new HealthCheckParameters
                    {
                        HealthCheckID = Convert.ToInt32(row["HealthCheckID"]),
                        TargetDBSchTbl = row["trgDBSchTbl"]?.ToString() ?? string.Empty,
                        DateColumn = row["DateColumn"]?.ToString() ?? string.Empty,
                        BaseValueExp = row["BaseValueExp"]?.ToString() ?? string.Empty,
                        TargetConString = row["trgConString"]?.ToString() ?? string.Empty,
                        TargetTenantId = row["trgTenantId"]?.ToString() ?? string.Empty,
                        TargetApplicationId = row["trgApplicationId"]?.ToString() ?? string.Empty,
                        TargetClientSecret = row["trgClientSecret"]?.ToString() ?? string.Empty,
                        TargetKeyVaultName = row["trgKeyVaultName"]?.ToString() ?? string.Empty,
                        TargetSecretName = row["trgSecretName"]?.ToString() ?? string.Empty,
                        TargetStorageAccountName = row["trgStorageAccountName"]?.ToString() ?? string.Empty,
                        TargetBlobContainer = row["trgBlobContainer"]?.ToString() ?? string.Empty,
                        HealthCheckCommand = row["hcCmd"]?.ToString() ?? string.Empty,
                        MLMaxExperimentTimeInSeconds = Convert.ToUInt32(row["mLMaxExperimentTimeInSeconds"]),
                        ModelData = row["MLModel"] != DBNull.Value ? (byte[])row["MLModel"] : null
                    });
                }
            }

            return parameters;
        }

        /// <summary>
        /// Resolves the target connection string, including retrieving from Azure Key Vault if necessary.
        /// </summary>
        private static string ResolveTargetConnectionString(HealthCheckParameters parameters, StringBuilder logStack)
        {
            string targetConString = parameters.TargetConString;

            // If secret name is specified, retrieve from Key Vault
            if (!string.IsNullOrWhiteSpace(parameters.TargetSecretName))
            {
                logStack.AppendLine("Retrieving connection string from Azure Key Vault");
                try
                {
                    var keyVaultManager = new AzureKeyVaultManager(
                        parameters.TargetTenantId,
                        parameters.TargetApplicationId,
                        parameters.TargetClientSecret,
                        parameters.TargetKeyVaultName);

                    targetConString = keyVaultManager.GetSecret(parameters.TargetSecretName);
                    logStack.AppendLine("Successfully retrieved connection string from Key Vault");
                }
                catch (Exception ex)
                {
                    logStack.AppendLine($"Failed to retrieve connection string from Key Vault: {ex.Message}");
                    throw new ApplicationException("Failed to retrieve connection string from Key Vault", ex);
                }
            }

            // Ensure the connection string is valid
            if (string.IsNullOrWhiteSpace(targetConString))
            {
                throw new InvalidOperationException("Target connection string could not be resolved");
            }

            // Parse and validate the target connection string
            var conStringParser = new ConStringParser(targetConString)
            {
                ConBuilderMsSql =
                {
                    ApplicationName = "SQLFlow Target"
                }
            };

            return conStringParser.ConBuilderMsSql.ConnectionString;
        }

        /// <summary>
        /// Executes a health check using the specified parameters.
        /// </summary>
        private static void ExecuteHealthCheck(
            SqlConnection sqlFlowCon,
            string targetConString,
            HealthCheckParameters parameters,
            int runModelSelection,
            StringBuilder logStack)
        {
            var stopwatch = new Stopwatch();
            var operationwatch = new Stopwatch();

            try
            {
                using var targetConnection = new SqlConnection(targetConString);
                targetConnection.Open();
                logStack.AppendLine($"Connected to target database successfully");

                // Load data and prepare for ML
                stopwatch.Start();

                logStack.AppendLine($"Starting health check on {parameters.TargetDBSchTbl} with date column {parameters.DateColumn}");
                operationwatch.Restart();
                var dailyDataList = LoadData(targetConnection, parameters.HealthCheckCommand);
                operationwatch.Stop();
                logStack.AppendLine($"Loaded {dailyDataList.Count} rows of base data in {operationwatch.ElapsedMilliseconds / 1000} seconds");

                operationwatch.Restart();
                var dateFeatureList = GetDateFeatures(sqlFlowCon);
                operationwatch.Stop();
                logStack.AppendLine($"Loaded {dateFeatureList.Count} date features in {operationwatch.ElapsedMilliseconds / 1000} seconds");

                // Process data for ML
                operationwatch.Restart();

                // Detect frequency and missing data
                var dates = dailyDataList.Select(data => data.Date).ToList();
                var frequency = DetectFrequency(dates);
                logStack.AppendLine($"Detected data frequency: {frequency}");

                var missingDates = DetectMissingDates(dates, frequency);
                logStack.AppendLine($"Found {missingDates.Count} missing dates in the dataset");

                // Prepare dataset
                AddMissingDatesWithZeroBaseValue(dailyDataList, missingDates);
                PopulateDailyDataWithDateFeatures(dailyDataList, dateFeatureList);
                ImputeWithWeekdayAndMovingAverage(dailyDataList, DefaultMovingAverageWindow);

                operationwatch.Stop();
                logStack.AppendLine($"Prepared data for ML processing in {operationwatch.ElapsedMilliseconds / 1000} seconds");

                stopwatch.Stop();
                logStack.AppendLine($"Data preparation completed in {stopwatch.ElapsedMilliseconds / 1000} seconds");

                // Initialize ML context with fixed seed for reproducibility
                var mlContext = new MLContext(seed: 42);
                string jsonDailyData;

                if (runModelSelection == 1 || parameters.ModelData == null)
                {
                    // Train a new model
                    TrainAndSaveNewModel(mlContext, dailyDataList, parameters, sqlFlowCon, logStack, out jsonDailyData);
                }
                else
                {
                    // Use existing model
                    UseExistingModel(mlContext, dailyDataList, parameters, sqlFlowCon, logStack, out jsonDailyData);
                }

                logStack.AppendLine("Health check completed successfully");
                logStack.AppendLine(LogSeparator);
            }
            catch (Exception ex)
            {
                logStack.AppendLine($"Error executing health check: {ex.Message}");
                logStack.AppendLine($"Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// Trains a new model and saves it to the database.
        /// </summary>
        private static void TrainAndSaveNewModel(
            MLContext mlContext,
            List<DailyData> dailyDataList,
            HealthCheckParameters parameters,
            SqlConnection sqlFlowCon,
            StringBuilder logStack,
            out string jsonDailyData)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            logStack.AppendLine("Training new prediction model");

            // Create training data view
            IDataView trainingData = mlContext.Data.LoadFromEnumerable(dailyDataList);

            // Define features - Excluding BaseValueAdjusted as this is the target column
            var featureColumnNames = new string[] {
                "Year", "Quarter", "WeekOfYear", "MonthNumber",
                "DayOfWeekNumber", "IsWeekend", "IsHoliday"
            };

            // Create feature pipeline
            var featurePipeline = mlContext.Transforms.Concatenate(
                "Features", featureColumnNames)
                .Append(mlContext.Transforms.NormalizeMinMax("Features"));

            // Split data for training and validation
            var dataSplit = mlContext.Data.TrainTestSplit(trainingData, testFraction: 0.2, seed: 42);
            var trainSet = dataSplit.TrainSet;
            var testSet = dataSplit.TestSet;

            // Configure experiment
            var experimentSettings = new RegressionExperimentSettings
            {
                MaxExperimentTimeInSeconds = parameters.MLMaxExperimentTimeInSeconds,
                OptimizingMetric = RegressionMetric.RSquared, // Use R² instead of MAE for better overall fit
                CacheDirectoryName = Path.Combine(Path.GetTempPath(), "SQLFlowMLCache")
            };

            var progressHandler = new RegressionExperimentProgressHandler();
            var regressionExperiment = mlContext.Auto().CreateRegressionExperiment(experimentSettings);

            // Run the experiment
            logStack.AppendLine($"Starting AutoML experiment (max time: {parameters.MLMaxExperimentTimeInSeconds} seconds)");
            var resultPrediction = regressionExperiment.Execute(
                trainSet,
                labelColumnName: "BaseValueAdjusted",
                progressHandler: progressHandler);

            var bestRun = resultPrediction.BestRun;
            logStack.AppendLine($"Best model: {bestRun.TrainerName} with R² = {bestRun.ValidationMetrics.RSquared:F4}");

            // Evaluate the model on test data
            var testMetrics = mlContext.Regression.Evaluate(
                bestRun.Model.Transform(testSet),
                labelColumnName: "BaseValueAdjusted");

            logStack.AppendLine($"Test data metrics - R²: {testMetrics.RSquared:F4}, MAE: {testMetrics.MeanAbsoluteError:F4}");

            // Create prediction engine and make predictions
            var predictionEngine = mlContext.Model.CreatePredictionEngine<DailyData, DailyDataPrediction>(bestRun.Model);

            foreach (var dailyData in dailyDataList)
            {
                var prediction = predictionEngine.Predict(dailyData);
                dailyData.PredictedValue = prediction.PredictedValue;
                dailyData.trgObject = parameters.TargetDBSchTbl;
            }

            // Tag anomalies using improved detection
            TagSignificantDifferences(dailyDataList);

            // Calculate evaluation metrics
            CalculateAndAddEvaluationMetrics(dailyDataList);

            // Convert to JSON
            jsonDailyData = ConvertDailyDataListToJson(dailyDataList);

            // Save the model to the database
            SaveModelToDatabase(
                mlContext,
                bestRun.Model,
                resultPrediction.BestRun.TrainerName,
                progressHandler.GetResultsAsJson(),
                jsonDailyData,
                parameters.HealthCheckID,
                sqlFlowCon);

            stopwatch.Stop();
            logStack.AppendLine($"Model training and prediction completed in {stopwatch.ElapsedMilliseconds / 1000} seconds");
        }

        /// <summary>
        /// Uses an existing model for predictions.
        /// </summary>
        private static void UseExistingModel(
            MLContext mlContext,
            List<DailyData> dailyDataList,
            HealthCheckParameters parameters,
            SqlConnection sqlFlowCon,
            StringBuilder logStack,
            out string jsonDailyData)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            logStack.AppendLine("Using existing model for predictions");

            // Load the model from byte array
            ITransformer model = LoadModelFromByteArray(mlContext, parameters.ModelData);

            // Create prediction engine
            var predictionEngine = mlContext.Model.CreatePredictionEngine<DailyData, DailyDataPrediction>(model);

            // Make predictions
            foreach (var dailyData in dailyDataList)
            {
                var prediction = predictionEngine.Predict(dailyData);
                dailyData.PredictedValue = prediction.PredictedValue;
                dailyData.trgObject = parameters.TargetDBSchTbl;
            }

            // Tag anomalies using improved detection
            TagSignificantDifferences(dailyDataList);

            // Calculate evaluation metrics
            CalculateAndAddEvaluationMetrics(dailyDataList);

            // Convert to JSON
            jsonDailyData = ConvertDailyDataListToJson(dailyDataList);

            // Update the result in the database
            UpdateResultInDatabase(jsonDailyData, parameters.HealthCheckID, sqlFlowCon);

            stopwatch.Stop();
            logStack.AppendLine($"Prediction using existing model completed in {stopwatch.ElapsedMilliseconds / 1000} seconds");
        }

        /// <summary>
        /// Calculates evaluation metrics for the predictions and adds them to the log.
        /// </summary>
        private static void CalculateAndAddEvaluationMetrics(List<DailyData> dailyDataList)
        {
            // Filter out rows with imputed data for accurate evaluation
            var actualDataPoints = dailyDataList.Where(d => d.IsNoData == 0).ToList();

            if (actualDataPoints.Count < 10)
            {
                // Not enough data points for meaningful metrics
                return;
            }

            // Calculate Mean Absolute Error
            float mae = actualDataPoints.Average(d => Math.Abs(d.BaseValue - d.PredictedValue));

            // Calculate Root Mean Squared Error
            float rmse = (float)Math.Sqrt(actualDataPoints.Average(d =>
                Math.Pow(d.BaseValue - d.PredictedValue, 2)));

            // Calculate Mean Absolute Percentage Error
            float mape = actualDataPoints
                .Where(d => d.BaseValue != 0) // Avoid division by zero
                .Average(d => Math.Abs((d.BaseValue - d.PredictedValue) / d.BaseValue)) * 100;

            // Calculate R-squared
            float meanActual = actualDataPoints.Average(d => d.BaseValue);
            float ssTot = actualDataPoints.Sum(d => (float)Math.Pow(d.BaseValue - meanActual, 2));
            float ssRes = actualDataPoints.Sum(d => (float)Math.Pow(d.BaseValue - d.PredictedValue, 2));
            float rSquared = 1 - (ssRes / ssTot);

            // Add metrics to daily data
            foreach (var data in dailyDataList)
            {
                data.EvaluationMAE = mae;
                data.EvaluationRMSE = rmse;
                data.EvaluationMAPE = mape;
                data.EvaluationRSquared = rSquared;
            }
        }

        /// <summary>
        /// Saves a trained model and its results to the database.
        /// </summary>
        private static void SaveModelToDatabase(
            MLContext mlContext,
            ITransformer model,
            string modelName,
            string modelSelectionResults,
            string jsonResults,
            int healthCheckId,
            SqlConnection connection)
        {
            byte[] modelData;
            using (var stream = new MemoryStream())
            {
                mlContext.Model.Save(model, null, stream);
                modelData = stream.ToArray();
            }

            using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE [flw].[HealthCheck]
SET [MLModelSelection] = @MLModelSelection
   ,[MLModelName] = @MLModelName
   ,[MLModel] = @MLModel
   ,[MLModelDate] = @CreatedDate
   ,[Result] = @Result
WHERE [HealthCheckID] = @HealthCheckID;";

            command.Parameters.Add("@MLModelSelection", SqlDbType.NVarChar).Value = modelSelectionResults;
            command.Parameters.Add("@MLModelName", SqlDbType.NVarChar).Value = modelName;
            command.Parameters.Add("@MLModel", SqlDbType.VarBinary).Value = modelData;
            command.Parameters.Add("@CreatedDate", SqlDbType.DateTime).Value = DateTime.UtcNow;
            command.Parameters.Add("@Result", SqlDbType.NVarChar).Value = jsonResults;
            command.Parameters.Add("@HealthCheckID", SqlDbType.Int).Value = healthCheckId;

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Updates the prediction results in the database.
        /// </summary>
        private static void UpdateResultInDatabase(
            string jsonResults,
            int healthCheckId,
            SqlConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
UPDATE [flw].[HealthCheck]
SET [ResultDate] = @ResultDate
   ,[Result] = @Result
WHERE [HealthCheckID] = @HealthCheckID;";

            command.Parameters.Add("@ResultDate", SqlDbType.DateTime).Value = DateTime.UtcNow;
            command.Parameters.Add("@Result", SqlDbType.NVarChar).Value = jsonResults;
            command.Parameters.Add("@HealthCheckID", SqlDbType.Int).Value = healthCheckId;

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Loads a machine learning model from a byte array.
        /// </summary>
        /// <param name="mlContext">The machine learning context.</param>
        /// <param name="modelData">The byte array containing the serialized machine learning model.</param>
        /// <returns>A machine learning model transformer.</returns>
        static ITransformer LoadModelFromByteArray(MLContext mlContext, byte[] modelData)
        {
            if (modelData == null || modelData.Length == 0)
            {
                throw new ArgumentException("Model data cannot be null or empty", nameof(modelData));
            }

            using var stream = new MemoryStream(modelData);
            return mlContext.Model.Load(stream, out _);
        }

        /// <summary>
        /// Converts a list of DailyData objects to a JSON string.
        /// </summary>
        /// <param name="dailyDataList">The list of DailyData objects to convert.</param>
        /// <returns>A JSON string representation of the list of DailyData objects.</returns>
        static string ConvertDailyDataListToJson(List<DailyData> dailyDataList)
        {
            if (dailyDataList == null)
            {
                throw new ArgumentNullException(nameof(dailyDataList));
            }

            // Setup serialization options for better performance and readability
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            return JsonSerializer.Serialize(dailyDataList, options);
        }

        /// <summary>
        /// Formats a section of the code stack for logging purposes.
        /// </summary>
        /// <param name="header">The header of the section.</param>
        /// <param name="tsql">The T-SQL command associated with the section.</param>
        /// <returns>A formatted string representing the section of the code stack.</returns>
        private static string CodeStackSection(string header, string tsql)
        {
            return $"{Environment.NewLine}--################################### {header} ###{Environment.NewLine}{tsql}{Environment.NewLine}";
        }

        #endregion ExecMLHealthCheck

        /// <summary>
        /// Retrieves a list of date features from the SQLFlow database.
        /// </summary>
        /// <param name="connection">The SQL connection to the SQLFlow database.</param>
        /// <returns>A list of DateFeature objects, each representing a set of features based on a specific date.</returns>
        static List<DateFeature> GetDateFeatures(SqlConnection connection)
        {
            var dateFeatures = new List<DateFeature>();

            const string query = @"
SELECT [Date], [Year], [Quarter], [WeekOfYear], [MonthNumber], 
       [DayOfWeekNumber], [IsWeekend], [IsHoliday] 
FROM [flw].[SysDateFeatures]";

            using var command = new SqlCommand(query, connection);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                dateFeatures.Add(new DateFeature
                {
                    Date = reader.GetDateTime(0),
                    Year = (float)reader.GetDouble(1),
                    Quarter = (float)reader.GetDouble(2),
                    WeekOfYear = (float)reader.GetDouble(3),
                    MonthNumber = (float)reader.GetDouble(4),
                    DayOfWeekNumber = (float)reader.GetDouble(5),
                    IsWeekend = (float)reader.GetDouble(6),
                    IsHoliday = (float)reader.GetDouble(7)
                });
            }

            return dateFeatures;
        }

        /// <summary>
        /// Loads daily data from a SQL database using the provided SQL command.
        /// </summary>
        /// <param name="connection">The SQL connection to the database.</param>
        /// <param name="cmdSQL">The SQL command to execute.</param>
        /// <returns>A list of daily data retrieved from the database.</returns>
        static List<DailyData> LoadData(SqlConnection connection, string cmdSQL)
        {
            var resultList = new List<DailyData>();

            using var command = new SqlCommand(cmdSQL, connection);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                // Validate that we have at least two columns
                if (reader.FieldCount < 2)
                {
                    throw new InvalidOperationException(
                        "The query must return at least two columns: Date and BaseValue");
                }

                // Safe reading of values
                DateTime date;
                float baseValue;

                try
                {
                    date = reader.GetDateTime(0);
                    baseValue = Convert.ToSingle(reader.GetValue(1));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "Error parsing data from database. Ensure the first column is a valid date and the second column can be converted to a float.",
                        ex);
                }

                resultList.Add(new DailyData
                {
                    Date = date,
                    BaseValue = baseValue
                });
            }

            // Sort the data by date to ensure correct processing
            resultList.Sort((a, b) => a.Date.CompareTo(b.Date));

            return resultList;
        }

        /// <summary>
        /// Adds missing dates to the provided list of daily data, with zero as the base value.
        /// </summary>
        /// <param name="dailyDataList">The list of daily data to which missing dates will be added.</param>
        /// <param name="missingDates">The list of missing dates that need to be added to the daily data list.</param>
        static void AddMissingDatesWithZeroBaseValue(List<DailyData> dailyDataList, List<DateTime> missingDates)
        {
            if (dailyDataList == null)
            {
                throw new ArgumentNullException(nameof(dailyDataList));
            }

            if (missingDates == null || !missingDates.Any())
            {
                // No missing dates to add
                return;
            }

            // Add missing dates with zero base value
            foreach (var missingDate in missingDates)
            {
                dailyDataList.Add(new DailyData
                {
                    Date = missingDate,
                    BaseValue = 0,
                    IsNoData = 1 // Mark as missing data
                });
            }

            // Resort the list to maintain chronological order
            dailyDataList.Sort((a, b) => a.Date.CompareTo(b.Date));
        }

        /// <summary>
        /// Detects the frequency of dates in a given list.
        /// </summary>
        /// <param name="dates">A list of DateTime objects to analyze.</param>
        /// <returns>A DataFrequency value representing the most common difference between consecutive dates.</returns>
        static DataFrequency DetectFrequency(List<DateTime> dates)
        {
            if (dates == null || dates.Count < 2)
            {
                throw new ArgumentException("Need at least two dates to detect frequency.", nameof(dates));
            }

            // Sort the dates to ensure they are in order
            dates = dates.OrderBy(d => d).ToList();

            // Calculate differences between consecutive dates
            var dateDifferences = new List<TimeSpan>();
            for (int i = 1; i < dates.Count; i++)
            {
                dateDifferences.Add(dates[i] - dates[i - 1]);
            }

            // Group by difference and find the most common one
            var frequencyGroups = dateDifferences
                .GroupBy(diff => diff.TotalDays)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key)
                .ToList();

            if (!frequencyGroups.Any())
            {
                return DataFrequency.Irregular;
            }

            // Get the most common difference
            double mostCommonDifference = frequencyGroups.First().Key;

            // Map to DataFrequency enum
            return mostCommonDifference switch
            {
                < 1.5 => DataFrequency.Daily,
                >= 1.5 and < 2.5 => DataFrequency.BiDaily,
                >= 2.5 and < 8 => DataFrequency.Weekly,
                >= 8 and < 15 => DataFrequency.BiWeekly,
                >= 15 and < 32 => DataFrequency.Monthly,
                >= 32 and < 100 => DataFrequency.Quarterly,
                >= 100 and < 200 => DataFrequency.HalfYearly,
                >= 200 and < 400 => DataFrequency.Yearly,
                _ => DataFrequency.Irregular
            };
        }

        /// <summary>
        /// Detects missing dates in the provided list based on the specified data frequency.
        /// </summary>
        /// <param name="dates">A list of dates to check for missing dates.</param>
        /// <param name="frequency">The expected frequency of the dates in the list.</param>
        /// <returns>A list of missing dates according to the specified frequency.</returns>
        static List<DateTime> DetectMissingDates(List<DateTime> dates, DataFrequency frequency)
        {
            if (dates == null || !dates.Any())
            {
                throw new ArgumentException("The date list is empty or null.", nameof(dates));
            }

            // Sort dates and get range
            dates = dates.OrderBy(d => d).ToList();
            DateTime startDate = dates.First();
            DateTime endDate = dates.Last();

            // Get the expected date increment based on frequency
            TimeSpan dateIncrement = GetDateIncrementFromFrequency(frequency);

            // Handle irregular frequency
            if (frequency == DataFrequency.Irregular)
            {
                // For irregular frequencies, calculate the average difference
                var totalDays = (endDate - startDate).TotalDays;
                var expectedCount = dates.Count * 1.1; // Add 10% buffer
                var avgDays = totalDays / expectedCount;
                dateIncrement = TimeSpan.FromDays(avgDays);
            }

            // Build a HashSet of existing dates for O(1) lookups
            var dateSet = new HashSet<DateTime>(dates.Select(d => d.Date));
            var missingDates = new List<DateTime>();

            // Loop through the date range at the specified frequency
            for (var date = startDate.Date; date <= endDate.Date; date = GetNextDate(date, frequency))
            {
                if (!dateSet.Contains(date))
                {
                    missingDates.Add(date);
                }
            }

            return missingDates;
        }

        /// <summary>
        /// Gets the next date based on the specified frequency.
        /// </summary>
        private static DateTime GetNextDate(DateTime date, DataFrequency frequency)
        {
            return frequency switch
            {
                DataFrequency.Daily => date.AddDays(1),
                DataFrequency.BiDaily => date.AddDays(2),
                DataFrequency.Weekly => date.AddDays(7),
                DataFrequency.BiWeekly => date.AddDays(14),
                DataFrequency.Monthly => date.AddMonths(1),
                DataFrequency.Quarterly => date.AddMonths(3),
                DataFrequency.HalfYearly => date.AddMonths(6),
                DataFrequency.Yearly => date.AddYears(1),
                _ => date.AddDays(1) // Default to daily for irregular
            };
        }

        /// <summary>
        /// Gets the date increment TimeSpan based on the specified frequency.
        /// </summary>
        private static TimeSpan GetDateIncrementFromFrequency(DataFrequency frequency)
        {
            return frequency switch
            {
                DataFrequency.Daily => TimeSpan.FromDays(1),
                DataFrequency.BiDaily => TimeSpan.FromDays(2),
                DataFrequency.Weekly => TimeSpan.FromDays(7),
                DataFrequency.BiWeekly => TimeSpan.FromDays(14),
                DataFrequency.Monthly => TimeSpan.FromDays(30),
                DataFrequency.Quarterly => TimeSpan.FromDays(91),
                DataFrequency.HalfYearly => TimeSpan.FromDays(182),
                DataFrequency.Yearly => TimeSpan.FromDays(365),
                _ => TimeSpan.FromDays(1) // Default to daily for irregular
            };
        }

        /// <summary>
        /// Populates daily data with date features.
        /// </summary>
        static void PopulateDailyDataWithDateFeatures(List<DailyData> dailyDataList, List<DateFeature> dateFeatureList)
        {
            if (dailyDataList == null)
            {
                throw new ArgumentNullException(nameof(dailyDataList));
            }

            if (dateFeatureList == null)
            {
                throw new ArgumentNullException(nameof(dateFeatureList));
            }

            // Create a dictionary for O(1) lookups
            var dateFeatureMap = dateFeatureList.ToDictionary(df => df.Date.Date);

            foreach (var dailyData in dailyDataList)
            {
                if (dateFeatureMap.TryGetValue(dailyData.Date.Date, out var dateFeature))
                {
                    // Update dailyData with information from dateFeature
                    dailyData.Year = dateFeature.Year;
                    dailyData.Quarter = dateFeature.Quarter;
                    dailyData.WeekOfYear = dateFeature.WeekOfYear;
                    dailyData.MonthNumber = dateFeature.MonthNumber;
                    dailyData.DayOfWeekNumber = dateFeature.DayOfWeekNumber;
                    dailyData.IsWeekend = dateFeature.IsWeekend;
                    dailyData.IsHoliday = dateFeature.IsHoliday;
                }
                else
                {
                    // If date feature not found, derive basic features from the date
                    dailyData.Year = dailyData.Date.Year;
                    dailyData.Quarter = (float)Math.Ceiling(dailyData.Date.Month / 3.0);
                    dailyData.WeekOfYear = GetIso8601WeekOfYear(dailyData.Date);
                    dailyData.MonthNumber = dailyData.Date.Month;
                    dailyData.DayOfWeekNumber = (float)dailyData.Date.DayOfWeek + 1;
                    dailyData.IsWeekend = (dailyData.Date.DayOfWeek == DayOfWeek.Saturday ||
                                          dailyData.Date.DayOfWeek == DayOfWeek.Sunday) ? 1 : 0;
                    dailyData.IsHoliday = 0; // Default to not a holiday
                }
            }
        }

        /// <summary>
        /// Gets the ISO 8601 week of year for a given date.
        /// </summary>
        private static float GetIso8601WeekOfYear(DateTime date)
        {
            // Implementation of ISO 8601 week calculation
            var day = (int)System.Globalization.CultureInfo.InvariantCulture.Calendar.GetDayOfWeek(date);
            if (day == 0) day = 7; // Sunday = 7, not 0

            // Add 3 days to get Thursday of current week
            date = date.AddDays(4 - day);

            // Get first day of the year
            var startOfYear = new DateTime(date.Year, 1, 1);

            // Calculate day of the year and divide by 7 to get week number
            var weekNumber = (int)Math.Ceiling((date - startOfYear).TotalDays / 7.0) + 1;

            return weekNumber;
        }

        /// <summary>
        /// Imputes missing values in the provided list of daily data using the average value for each weekday and a moving average.
        /// </summary>
        /// <param name="dailyDataList">The list of daily data to impute missing values in.</param>
        /// <param name="movingAverageWindow">The size of the moving average window.</param>
        static void ImputeWithWeekdayAndMovingAverage(List<DailyData> dailyDataList, int movingAverageWindow = 7)
        {
            if (dailyDataList == null)
            {
                throw new ArgumentNullException(nameof(dailyDataList));
            }

            // Sort data by date
            dailyDataList.Sort((a, b) => a.Date.CompareTo(b.Date));

            // Only consider non-zero base values for calculating averages
            var validData = dailyDataList.Where(d => d.BaseValue > 0).ToList();

            if (!validData.Any())
            {
                // No valid data to impute from
                return;
            }

            // Calculate the average BaseValue for each weekday
            var weekdayAverages = validData
                .GroupBy(data => data.Date.DayOfWeek)
                .ToDictionary(
                    group => group.Key,
                    group => group.Average(data => data.BaseValue)
                );

            // Calculate moving averages
            var movingAverages = CalculateMovingAverages(dailyDataList, movingAverageWindow);

            // Calculate seasonal patterns if enough data
            Dictionary<int, float> monthlyAverages = null;
            if (validData.Count >= 60) // At least 2 months of daily data
            {
                monthlyAverages = validData
                    .GroupBy(data => data.Date.Month)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Average(data => data.BaseValue)
                    );
            }

            // Impute missing values
            for (int i = 0; i < dailyDataList.Count; i++)
            {
                var data = dailyDataList[i];
                if (data.BaseValue == 0) // A zero value indicates missing data
                {
                    // Try multiple strategies for imputation
                    float imputedValue = 0;
                    bool valueImputed = false;

                    // 1. Try weekday average
                    if (weekdayAverages.TryGetValue(data.Date.DayOfWeek, out var weekdayAverage))
                    {
                        imputedValue = weekdayAverage;
                        valueImputed = true;

                        // Apply monthly seasonal adjustment if available
                        if (monthlyAverages != null &&
                            monthlyAverages.TryGetValue(data.Date.Month, out var monthlyAvg))
                        {
                            // Calculate average monthly value
                            var avgMonthlyValue = monthlyAverages.Values.Average();
                            if (avgMonthlyValue > 0)
                            {
                                // Apply seasonal factor
                                var seasonalFactor = monthlyAvg / avgMonthlyValue;
                                imputedValue *= seasonalFactor;
                            }
                        }
                    }

                    // 2. If weekday average failed, use moving average
                    if (!valueImputed && i < movingAverages.Count)
                    {
                        imputedValue = movingAverages[i];
                        valueImputed = true;
                    }

                    // 3. If all else fails, use the overall average
                    if (!valueImputed)
                    {
                        imputedValue = validData.Average(d => d.BaseValue);
                    }

                    // Update with imputed value
                    data.BaseValueAdjusted = imputedValue;
                    data.IsNoData = 1; // Flag as imputed data
                }
                else
                {
                    data.BaseValueAdjusted = data.BaseValue;
                    data.IsNoData = 0; // Real data
                }
            }
        }

        /// <summary>
        /// Calculates the moving averages for a given list of daily data.
        /// </summary>
        /// <param name="dataList">The list of daily data for which to calculate the moving averages.</param>
        /// <param name="window">The size of the moving window for the average calculation.</param>
        /// <returns>A list of float values representing the moving averages of the provided data.</returns>
        static List<float> CalculateMovingAverages(List<DailyData> dataList, int window)
        {
            var movingAverages = new List<float>(dataList.Count);

            for (int i = 0; i < dataList.Count; i++)
            {
                int start = Math.Max(i - window / 2, 0);
                int end = Math.Min(i + window / 2, dataList.Count - 1);
                int count = end - start + 1;

                // Only consider non-zero values for the average
                var nonZeroValues = dataList
                    .GetRange(start, count)
                    .Where(data => data.BaseValue > 0)
                    .Select(data => data.BaseValue)
                    .ToList();

                // If there are no non-zero values, use all values
                float average = nonZeroValues.Any()
                    ? nonZeroValues.Average()
                    : dataList.GetRange(start, count).Average(data => data.BaseValue);

                movingAverages.Add(average);
            }

            return movingAverages;
        }

        /// <summary>
        /// Tags the significant differences in the provided list of daily data.
        /// </summary>
        /// <param name="dailyDataList">The list of daily data to analyze.</param>
        static void TagSignificantDifferences(List<DailyData> dailyDataList)
        {
            if (dailyDataList == null || !dailyDataList.Any())
            {
                return;
            }

            // Only consider actual data (not imputed) for threshold calculation
            var actualData = dailyDataList.Where(d => d.IsNoData == 0 && d.BaseValue > 0).ToList();

            if (actualData.Count < 3)
            {
                // Not enough data for meaningful analysis
                return;
            }

            // Calculate differences between predicted and actual
            var differences = actualData.Select(d => d.PredictedValue - d.BaseValue).ToList();

            // Calculate mean and standard deviation of the differences
            float meanDiff = differences.Average();
            float stdDevDiff = (float)Math.Sqrt(
                differences.Select(d => Math.Pow(d - meanDiff, 2)).Average()
            );

            // Calculate relative differences (as percentages)
            var relativeDiffs = actualData
                .Where(d => d.BaseValue > 0)
                .Select(d => Math.Abs((d.PredictedValue - d.BaseValue) / d.BaseValue) * 100)
                .ToList();

            // Calculate mean and standard deviation of relative differences
            float meanRelDiff = relativeDiffs.Any() ? relativeDiffs.Average() : 0;
            float stdDevRelDiff = relativeDiffs.Any()
                ? (float)Math.Sqrt(relativeDiffs.Select(d => Math.Pow(d - meanRelDiff, 2)).Average())
                : 0;

            // Apply adaptive thresholds
            float absThreshold = Math.Max(stdDevDiff * DefaultAnomalyThresholdStdDev, 1.0f);
            float relThreshold = Math.Max(meanRelDiff + stdDevRelDiff * DefaultAnomalyThresholdStdDev, 10.0f);

            // Tag anomalies
            foreach (var data in dailyDataList)
            {
                if (data.IsNoData == 1 || data.BaseValue == 0)
                {
                    // Missing or imputed data is tagged as anomaly
                    data.AnomalyDetected = 1;
                    data.AnomalyReason = "Missing Data";
                }
                else if (Math.Abs(data.PredictedValue - data.BaseValue) > absThreshold)
                {
                    // Absolute difference exceeds threshold
                    data.AnomalyDetected = 1;
                    data.AnomalyReason = "Absolute Difference";
                }
                else if (data.BaseValue > 0 &&
                        (Math.Abs(data.PredictedValue - data.BaseValue) / data.BaseValue * 100) > relThreshold)
                {
                    // Relative difference exceeds threshold
                    data.AnomalyDetected = 1;
                    data.AnomalyReason = "Relative Difference";
                }
                else
                {
                    data.AnomalyDetected = 0;
                    data.AnomalyReason = null;
                }
            }
        }
    }

    /// <summary>
    /// Class to hold health check parameters.
    /// </summary>
    public class HealthCheckParameters
    {
        public int HealthCheckID { get; set; }
        public string TargetDBSchTbl { get; set; }
        public string DateColumn { get; set; }
        public string BaseValueExp { get; set; }
        public string TargetConString { get; set; }
        public string TargetTenantId { get; set; }
        public string TargetApplicationId { get; set; }
        public string TargetClientSecret { get; set; }
        public string TargetKeyVaultName { get; set; }
        public string TargetSecretName { get; set; }
        public string TargetStorageAccountName { get; set; }
        public string TargetBlobContainer { get; set; }
        public string HealthCheckCommand { get; set; }
        public uint MLMaxExperimentTimeInSeconds { get; set; }
        public byte[] ModelData { get; set; }
    }

    /// <summary>
    /// Represents a feature set based on a specific date, used in machine learning models.
    /// </summary>
    public class DateFeature
    {
        public DateTime Date { get; set; }
        public float Year { get; set; }
        public float Quarter { get; set; }
        public float WeekOfYear { get; set; }
        public float MonthNumber { get; set; }
        public float DayOfWeekNumber { get; set; }
        public float IsWeekend { get; set; }
        public float IsHoliday { get; set; }
    }

    /// <summary>
    /// Represents daily data used in the machine learning health check process.
    /// </summary>
    public class DailyData
    {
        [LoadColumn(0)]
        public DateTime Date { get; set; }

        public float BaseValue { get; set; }

        public float PredictedValue { get; set; }

        [LoadColumn(1)]
        public float BaseValueAdjusted { get; set; }

        [LoadColumn(2)]
        public float IsNoData { get; set; }

        [LoadColumn(3)]
        public float Year { get; set; }

        [LoadColumn(4)]
        public float Quarter { get; set; }

        [LoadColumn(5)]
        public float WeekOfYear { get; set; }

        [LoadColumn(6)]
        public float MonthNumber { get; set; }

        [LoadColumn(7)]
        public float DayOfWeekNumber { get; set; }

        [LoadColumn(8)]
        public float IsWeekend { get; set; }

        [LoadColumn(9)]
        public float IsHoliday { get; set; }

        public string trgObject { get; set; }

        public float AnomalyDetected { get; set; }

        public string AnomalyReason { get; set; }

        // Evaluation metrics
        public float EvaluationMAE { get; set; }
        public float EvaluationRMSE { get; set; }
        public float EvaluationMAPE { get; set; }
        public float EvaluationRSquared { get; set; }
    }

    /// <summary>
    /// Represents the prediction result of the daily data.
    /// </summary>
    public class DailyDataPrediction
    {
        [ColumnName("Score")]
        public float PredictedValue { get; set; }
    }

    /// <summary>
    /// Represents the frequency of data in a time series.
    /// </summary>
    public enum DataFrequency
    {
        Daily,
        BiDaily,
        Weekly,
        BiWeekly,
        Monthly,
        Quarterly,
        HalfYearly,
        Yearly,
        Irregular
    }

    /// <summary>
    /// Handles the progress of a regression experiment.
    /// </summary>
    public class RegressionExperimentProgressHandler : IProgress<RunDetail<RegressionMetrics>>
    {
        private readonly List<RunDetail<RegressionMetrics>> _details = new();

        public void Report(RunDetail<RegressionMetrics> runDetail)
        {
            _details.Add(runDetail);
        }

        internal string GetResultsAsJson()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            return JsonSerializer.Serialize(_details, options);
        }
    }
}