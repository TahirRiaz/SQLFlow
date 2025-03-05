using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using HtmlAgilityPack;
using TimeAndDate.Services;
using TimeAndDate.Services.DataTypes.Holidays;
using TimeAndDate.Services.DataTypes.Time;
using System.Net;
using SQLFlowCore.Common;
using SQLFlowCore.Services.AzureResources;

namespace SQLFlowCore.Utils.Period
{
    /// <summary>
    /// Represents the service information for a specific service.
    /// </summary>
    internal class ServiceInfo
    {
        /// <summary>
        /// Gets or sets the type of the service.
        /// </summary>
        internal string ServiceType { get; set; }
        /// <summary>
        /// Gets or sets the alias of the API key.
        /// </summary>
        internal string ApiKeyAlias { get; set; }
        /// <summary>
        /// Gets or sets the access key for the service.
        /// </summary>
        internal string AccessKey { get; set; }
        /// <summary>
        /// Gets or sets the secret key for the service.
        /// </summary>
        internal string SecretKey { get; set; }
        /// <summary>
        /// Gets or sets the name of the key vault.
        /// </summary>
        internal string KeyVaultName { get; set; }
        /// <summary>
        /// Gets or sets the name of the secret.
        /// </summary>
        internal string SecretName { get; set; }
    }


    /// <summary>
    /// Represents a period dimension in SQLFlow. This class is responsible for building a DataTable that represents a period dimension.
    /// </summary>
    /// <remarks>
    /// The period dimension includes various time-related attributes such as date, day of month, day of week, week of year, month number, quarter, year, and various fiscal and holiday-related attributes.
    /// </remarks>
    public class DimPeriod
    {
        private readonly DateTime _startDate;
        private readonly DateTime _endDate;
        private readonly int _fiscalYearStartMonth;
        private readonly string _apiKeyAlias;
        private readonly string _country;
        private readonly string _eventLang;
        private readonly string _connString;
        private readonly ServiceInfo si;

        /// <summary>
        /// Initializes a new instance of the <see cref="DimPeriod"/> class.
        /// </summary>
        /// <param name="SqlFlowConStr">The SQLFlow connection string.</param>
        /// <param name="startDate">The start date of the period.</param>
        /// <param name="endDate">The end date of the period.</param>
        /// <param name="fiscalYearStartMonth">The start month of the fiscal year.</param>
        /// <param name="country">The country for which the period dimension is being built.</param>
        /// <param name="eventLang">The language for the events.</param>
        /// <param name="apiKeyAlias">The API key alias.</param>
        public DimPeriod(string SqlFlowConStr, DateTime startDate, DateTime endDate, int fiscalYearStartMonth, string country, string eventLang = "", string apiKeyAlias = "")
        {
            _connString = SqlFlowConStr;
            _startDate = startDate;
            _endDate = endDate;
            _country = country;
            _fiscalYearStartMonth = fiscalYearStartMonth;
            _apiKeyAlias = apiKeyAlias;
            _country = country;
            _eventLang = eventLang;

            si = new ServiceInfo();
            si.ApiKeyAlias = apiKeyAlias;

            GetServiceTypeAndAPIKeyByAlias(_connString, si);
        }

