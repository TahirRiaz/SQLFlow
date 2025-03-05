using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;


namespace SQLFlowCore.Common
{
    internal static class Functions
    {
        internal enum StatsParameter
        {
            Fetched,
            Inserted,
            Updated,
            Deleted
        }

        internal static int GetStableHash(string input)
        {
            unchecked
            {
                int hash = 17;
                foreach (char c in input)
                {
                    hash = hash * 23 + c;
                }
                return hash;
            }
        }

        internal static string CleanupColumns(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            // Split by comma. If there's no comma, it will return an array with a single item.
            string[] items = value.Split(',');

            for (int i = 0; i < items.Length; i++)
            {
                items[i] = items[i].Trim().Replace("[", "").Replace("]", "");
            }

            return string.Join(",", items);
        }

        public static string FindCommonColumns(string firstColList, string secondColList)
        {
            List<ObjectName> firstCols = CommonDB.ParseObjectNames(firstColList);
            List<ObjectName> secondCols = CommonDB.ParseObjectNames(secondColList);

            // Find common columns between the two lists based on QuotedColumn property
            var commonCols = firstCols.Select(fc => fc.QuotedName)
                .Intersect(secondCols.Select(sc => sc.QuotedName));

            // Return the result as a comma-separated string
            return string.Join(",", commonCols);
        }

        internal static bool IsInList(List<string> List, string Value)
        {
            bool rValue = false;

            foreach (string item in List)
            {
                if (item.Equals(Value, StringComparison.InvariantCultureIgnoreCase))
                {
                    rValue = true;
                }
            }
            return rValue;
        }

        internal static List<string> AddItemsToList(List<string> srclist, List<string> list)
        {
            if (srclist == null)
            {
                throw new ArgumentNullException(nameof(srclist), "Source list cannot be null.");
            }

            if (list == null)
            {
                throw new ArgumentNullException(nameof(list), "List to add cannot be null.");
            }

            List<string> resultList = new List<string>(srclist); // Create a new list initialized with the items from srclist

            foreach (string item in list)
            {
                if (!IsInList(resultList, item))
                {
                    resultList.Add(item); // Add to the new list if it's not already present
                }
            }

            return resultList; // Return the new list containing all items
        }


        internal static List<string> RemoveItemsFromList(List<string> srclist, List<string> list)
        {
            List<string> itemsToRemove = new List<string>(); // Create a new list for items to be removed

            foreach (string item in list)
            {
                if (IsInList(srclist, item))
                {
                    itemsToRemove.Add(item); // Add to the temporary list instead of removing immediately
                }
            }

            foreach (string item in itemsToRemove)
            {
                srclist.Remove(item); // Remove all items in the temporary list from the original list
            }

            return srclist;
        }


        internal static Dictionary<string, string> BuildDictionary(string list1, string list2)
        {
            if (list1 == null || list2 == null)
            {
                throw new ArgumentNullException("Both input lists must be non-null.");
            }

            List<ObjectName> olist1 = CommonDB.ParseObjectNames(list1);
            List<ObjectName> olist2 = CommonDB.ParseObjectNames(list2);

            if (olist1.Count != olist2.Count)
            {
                throw new ArgumentException("Both input lists must have the same number of items.");
            }

            return olist1.Zip(olist2, (item1, item2) => new { Key = item1.UnquotedName, Value = item2.UnquotedName })
                .ToDictionary(item => item.Key, item => item.Value);
        }

        internal static string RemoveBrackets(string SrcString)
        {
            SrcString = SrcString.Replace("[", "").Replace("]", "");
            return SrcString;
        }

        internal static List<string> GetStatsParams()
        {
            List<string> rValue = new List<string>();

            foreach (var p in Enum.GetNames(typeof(StatsParameter)))
            {
                rValue.Add("@" + p.ToLower());
            }
            return rValue;
        }

        internal static string ExtractFolderDividers(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "";
            }

            HashSet<char> dividers = new HashSet<char>();

            foreach (char c in path)
            {
                if (c == '/')
                {
                    dividers.Add('/');
                }
                else if (c == '\\')
                {
                    dividers.Add('\\');
                }
            }

            return string.Join("", dividers);
        }


