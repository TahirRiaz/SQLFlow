using System;
using System.Collections.Generic;
using System.Globalization;

namespace SQLFlowCore.Utils.Period
{
    /// <summary>
    /// Represents date information for a specific country.
    /// </summary>
    internal class DateInfo
    {
        /// <summary>
        /// Maps country names to culture codes.
        /// </summary>
        private static readonly Dictionary<string, string> CountryToCultureMap = Country.CountryToCulture;
        /// <summary>
        /// Maps country names to season maps.
        /// </summary>
        private static readonly Dictionary<string, Dictionary<int, string>> CountrySeasonMap = Country.CountrySeasonMap;
        /// <summary>
        /// The date for which information is needed.
        /// </summary>
        private readonly DateTime _date;
        /// <summary>
        /// The culture information for the country.
        /// </summary>
        private readonly CultureInfo _culture;
        /// <summary>
        /// The name of the country.
        /// </summary>
        private readonly string _countryName;

        /// <summary>
        /// Initializes a new instance of the <see cref="DateInfo"/> class.
        /// </summary>
        /// <param name="date">The date for which information is needed.</param>
        /// <param name="countryName">The name of the country.</param>
        internal DateInfo(DateTime date, string countryName)
        {
            _date = date;
            _countryName = countryName;
            if (CountryToCultureMap.TryGetValue(countryName, out var cultureCode))
            {
                _culture = new CultureInfo(cultureCode);
            }
            else
            {
                throw new ArgumentException($"Unsupported country: {countryName}");
            }
        }

        /// <summary>
        /// Gets the name of the day of the week.
        /// </summary>
        /// <returns>The name of the day of the week.</returns>
        internal string GetDayName()
        {
            return _culture.DateTimeFormat.GetDayName(_date.DayOfWeek);
        }

        /// <summary>
        /// Gets the date as an integer.
        /// </summary>
        /// <returns>The date as an integer.</returns>

        internal int GetDateAsInt()
        {
            string formattedDate = _date.ToString("yyyyMMdd");
            return int.Parse(formattedDate);
        }

        /// <summary>
        /// Gets the abbreviated name of the day of the week.
        /// </summary>
        /// <returns>The abbreviated name of the day of the week.</returns>
        internal string GetShortDayName()
        {
            return _culture.DateTimeFormat.AbbreviatedDayNames[(int)_date.DayOfWeek].Replace(".", "");
        }

        /// <summary>
        /// Gets the name of the month.
        /// </summary>
        /// <returns>The name of the month.</returns>
        internal string GetMonthName()
        {
            return _culture.DateTimeFormat.GetMonthName(_date.Month);
        }

        /// <summary>
        /// Gets the abbreviated name of the month.
        /// </summary>
        /// <returns>The abbreviated name of the month.</returns>
        internal string GetShortMonthName()
        {
            return _culture.DateTimeFormat.AbbreviatedMonthNames[_date.Month - 1].Replace(".", ""); // Subtract 1 because the array is 0-indexed
        }

        /// <summary>
        /// Gets the week number of the year.
        /// </summary>
        /// <returns>The week number of the year.</returns>
        internal int GetWeekNumber()
        {
            Calendar cal = _culture.Calendar;
            CalendarWeekRule weekRule = _culture.DateTimeFormat.CalendarWeekRule;
            DayOfWeek firstDayOfWeek = _culture.DateTimeFormat.FirstDayOfWeek;
            return cal.GetWeekOfYear(_date, weekRule, firstDayOfWeek);
        }

        /// <summary>
        /// Gets the abbreviated name of the month with the month number.
        /// </summary>
        /// <returns>The abbreviated name of the month with the month number.</returns>
        internal string GetShortMonthNameWithNumber()
        {
            string shortMonthName = _culture.DateTimeFormat.AbbreviatedMonthNames[_date.Month - 1].Replace(".", "");
            return $"{_date:MM}-{shortMonthName}";
        }

        /// <summary>
        /// Gets the season of the year.
        /// </summary>
        /// <returns>The season of the year.</returns>
        internal string GetSeason()
        {
            // If the specified country isn't found, default to "United States"
            if (!CountrySeasonMap.TryGetValue(_countryName, out var seasonMap))
            {
                seasonMap = CountrySeasonMap["United States"];
            }
            if (seasonMap.TryGetValue(_date.Month, out var season))
            {
                return season;
            }
            throw new ArgumentException($"Invalid date: {_date}");
        }

        /// <summary>
        /// Gets the quarter of the year.
        /// </summary>
        /// <returns>The quarter of the year.</returns>
        internal string GetQuarterName()
        {
            // You might adjust this to pull from a resource file if you want culture-specific quarter naming.
            switch (_date.Month)
            {
                case 1:
                case 2:
                case 3:
                    return "Q1";
                case 4:
                case 5:
                case 6:
                    return "Q2";
                case 7:
                case 8:
                case 9:
                    return "Q3";
                default: // 10, 11, 12
                    return "Q4";
            }
        }
    }

}