        internal void GetServiceTypeAndAPIKeyByAlias(string connString, ServiceInfo info)
        {
            using (SqlConnection connection = new SqlConnection(_connString))
            {
                connection.Open();

                // SQL command with parameterized query
                string query = @"SELECT ak.ApiKeyID,
                                           ak.ServiceType,
                                           ak.ApiKeyAlias,
                                           ak.AccessKey,
                                           ak.SecretKey,

                                           

                                           v.TenantId AS trgTenantId,
                                           v.SubscriptionId AS trgSubscriptionId,
                                           v.ApplicationId AS trgApplicationId,
                                           v.ClientSecret AS trgClientSecret,
                                           v.KeyVaultName AS trgKeyVaultName,
                                           v.SecretName AS trgSecretName,
                                           v.ResourceGroup AS trgResourceGroup,
                                           v.DataFactoryName AS trgDataFactoryName,
                                           v.AutomationAccountName AS trgAutomationAccountName,
                                           v.StorageAccountName AS trgStorageAccountName,
                                           v.BlobContainer AS trgBlobContainer, 

                                           ak.KeyVaultSecretName AS SecretName
                                    FROM flw.SysAPIKey AS ak
                                        LEFT OUTER JOIN [flw].[SysServicePrincipal] AS v
                                            ON ak.ServicePrincipalAlias = v.ServicePrincipalAlias
                                    WHERE [APIKeyAlias] = @alias";
                using (SqlCommand cmd = new SqlCommand(query, connection))
                {
                    string trgTenantId = string.Empty;
                    string trgSubscriptionId = string.Empty;
                    string trgApplicationId = string.Empty;
                    string trgClientSecret = string.Empty;
                    string trgKeyVaultName = string.Empty;
                    string trgSecretName = string.Empty;
                    string trgResourceGroup = string.Empty;
                    string trgDataFactoryName = string.Empty;
                    string trgAutomationAccountName = string.Empty;
                    string trgStorageAccountName = string.Empty;
                    string trgBlobContainer = string.Empty;

                    cmd.Parameters.AddWithValue("@alias", info.ApiKeyAlias);

                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            trgTenantId = reader["trgTenantId"]?.ToString() ?? string.Empty;
                            trgApplicationId = reader["trgApplicationId"]?.ToString() ?? string.Empty;
                            trgClientSecret = reader["trgClientSecret"]?.ToString() ?? string.Empty;
                            trgKeyVaultName = reader["trgKeyVaultName"]?.ToString() ?? string.Empty;
                            trgSecretName = reader["trgSecretName"]?.ToString() ?? string.Empty;
                            trgStorageAccountName = reader["trgStorageAccountName"]?.ToString() ?? string.Empty;
                            trgBlobContainer = reader["trgBlobContainer"]?.ToString() ?? string.Empty;

                            info.ServiceType = reader["ServiceType"]?.ToString() ?? string.Empty;
                            info.AccessKey = reader["AccessKey"]?.ToString() ?? string.Empty;
                            info.SecretKey = reader["SecretKey"]?.ToString() ?? string.Empty;

                            info.KeyVaultName = reader["KeyVaultName"]?.ToString() ?? string.Empty;
                            info.SecretName = reader["SecretName"]?.ToString() ?? string.Empty;

                            if (info.SecretName.Length > 0)
                            {
                                //AzureKeyVaultManager keyVaultManager = new AzureKeyVaultManager(info.KeyVaultName);
                                AzureKeyVaultManager trgKeyVaultManager = new AzureKeyVaultManager(
                                    trgTenantId,
                                    trgApplicationId,
                                    trgClientSecret,
                                    trgKeyVaultName);
                                info.SecretKey = trgKeyVaultManager.GetSecret(trgSecretName);
                            }

                        }
                        else
                        {
                            throw new Exception($"No record found for APIKeyAlias: {info.ApiKeyAlias}");
                        }
                    }
                }