        internal static string EnsureEndingSlash(string path, string devider)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "";
            }

            char lastChar = path[path.Length - 1];
            char endingSlash;

            if (lastChar == '/' || lastChar == '\\')
            {
                return path;
            }

            if (path.Contains("/"))
            {
                endingSlash = '/';
            }
            else if (path.Contains("\\"))
            {
                endingSlash = '\\';
            }
            else if (devider.Length > 0)
            {
                endingSlash = devider.ToCharArray()[0];
            }
            else
            {
                endingSlash = Path.DirectorySeparatorChar;
            }

            return path + endingSlash;
        }


        internal static string GetFullPathWithEndingSlashes(string input)
        {
            string fullPath = Path.GetFullPath(input);
            return fullPath
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
        }

        internal static string GetValidFileDirName(string input)
        {
            foreach (char ch in Path.GetInvalidFileNameChars())
            {
                input = input.Replace(ch, ' ');
            }
            return input;
        }

        internal static DataTable GetMinIncTblFromSrcTrg(DataTable srcIncTbl, DataTable trgIncTbl, bool FetchMinValuesFromSrc, DataTable DateTimeFormats)
        {
            DataTable rTbl = trgIncTbl;

            if (FetchMinValuesFromSrc)
            {
                var srcWhereXML = "";
                var trgWhereXML = "";

                if (srcIncTbl != null)
                {
                    if (srcIncTbl.Columns.Contains("XmlNodes"))
                    {
                        srcWhereXML = srcIncTbl.Rows[0]["XmlNodes"]?.ToString() ?? string.Empty;
                    }
                }

                if (trgIncTbl != null)
                {
                    if (trgIncTbl.Columns.Contains("XmlNodes"))
                    {
                        trgWhereXML = trgIncTbl.Rows[0]["XmlNodes"]?.ToString() ?? string.Empty;
                    }
                }

                bool isSrcLess = CompTrgWhereXML(srcWhereXML, trgWhereXML, DateTimeFormats);

                if (isSrcLess)
                {
                    rTbl = srcIncTbl;
                }
            }

            return rTbl;
        }

        internal static bool IsWhereXMLLess(string srcWhereXML, DataTable trgIncTbl, DataTable DateTimeFormats)
        {
            bool IsLess = false;
            var trgWhereXML = "";

            if (trgIncTbl != null)
            {
                if (trgIncTbl.Columns.Contains("XmlNodes"))
                {
                    trgWhereXML = trgIncTbl.Rows[0]["XmlNodes"]?.ToString() ?? string.Empty;
                    bool isSrcLess = CompTrgWhereXML(srcWhereXML, trgWhereXML, DateTimeFormats);

                    if (isSrcLess)
                    {
                        IsLess = isSrcLess;
                    }
                }
            }

            return IsLess;
        }



        internal static IncObject ParseIncObject(DataTable tbl, string IncCol, string DateCol, DataTable DateFormats)
        {
            IncObject rValue = new IncObject
            {
                IncColIsDate = false
            };
            string whereXML = "";
            if (tbl != null)
            {
                if (tbl.Columns.Contains("IncExp"))
                {
                    //Set Final WhereKeyExp
                    string IncExpCMD = tbl.Rows[0]["IncExp"]?.ToString() ?? string.Empty;
                    rValue.IncColCMD = IncExpCMD;
                }

                if (tbl.Columns.Contains("DateExp"))
                {
                    //Set Final DateExp
                    string DateExpCMD = tbl.Rows[0]["DateExp"]?.ToString() ?? string.Empty;
                    rValue.DateColCMD = DateExpCMD;
                }

                if (tbl.Columns.Contains("RunFullload"))
                {
                    //Set Fulload CheckForError
                    bool runFullExport = tbl.Rows[0]["RunFullload"].ToString() == "1" ? true : false;
                    rValue.RunFullload = runFullExport;
                }

                if (tbl.Columns.Contains("XmlNodes"))
                {
                    //Set Final DateExp
                    whereXML = tbl.Rows[0]["XmlNodes"]?.ToString() ?? string.Empty;
                    rValue.XML = whereXML;
                }


                if (whereXML.Length > 0)
                {
                    XDocument trgDoc = XDocument.Parse(whereXML);
                    var srcFileDate_DW = from node in trgDoc.Descendants("Filters")
                                         where node.Attribute("ColType").Value == "IncCol"
                                         || node.Attribute("ColName").Value == "FileDate_DW"
                                         select node.Attribute("Value").Value;

                    if (srcFileDate_DW != null)
                    {
                        string fValue = srcFileDate_DW.FirstOrDefault();
                        if (fValue != null)
                        {
                            if (fValue.Length > 0)
                            {
                                rValue.IncColValDT = ParseToDateTime(fValue, DateFormats);
                            }
                        }
                    }

                    var srcDateColVal = from node in trgDoc.Descendants("Filters")
                                        where node.Attribute("ColType").Value == "DateCol"
                                        select node.Attribute("Value").Value;

                    if (srcDateColVal != null)
                    {
                        string fValue = srcDateColVal.FirstOrDefault();
                        if (fValue != null)
                        {
                            if (fValue.Length > 0)
                            {
                                rValue.DateColVal = ParseToDateTime(fValue, DateFormats);
                            }
                        }
                    }


                    var SrcIncCol = from node in trgDoc.Descendants("Filters")
                                    where node.Attribute("ColType").Value == "IncCol"
                                    select node.Attribute("Value").Value;




                    if (SrcIncCol != null)
                    {
                        string fValue = SrcIncCol.FirstOrDefault();
                        if (fValue != null)
                        {
                            if (fValue.Length > 0)
                            {
                                rValue.IncColValDT = ParseToDateTime(fValue, DateFormats);
                                //DateTime srcIncDate = new DateTime(0001, 1, 1);
                                if (rValue.IncColValDT != FlowDates.Default)
                                {
                                    rValue.IncColIsDate = true;
                                }
                            }
                        }
                    }

                    //If  IncCol Is a numeric value
                    if (SrcIncCol != null)
                    {
                        string fValue = SrcIncCol.FirstOrDefault();
                        if (fValue != null)
                        {
                            if (fValue.Length > 0)
                            {
                                int srcIncCol = -1;
                                int.TryParse(fValue, out srcIncCol);

                                rValue.IncColVal = srcIncCol;
                            }
                        }
                    }

                }

            }

            return rValue;
        }

        internal static bool CompTrgWhereXML(string srcWhereXML, string trgWhereXML, DataTable DateTimeFormats)
        {
            bool srcIsLess = false;

            long srcFileDate = 0;
            long trgFileDate = 0;
            long srcIncCol = 0;
            long trgIncCol = 0;
            DateTime srcIncDate = new DateTime(1901, 1, 1);
            DateTime trgIncDate = new DateTime(1900, 1, 1);

            if (srcWhereXML.Length > 0)
            {
                XDocument trgDoc = XDocument.Parse(srcWhereXML);
                var srcFileDate_DW = from node in trgDoc.Descendants("Filters")
                                     where node.Attribute("ColType").Value == "IncCol"
                                     || node.Attribute("ColName").Value == "FileDate_DW"
                                     select node.Attribute("Value").Value;

                if (srcFileDate_DW != null)
                {
                    string fValue = srcFileDate_DW.FirstOrDefault();
                    if (fValue != null)
                    {
                        if (fValue.Length > 0)
                        {
                            srcFileDate = long.Parse(fValue);
                        }
                    }
                }

                var srcDateColVal = from node in trgDoc.Descendants("Filters")
                                    where node.Attribute("ColType").Value == "DateCol"
                                    select node.Attribute("Value").Value;

                if (srcDateColVal != null)
                {
                    string fValue = srcDateColVal.FirstOrDefault();
                    if (fValue != null)
                    {
                        if (fValue.Length > 0)
                        {
                            srcIncDate = ParseToDateTime(fValue, DateTimeFormats);
                        }
                    }
                }


                var SrcIncCol = from node in trgDoc.Descendants("Filters")
                                where node.Attribute("ColType").Value == "IncCol"
                                select node.Attribute("Value").Value;

                if (SrcIncCol != null)
                {
                    string fValue = SrcIncCol.FirstOrDefault();
                    if (fValue != null)
                    {
                        if (fValue.Length > 0)
                        {
                            long.TryParse(fValue, out srcIncCol);
                        }
                    }
                }

            }

            if (trgWhereXML.Length > 0)
            {
                XDocument trgDoc = XDocument.Parse(trgWhereXML);
                var TrgFileDate_DW = from node in trgDoc.Descendants("Filters")
                                     where node.Attribute("ColType").Value == "IncCol"
                                     || node.Attribute("ColName").Value == "FileDate_DW"
                                     select node.Attribute("Value").Value;

                if (TrgFileDate_DW != null)
                {
                    string fValue = TrgFileDate_DW.FirstOrDefault();
                    if (fValue != null)
                    {
                        if (fValue.Length > 0)
                        {
                            trgFileDate = long.Parse(fValue);
                        }
                    }
                }

                var TrgDateColVal = from node in trgDoc.Descendants("Filters")
                                    where node.Attribute("ColType").Value == "DateCol"
                                    select node.Attribute("Value").Value;

                if (TrgDateColVal != null)
                {
                    string fValue = TrgDateColVal.FirstOrDefault();
                    if (fValue != null)
                    {
                        if (fValue.Length > 0)
                        {
                            trgIncDate = ParseToDateTime(fValue, DateTimeFormats);
                        }
                    }
                }

                var TrgIncCol = from node in trgDoc.Descendants("Filters")
                                where node.Attribute("ColType").Value == "IncCol"
                                select node.Attribute("Value").Value;

                if (TrgIncCol != null)
                {
                    string fValue = TrgIncCol.FirstOrDefault();
                    if (fValue != null)
                    {
                        if (fValue.Length > 0)
                        {
                            long.TryParse(fValue, out trgIncCol);
                        }
                    }
                }
            }

            if (srcFileDate < trgFileDate || srcIncDate < trgIncDate || srcIncCol < trgIncCol)
            {
                srcIsLess = true;
            }


            return srcIsLess;
        }

        internal static DateTime ParseToDateTime(string strDateTime, DataTable dt)
        {
            DateTime dateValue = FlowDates.Default;

            foreach (DataRow dr in dt.Rows)
            {
                string dtFormat = dr["Format"]?.ToString() ?? string.Empty;
                if (DateTime.TryParseExact(strDateTime, dtFormat,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out dateValue))
                    break;

            }

            if (dateValue < FlowDates.Default)
            {
                dateValue = FlowDates.Default;
            }

            return dateValue;
        }

        internal static DateTime ExtractDateTimeFromString(string srcString, DataTable DateTimeFormats)
        {
            DateTime rValue = FlowDates.Default;

            DataRow[] sortedRows = DateTimeFormats.Select("", "[FormatLength] desc");

            foreach (DataRow dr in sortedRows)
            {
                string format = dr["format"]?.ToString() ?? string.Empty;
                string extractedDateTimeString = ExtractDateTimeString(format, srcString);

                if (extractedDateTimeString != string.Empty)
                {
                    rValue = ParseToDateTime(extractedDateTimeString, DateTimeFormats);

                    if (rValue > FlowDates.Default)
                    {
                        break;
                    }
                }
            }

            return rValue;
        }

        static string ExtractDateTimeString(string format, string fileName)
        {
            string rValue = string.Empty;
            string pattern = GenerateRegexPattern(format);

            Regex rx = new Regex(pattern);

            MatchCollection matches = rx.Matches(fileName);

            foreach (Match match in matches)
            {
                rValue = match.Value;
            }

            if (rValue.Length < 8 && rValue.Length > 0)
            {
                rValue = rValue + " " + DateTime.Now.ToString("hh:mm:ss");
            }

            return rValue;
        }

        internal static string RemoveInvalidRegExChars(string input)
        {
            // List of special regular expression characters
            string[] specialRegExChars = { "\\", ":", "^", "$", "*", "+", "?", "(", ")", "[", "]", "{", "}", "|", "/" }; // "."

            // Escape each special character
            string[] escapedSpecialChars = Array.ConvertAll(specialRegExChars, Regex.Escape);

            // Create a regex pattern to match any of the special characters
            string pattern = "(" + string.Join("|", escapedSpecialChars) + ")";

            // Replace special characters with an empty string
            string cleanedInput = Regex.Replace(input, pattern, " ");

            return cleanedInput;
        }

        private static string GenerateRegexPattern(string format)
        {
            string pattern = RemoveInvalidRegExChars(format);

            // Replace format tokens with regex patterns
            pattern = pattern.Replace("MM", @"(0?[1-9]|1[0-2])")
                             .Replace("M", @"(0?[1-9]|1[0-2])")
                             .Replace("dd", @"(3[01]|[12]\$|0?[1-9])")
                             .Replace("d", @"(3[01]|[12]\$|0?[1-9])")
                             .Replace("yyyy", @"(\${4})")
                             .Replace("HH", @"([01]\$|2[0-3])")
                             .Replace("h", @"(0?[1-9]|1[0-2])")
                             .Replace("mm", @"([0-5]\$)")
                             .Replace("ss", @"([0-5]\$)")
                             .Replace("fffffff", @"(\${7})")
                             .Replace("ffffff", @"(\${6})")
                             .Replace("fffff", @"(\${5})")
                             .Replace("ffff", @"(\${4})")
                             .Replace("fff", @"(\${3})")
                             .Replace("ff", @"(\${2})")
                             .Replace("f", @"(\${1})")
                             .Replace("Z", @"(Z)")
                             .Replace("tt", @"(AM|PM)")
                             .Replace("$", @"d");


            //pattern = "(?<!\\d)" + pattern + "(?!\\d)";
            //pattern = Regex.Escape(pattern);
            return pattern;
        }

        internal static IEnumerable<(DateTime, DateTime)> BatchByMonthDays(DateTime start, DateTime end, int monthChunkSize)
        {
            DateTime dateEnd = DateTime.Parse(end.ToString());

            for (int i = 0; start.AddMonths(i) < dateEnd; i += monthChunkSize)
            {
                end = start.AddMonths(i + monthChunkSize);
                start.AddMonths(i);

                yield return (start.AddMonths(i), end < dateEnd ? end.AddDays(-1) : dateEnd);
            }
        }

        internal static IEnumerable<(DateTime, DateTime)> BatchByMonth(DateTime startDate, DateTime endDate, int monthChunkSize)
        {
            DateTime currentStart = startDate;
            while (currentStart <= endDate)
            {
                DateTime currentEnd;
                if (monthChunkSize > 1)
                {
                    currentEnd = currentStart.AddMonths(monthChunkSize - 1);
                    currentEnd = new DateTime(currentEnd.Year, currentEnd.Month, DateTime.DaysInMonth(currentEnd.Year, currentEnd.Month));
                }
                else
                {
                    currentEnd = new DateTime(currentStart.Year, currentStart.Month, DateTime.DaysInMonth(currentStart.Year, currentStart.Month));
                }
                if (currentEnd > endDate)
                {
                    currentEnd = endDate;
                }

                yield return (currentStart, currentEnd);

                currentStart = currentEnd.AddDays(1);
            }
        }

        internal static int Years(DateTime start, DateTime end)
        {
            return end.Year - start.Year - 1 +
                (end.Month > start.Month ||
                end.Month == start.Month && end.Day >= start.Day ? 1 : 0);
        }

        internal static DateTime LastDayOfYear(DateTime date)
        {
            DateTime newdate = new DateTime(date.Year + 1, 1, 1);
            //Substract one year
            return newdate.AddDays(-1);
        }


        internal static string PatternToSubfolderPath(string pattern, string folderDivider, DateTime date)
        {
            if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(folderDivider))
            {
                return "";
            }

            string yearPattern = "(?<year>YYYY)";
            string monthPattern = "(?<month>MM)";
            string dayPattern = "(?<day>DD)";

            string subfolderPath = pattern;

            if (Regex.IsMatch(pattern, yearPattern))
            {
                subfolderPath = Regex.Replace(subfolderPath, yearPattern, date.ToString("yyyy"));
            }
            if (Regex.IsMatch(pattern, monthPattern))
            {
                subfolderPath = Regex.Replace(subfolderPath, monthPattern, folderDivider + date.ToString("MM"));
            }
            if (Regex.IsMatch(pattern, dayPattern))
            {
                subfolderPath = Regex.Replace(subfolderPath, dayPattern, folderDivider + date.ToString("dd"));
            }

            subfolderPath = EnsureEndingSlash(subfolderPath, folderDivider);

            return subfolderPath;
        }

        internal static IEnumerable<(DateTime, DateTime)> BatchByYear(DateTime start, DateTime end, int monthChunkSize)
        {
            DateTime dateEnd = DateTime.Parse(end.ToString());


            for (int i = 0; start.AddMonths(i) < dateEnd; i += monthChunkSize)
            {
                end = start.AddMonths(i + monthChunkSize);
                start.AddMonths(i);

                yield return (start.AddMonths(i), end < dateEnd ? end.AddDays(-1) : dateEnd);
            }
        }

        internal static IEnumerable<(DateTime, DateTime)> BatchByDay(DateTime startDate, DateTime endDate, int chunkSizeInDays)
        {
            if (chunkSizeInDays < 1)
            {
                throw new ArgumentException("Chunk size must be greater than 0.", nameof(chunkSizeInDays));
            }

            //if (endDate < startDate)
            //{
            //    throw new ArgumentException("End date must be greater than or equal to the start date.", nameof(endDate));
            //}

            DateTime currentStart = startDate;
            DateTime currentEnd = currentStart.AddDays(chunkSizeInDays - 1);

            while (currentStart <= endDate)
            {
                if (currentEnd > endDate)
                {
                    currentEnd = endDate;
                }

                yield return (currentStart, currentEnd);

                currentStart = currentEnd.AddDays(1);
                currentEnd = currentStart.AddDays(chunkSizeInDays - 1);
            }
        }

        //internal static IEnumerable<(DateTime, DateTime)> BatchByDay(DateTime fromDate, DateTime toDate, int batchSize)
        //{
        //    var numberOfChunks = (toDate.Subtract(fromDate).Days + 1) / batchSize;

        //    for (int i = 0; i <= numberOfChunks; i++)
        //    {
        //        if (i == numberOfChunks)
        //        {
        //            yield return (fromDate.AddDays(batchSize * i), toDate);
        //        }
        //        else
        //        {
        //            yield return (fromDate.AddDays(batchSize * i), fromDate.AddDays((i + 1) * batchSize - 1));
        //        }
        //    }
        //}

        internal static string CodeStackSection(string header, string tsql)
        {
            var tempStr = new StringBuilder();
            tempStr.AppendFormat(
                "{0}--################################### {1} ###################################{0}{2}{0}",
                Environment.NewLine, header, tsql);
            return tempStr.ToString();
        }

        internal static IEnumerable<int> DistributeInteger(int total, int divider)
        {
            if (divider == 0)
            {
                yield return 0;
            }
            else
            {
                int rest = total % divider;
                double result = total / (double)divider;

                for (int i = 0; i < divider; i++)
                {
                    if (rest-- > 0)
                        yield return (int)Math.Ceiling(result);
                    else
                        yield return (int)Math.Floor(result);
                }
            }
        }


        internal static string FindColumnName(string tfColList, string input)
        {
            var columnList = tfColList.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .ToList();

            // Normalize the input (remove brackets and make it lowercase)
            var normalizedInput = input.Replace("[", "").Replace("]", "");

            // Search the list
            foreach (var columnName in columnList)
            {
                var normalizedColumnName = columnName.Replace("[", "").Replace("]", "");

                if (normalizedColumnName.Equals(normalizedInput, StringComparison.InvariantCultureIgnoreCase))
                {
                    return columnName;
                }
            }

            return string.Empty; // or return string.Empty; based on your preference
        }


        internal static Hashtable ConvertDataTableToHashTable(DataTable dtIn, string keyField, string valueField)
        {
            var htOut = new Hashtable(StringComparer.InvariantCultureIgnoreCase);
            foreach (DataRow drIn in dtIn.Rows) htOut.Add(drIn[keyField].ToString(), drIn[valueField].ToString());
            return htOut;
        }

        private static List<DataTable> CreateBatchTable(DataTable originalTable, int batchSize)
        {
            var tables = new List<DataTable>();
            var i = 0;
            var j = 1;
            var newDt = originalTable.Clone();
            newDt.TableName = "Table_" + j;
            newDt.Clear();
            foreach (DataRow row in originalTable.Rows)
            {
                var newRow = newDt.NewRow();
                newRow.ItemArray = row.ItemArray;
                newDt.Rows.Add(newRow);
                i++;
                if (i == batchSize)
                {
                    tables.Add(newDt);
                    j++;
                    newDt = originalTable.Clone();
                    newDt.TableName = "Table_" + j;
                    newDt.Clear();
                    i = 0;
                }
            }

            if (newDt.Rows.Count > 0)
            {
                tables.Add(newDt);
                j++;
                newDt = originalTable.Clone();
                newDt.TableName = "Table_" + j;
                newDt.Clear();
            }

            return tables;
        }
    }
}