                connection.Close();
                connection.Dispose();
            }
        }

        /// <summary>
        /// Builds a DataTable with various date-related columns for each day in the range from the start date to the end date.
        /// </summary>
        /// <remarks>
        /// The DataTable includes columns for the date, day of the month, day of the week, week of the year, month, quarter, year, and various other date-related attributes.
        /// It also includes fiscal week, month, quarter, and year, as well as holiday information.
        /// </remarks>
        /// <returns>A DataTable with a row for each day in the date range and columns for various date-related attributes.</returns>
        public DataTable BuildDt()
        {
            var table = new DataTable();
            table.Columns.Add("PeriodID", typeof(int));
            table.Columns.Add("Date", typeof(DateTime));
            table.Columns.Add("DayOfMonth", typeof(int));
            table.Columns.Add("DayOfWeekName", typeof(string));
            table.Columns.Add("DayOfWeekNameShort", typeof(string));
            table.Columns.Add("DayOfWeekNumber", typeof(int));
            table.Columns.Add("WeekOfYear", typeof(int));
            table.Columns.Add("MonthNumber", typeof(int));
            table.Columns.Add("MonthName", typeof(string));
            table.Columns.Add("MonthNameShort", typeof(string));
            table.Columns.Add("MonthNumName", typeof(string));
            table.Columns.Add("Quarter", typeof(string));
            table.Columns.Add("Year", typeof(int));
            table.Columns.Add("IsWeekend", typeof(bool));
            table.Columns.Add("IsLeapYear", typeof(bool));
            table.Columns.Add("IsLastDayOfMonth", typeof(bool));

            // Advanced column definitions  
            table.Columns.Add("FiscalWeekOfYear", typeof(int));
            table.Columns.Add("FiscalMonth", typeof(int));
            table.Columns.Add("FiscalQuarter", typeof(int));
            table.Columns.Add("FiscalYear", typeof(int));
            table.Columns.Add("IsHoliday", typeof(bool));
            table.Columns.Add("HolidayName", typeof(string));
            table.Columns.Add("Season", typeof(string));
            table.Columns.Add("DaylightSavingTime", typeof(bool));
            table.Columns.Add("ISOWeekNumber", typeof(int));


            for (var date = _startDate; date <= _endDate; date = date.AddDays(1))
            {
                DateInfo dateInfo = new DateInfo(date, _country);
                var row = table.NewRow();
                row["PeriodID"] = dateInfo.GetDateAsInt();
                row["Date"] = date;
                row["DayOfMonth"] = date.Day;
                row["DayOfWeekName"] = dateInfo.GetDayName();
                row["DayOfWeekNameShort"] = dateInfo.GetShortDayName();
                row["DayOfWeekNumber"] = (int)date.DayOfWeek;
                row["WeekOfYear"] = dateInfo.GetWeekNumber();
                row["MonthNumber"] = date.Month;
                row["MonthName"] = dateInfo.GetMonthName();
                row["MonthNameShort"] = dateInfo.GetShortMonthName();
                row["MonthNumName"] = dateInfo.GetShortMonthNameWithNumber();
                row["Quarter"] = dateInfo.GetQuarterName();
                row["Year"] = date.Year;
                row["IsWeekend"] = date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;
                row["IsLeapYear"] = DateTime.IsLeapYear(date.Year);
                row["IsLastDayOfMonth"] = date.Day == DateTime.DaysInMonth(date.Year, date.Month);
                row["Season"] = dateInfo.GetSeason();
                row["FiscalWeekOfYear"] = CalculateFiscalWeekOfYear(date);
                row["FiscalMonth"] = CalculateFiscalMonth(date);
                row["FiscalQuarter"] = CalculateFiscalQuarter(date);
                row["FiscalYear"] = CalculateFiscalYear(date);
                row["DaylightSavingTime"] = IsDaylightSavingTime(date);
                row["ISOWeekNumber"] = GetISOWeekNumber(date);

                table.Rows.Add(row);

            }

            UpdateHolidayColumns(table);

            return table;
        }


        /// <summary>
        /// Updates the "IsHoliday" and "HolidayName" columns in the provided DataTable.
        /// </summary>
        /// <param name="mainTable">The DataTable to be updated. This table should have columns named "IsHoliday" and "HolidayName".</param>
        /// <remarks>
        /// This method fetches holiday data for each distinct year present in the mainTable. The source of the holiday data is determined by the ServiceType property of the ServiceInfo instance.
        /// If the ServiceType is "Google", it fetches the holiday data from Google API.
        /// If the ServiceType is "timeanddate.com", it fetches the holiday data from timeanddate.com API.
        /// If the ServiceType is "Web Scraping", it fetches the holiday data by web scraping.
        /// After fetching the holiday data, it updates the "IsHoliday" and "HolidayName" columns in the mainTable for the corresponding dates.
        /// </remarks>
        internal void UpdateHolidayColumns(DataTable mainTable)
        {
            var distinctYears = mainTable.AsEnumerable()
                .Select(row => row.Field<int>("Year"))
                .Distinct()
                .ToList();

            foreach (int year in distinctYears)
            {
                if (si.ServiceType == "Google")
                {
                    DataTable holidaysForYear = FetchHolidaysFromGoogleAPI(year);

                    // Update the mainTable with the fetched holiday data
                    foreach (DataRow holidayRow in holidaysForYear.Rows)
                    {
                        DateTime holidayDate = (DateTime)holidayRow["startDate"];
                        string holidayName = holidayRow["eventSummary"]?.ToString() ?? string.Empty;

                        DataRow[] mainTableRows = mainTable.Select($"Date = '{holidayDate.ToString("yyyy-MM-dd")}'");

                        foreach (DataRow mainRow in mainTableRows)
                        {
                            mainRow["IsHoliday"] = true;
                            mainRow["HolidayName"] = holidayName;
                        }
                    }
                }
                else if (si.ServiceType == "timeanddate.com")
                {
                    if (year == DateTime.Now.Year)
                    {
                        var countryAlpha2 = DictionaryUtilities.GetKeyFromValue(Country.CountryIso3166Alpha2, _country).ToLower();
                        var service = new HolidaysService(si.AccessKey, si.SecretKey);
                        service.Language = _eventLang;
                        IList<Holiday> result = service.HolidaysForCountry(countryAlpha2, 2023);

                        if (result != null)
                        {
                            foreach (Holiday h in result)
                            {
                                DateTime hDate = ToDateTime(h.Date.DateTime);

                                DataRow[] mainTableRows = mainTable.Select($"Date = '{hDate.ToString("yyyy-MM-dd")}'");

                                foreach (DataRow mainRow in mainTableRows)
                                {
                                    mainRow["IsHoliday"] = true;
                                    mainRow["HolidayName"] = h.Name;
                                }
                            }
                        }
                    }
                }
                else if (si.ServiceType == "Web Scraping")
                {
                    var countryAlpha2 = DictionaryUtilities.GetKeyFromValue(Country.CountryIso3166Alpha2, _country).ToLower();

                    string basePage = "";
                    string url = si.AccessKey + year.ToString();
                    try
                    {
                        var handler = new HttpClientHandler
                        {
                            UseCookies = true,
                            CookieContainer = new CookieContainer()
                        };

                        HttpClient httpClient = new HttpClient(handler);
                        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
                        httpClient.Timeout = TimeSpan.FromSeconds(30);
                        var response = httpClient.GetAsync(url).Result;

                        basePage = response.Content.ReadAsStringAsync().Result;

                        //response.EnsureSuccessStatusCode();  // Throws an exception if not a success status code

                    }
                    catch (HttpRequestException e)
                    {
                        // Handle exception
                        Console.WriteLine($"Request error: {e.Message}");

                    }

                    string htmlData = ExtractHolidaysTable(basePage);
                    DataTable resDT = ParseHtmlToDataTable(htmlData, year);


                    foreach (DataRow holidayRow in resDT.Rows)
                    {
                        DateTime holidayDate = (DateTime)holidayRow["Date"];
                        string holidayName = holidayRow["Navn"]?.ToString() ?? string.Empty;

                        DataRow[] mainTableRows = mainTable.Select($"Date = '{holidayDate.ToString("yyyy-MM-dd")}'");

                        foreach (DataRow mainRow in mainTableRows)
                        {
                            mainRow["IsHoliday"] = true;
                            mainRow["HolidayName"] = holidayName;
                        }
                    }

                }
            }
        }

        internal DateTime ToDateTime(TADDateTime baseDate)
        {
            return new DateTime(baseDate.Year, baseDate.Month, baseDate.Day, baseDate.Hour, baseDate.Minute, baseDate.Second);
        }


        /// <summary>
        /// Extracts the HTML content of the holidays table from the given HTML content.
        /// </summary>
        /// <param name="htmlContent">The HTML content to extract the holidays table from.</param>
        /// <returns>The HTML content of the holidays table, or null if the table is not found.</returns>
        internal string ExtractHolidaysTable(string htmlContent)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(htmlContent);

            var tableNode = doc.DocumentNode.SelectSingleNode("//table[@id='holidays-table']");
            return tableNode?.OuterHtml;
        }

        /// <summary>
        /// Parses the HTML string into a DataTable.
        /// </summary>
        /// <param name="html">The HTML string to parse.</param>
        /// <param name="year">The year for which the data is being parsed.</param>
        /// <returns>A DataTable containing the parsed data.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the number of columns in the header does not match the number of cells in a row.</exception>
        internal DataTable ParseHtmlToDataTable(string html, int year)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var dataTable = new DataTable();

            // Add columns from <thead>
            var header = doc.DocumentNode.SelectSingleNode("//table[@id='holidays-table']/thead/tr");
            foreach (var headerNode in header.ChildNodes.Where(n => n.Name == "th"))
            {
                dataTable.Columns.Add(headerNode.InnerText.Trim());
            }

            // Fetch rows from <tbody>
            var rows = doc.DocumentNode.SelectNodes("//table[@id='holidays-table']/tbody/tr");
            foreach (var rowNode in rows)
            {
                if (rowNode.GetAttributeValue("id", "").Contains("tr"))
                {
                    var dataCells = rowNode.ChildNodes.Where(n => n.Name == "th" || n.Name == "td").ToList();
                    var data = dataCells.Select(cell =>
                    {
                        // Since the first cell can be a th or td, handling the image scenario for both
                        if ((cell.Name == "td" || cell.Name == "th") && cell.FirstChild != null && cell.FirstChild.Name == "img")
                        {
                            return HtmlEntity.DeEntitize(cell.FirstChild.Attributes["src"]?.Value) ?? string.Empty;
                        }
                        return HtmlEntity.DeEntitize(cell.InnerText.Trim());
                    }).ToArray();

                    if (data.Length != dataTable.Columns.Count)
                    {
                        throw new InvalidOperationException("Number of columns in <thead> does not match number of cells in a row of <tbody>.");
                    }

                    dataTable.Rows.Add(data);
                }
            }

            dataTable.Columns[0].ColumnName = "Dato";
            dataTable.Columns[1].ColumnName = "Dag";
            dataTable.Columns[2].ColumnName = "Navn";
            dataTable.Columns[3].ColumnName = "Type";
            dataTable.Columns[4].ColumnName = "Flagdag";

            DataColumn dc = new DataColumn("Date", typeof(DateTime));
            dataTable.Columns.Add(dc);

            foreach (DataRow dr in dataTable.Rows)
            {
                dr["Date"] = ConvertToDate(dr["Dato"].ToString(), year);
            }

            return dataTable;
        }

        /// <summary>
        /// Converts a date string and a year into a DateTime object.
        /// </summary>
        /// <param name="dateString">The date string in the format "d. MMM".</param>
        /// <param name="year">The year as an integer.</param>
        /// <returns>A DateTime object representing the parsed date if successful, or DBNull.Value if the parsing fails.</returns>
        private object ConvertToDate(string dateString, int year)
        {
            DateTime parsedDate;
            var fullDateString = $"{dateString} {year}";

            var norwegianCulture = new CultureInfo("nb-NO");

            if (DateTime.TryParseExact(fullDateString, "d. MMM yyyy", norwegianCulture, DateTimeStyles.None, out parsedDate))
            {
                return parsedDate;
            }
            return DBNull.Value;
        }

        /// <summary>
        /// Fetches the holiday data for a specific year from the Google Calendar API.
        /// </summary>
        /// <param name="year">The year for which the holiday data is to be fetched.</param>
        /// <returns>A DataTable containing the fetched holiday data.</returns>
        private DataTable FetchHolidaysFromGoogleAPI(int year)
        {
            var startDate = new DateTime(year, 1, 1);
            var endDate = new DateTime(year, 12, 31, 23, 59, 59);

            string baseUrl = "https://www.googleapis.com/calendar/v3/calendars";
            string id = Country.GoogleCountryHoliday[_country].Substring(3);
            using HttpClient httpClient = new HttpClient();
            var uri = $"{baseUrl}/{_eventLang}.{id}/events?key={si.SecretKey}&timeMin={startDate:yyyy-MM-ddTHH:mm:ssZ}&timeMax={endDate:yyyy-MM-ddTHH:mm:ssZ}";

            var response = httpClient.GetStringAsync(uri).Result;

            DataTable dataTable = ConvertJsonToDataTable(response);

            return dataTable;
        }

        /// <summary>
        /// Converts a JSON string into a DataTable.
        /// </summary>
        /// <param name="json">The JSON string to convert.</param>
        /// <returns>A DataTable that represents the JSON string.</returns>
        /// <remarks>
        /// The method parses the JSON string and creates a DataTable with columns for each property in the JSON object. 
        /// The method also handles nested JSON objects and arrays by creating additional rows in the DataTable for each item in the array.
        /// </remarks>
        DataTable ConvertJsonToDataTable(string json)
        {
            var jsonObject = JObject.Parse(json);
            var items = jsonObject["items"] as JArray;
            var culture = CultureInfo.InvariantCulture;

            var dataTable = new DataTable();

            // Top-level properties with appropriate data types
            dataTable.Columns.Add("kind", typeof(string));
            dataTable.Columns.Add("etag", typeof(string));
            dataTable.Columns.Add("summary", typeof(string));
            dataTable.Columns.Add("description", typeof(string));
            dataTable.Columns.Add("updated", typeof(DateTime));
            dataTable.Columns.Add("timeZone", typeof(string));
            dataTable.Columns.Add("accessRole", typeof(string));

            // Event details with appropriate data types
            dataTable.Columns.Add("eventId", typeof(string));
            dataTable.Columns.Add("eventStatus", typeof(string));
            dataTable.Columns.Add("eventHtmlLink", typeof(string));
            dataTable.Columns.Add("eventCreated", typeof(DateTime));
            dataTable.Columns.Add("eventUpdated", typeof(DateTime));
            dataTable.Columns.Add("eventSummary", typeof(string));
            dataTable.Columns.Add("eventDescription", typeof(string));
            dataTable.Columns.Add("creatorEmail", typeof(string));
            dataTable.Columns.Add("creatorDisplayName", typeof(string));
            dataTable.Columns.Add("organizerEmail", typeof(string));
            dataTable.Columns.Add("organizerDisplayName", typeof(string));
            dataTable.Columns.Add("startDate", typeof(DateTime));
            dataTable.Columns.Add("endDate", typeof(DateTime));
            dataTable.Columns.Add("transparency", typeof(string));
            dataTable.Columns.Add("visibility", typeof(string));
            dataTable.Columns.Add("iCalUID", typeof(string));
            dataTable.Columns.Add("sequence", typeof(int));
            dataTable.Columns.Add("eventType", typeof(string));

            foreach (var item in items)
            {
                DataRow row = dataTable.NewRow();

                // Top-level properties
                row["kind"] = jsonObject["kind"]?.ToString() ?? string.Empty;
                row["etag"] = jsonObject["etag"]?.ToString() ?? string.Empty;
                row["summary"] = jsonObject["summary"]?.ToString() ?? string.Empty;
                row["description"] = jsonObject["description"]?.ToString() ?? string.Empty;

                row["updated"] = TryParseDateTime(item["updated"]?.ToString() ?? string.Empty);


                //row["updated"] = DateTime.ParseExact(jsonObject["updated"].ToString(), dateTimeFormat, culture);
                row["timeZone"] = jsonObject["timeZone"]?.ToString() ?? string.Empty;
                row["accessRole"] = jsonObject["accessRole"]?.ToString() ?? string.Empty;

                // Event details
                row["eventId"] = item["id"]?.ToString() ?? string.Empty;
                row["eventStatus"] = item["status"]?.ToString() ?? string.Empty;
                row["eventHtmlLink"] = item["htmlLink"]?.ToString() ?? string.Empty;
                row["eventCreated"] = TryParseDateTime(item["eventCreated"]?.ToString());
                row["eventUpdated"] = TryParseDateTime(item["eventUpdated"]?.ToString());
                row["eventSummary"] = item["summary"]?.ToString() ?? string.Empty;
                row["eventDescription"] = item["description"]?.ToString() ?? string.Empty;
                row["creatorEmail"] = item["creator"]["email"]?.ToString() ?? string.Empty;
                row["creatorDisplayName"] = item["creator"]["displayName"]?.ToString() ?? string.Empty;
                row["organizerEmail"] = item["organizer"]["email"]?.ToString() ?? string.Empty;
                row["organizerDisplayName"] = item["organizer"]["displayName"]?.ToString() ?? string.Empty;
                row["startDate"] = TryParseDateTime(item["start"]["date"]?.ToString() ?? string.Empty);
                row["endDate"] = TryParseDateTime(item["end"]["date"]?.ToString() ?? string.Empty);
                row["transparency"] = item["transparency"]?.ToString() ?? string.Empty;
                row["visibility"] = item["visibility"]?.ToString() ?? string.Empty;
                row["iCalUID"] = item["iCalUID"]?.ToString() ?? string.Empty;
                row["sequence"] = int.Parse(item["sequence"]?.ToString() ?? string.Empty);
                row["eventType"] = item["eventType"]?.ToString() ?? string.Empty;

                dataTable.Rows.Add(row);
            }

            return dataTable;
        }

        /// <summary>
        /// Tries to parse a date string into a DateTime object using various common date formats.
        /// </summary>
        /// <param name="dateString">The string containing the date to parse.</param>
        /// <returns>A DateTime object if the parsing is successful, otherwise returns DBNull.Value.</returns>
        object TryParseDateTime(string dateString)
        {
            if (dateString != null)
            {
                // List of date formats you expect to encounter
                var dateFormats = new List<string>
        {
            "yyyy-MM-ddTHH:mm:ss.fffZ",      // ISO 8601 format
            "dd.MM.yyyy HH:mm:ss",           // Example provided in the error
            "yyyy-MM-dd",                    // Date only format
            "MM/dd/yyyy HH:mm:ss",           // Another common format
            "MM/dd/yyyy",                    // US Date format
            // Add more formats as necessary
        };

                DateTime parsedDate;
                foreach (var format in dateFormats)
                {
                    if (DateTime.TryParseExact(dateString, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsedDate))
                    {
                        return parsedDate;
                    }
                }
            }

            // Return DBNull if none of the formats match
            return DBNull.Value;
        }


        /// <summary>
        /// Checks if the provided date is in Daylight Saving Time according to the local time zone.
        /// </summary>
        /// <param name="date">The date to check.</param>
        /// <returns>Returns true if the date is in Daylight Saving Time, false otherwise.</returns>
        private bool IsDaylightSavingTime(DateTime date)
        {
            return TimeZoneInfo.Local.IsDaylightSavingTime(date);
        }


        internal string GetShortDateDescription(DateTime date)
        {
            return date.ToString("d");
        }


        /// <summary>
        /// Calculates the ISO week number for a given date.
        /// </summary>
        /// <param name="date">The date for which to calculate the ISO week number.</param>
        /// <returns>The ISO week number of the provided date.</returns>
        /// <remarks>
        /// The ISO week number is defined as the first week of the year that contains at least 4 days. 
        /// The week starts on Monday. If the first calendar week of the year has less than 4 days, 
        /// the week is considered the last week of the previous year.
        /// </remarks>
        internal int GetISOWeekNumber(DateTime date)
        {
            DayOfWeek day = CultureInfo.InvariantCulture.Calendar.GetDayOfWeek(date);
            if (day >= DayOfWeek.Monday && day <= DayOfWeek.Wednesday)
            {
                date = date.AddDays(3);
            }

            return CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(date, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
        }

        internal string GetLongDateDescription(DateTime date)
        {
            return date.ToLongDateString();
        }

        /// <summary>
        /// Calculates the fiscal week of the year for a given date.
        /// </summary>
        /// <param name="date">The date for which the fiscal week of the year is to be calculated.</param>
        /// <returns>The fiscal week of the year as an integer.</returns>
        /// <remarks>
        /// The fiscal year is assumed to start on the month specified by the _fiscalYearStartMonth field. 
        /// Week 1 of the fiscal year is considered to be the week that includes the first day of the fiscal year.
        /// If the given date falls into the last week of the previous fiscal year, this method returns 0.
        /// </remarks>
        private int CalculateFiscalWeekOfYear(DateTime date)
        {
            // Assuming fiscal year starts on the _fiscalYearStartMonth  
            DateTime fiscalYearStart;
            if (date.Month < _fiscalYearStartMonth || date.Month == _fiscalYearStartMonth && date.Day < 1)
                fiscalYearStart = new DateTime(date.Year - 1, _fiscalYearStartMonth, 1);
            else
                fiscalYearStart = new DateTime(date.Year, _fiscalYearStartMonth, 1);

            // Get the start of the week for the given date, assuming weeks start on Monday  
            var startOfWeek = date.AddDays(-(int)date.DayOfWeek + (int)DayOfWeek.Monday);

            // Week 1 of the fiscal year is the week that includes the first day of the fiscal year  
            if (startOfWeek < fiscalYearStart)
                return 0;   // This day falls into the last week of the previous fiscal year  
            else
                return (startOfWeek - fiscalYearStart).Days / 7 + 1;
        }

        /// <summary>
        /// Calculates the fiscal month based on the provided date.
        /// </summary>
        /// <param name="date">The date for which the fiscal month is to be calculated.</param>
        /// <returns>The fiscal month corresponding to the provided date.</returns>
        private int CalculateFiscalMonth(DateTime date)
        {
            int fiscalMonth = (date.Month - _fiscalYearStartMonth + 11) % 12 + 1;
            return fiscalMonth;
        }

        /// <summary>
        /// Calculates the fiscal quarter for a given date.
        /// </summary>
        /// <param name="date">The date for which the fiscal quarter is to be calculated.</param>
        /// <returns>The fiscal quarter of the given date.</returns>
        private int CalculateFiscalQuarter(DateTime date)
        {
            // Calculate fiscal month first  
            int fiscalMonth = CalculateFiscalMonth(date);

            // Determine fiscal quarter based on fiscal month  
            int fiscalQuarter = (fiscalMonth - 1) / 3 + 1;

            return fiscalQuarter;
        }

        /// <summary>
        /// Calculates the fiscal year for a given date.
        /// </summary>
        /// <param name="date">The date for which to calculate the fiscal year.</param>
        /// <returns>The calculated fiscal year.</returns>
        /// <remarks>
        /// The fiscal year is calculated based on the assumption that the fiscal year starts on the _fiscalYearStartMonth. 
        /// If the month of the date is less than _fiscalYearStartMonth or the date is the first day of _fiscalYearStartMonth, 
        /// the fiscal year is the same as the year of the date. Otherwise, the fiscal year is the year of the date plus one.
        /// </remarks>
        private int CalculateFiscalYear(DateTime date)
        {
            // Assuming fiscal year starts on the _fiscalYearStartMonth  
            int fiscalYear;
            if (date.Month < _fiscalYearStartMonth || date.Month == _fiscalYearStartMonth && date.Day < 1)
                fiscalYear = date.Year;
            else
                fiscalYear = date.Year + 1;

            return fiscalYear;
        }



    }

}
