using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using PoorMansTSqlFormatterRedux;

namespace SQLFlowUi.Controllers
{

    public class HtmlObjects
    {
        public static string MakeJavaScriptSafe(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            StringBuilder builder = new StringBuilder(input.Length);

            foreach (char c in input)
            {
                switch (c)
                {
                    case '\\':  // Escape backslash
                        builder.Append("\\\\");
                        break;
                    case '\'':  // Escape single quote
                        builder.Append("\\\'");
                        break;
                    case '\"':  // Escape double quote
                        builder.Append("\\\"");
                        break;
                    case '\n':  // Escape new line
                        builder.Append("\\n");
                        break;
                    case '\r':  // Escape carriage return
                        builder.Append("\\r");
                        break;
                    case '\t':  // Escape tab
                        builder.Append("\\t");
                        break;
                    case '\b':  // Escape backspace
                        builder.Append("\\b");
                        break;
                    case '\f':  // Escape form feed
                        builder.Append("\\f");
                        break;
                    case '<':  // Escape less than
                        builder.Append("\\u003C");
                        break;
                    case '>':  // Escape greater than
                        builder.Append("\\u003E");
                        break;
                    case '&':  // Escape ampersand
                        builder.Append("\\u0026");
                        break;
                    default:
                        builder.Append(c);
                        break;
                }
            }

            return builder.ToString();
        }

        public static string ConvertToHtmlTable(DataTable dt)
        {
            string tableStyle = "border:1px solid #34373b;width:100%;font-family: Arial, sans-serif; font-size: 13px;";
            string headerStyle = "background-color: #1b1d20; font-weight: bold;padding: 4px;";
            string rowStyle = "border-bottom:1px solid #34373b; padding: 4px;";
            string cellStyle = "padding: 4px; white-space: nowrap;";

            if (dt == null) 
            {
                throw new ArgumentNullException(nameof(dt), "DataTable should not be null.");
            }

            StringBuilder html = new StringBuilder();

            // Open the table
            html.AppendLine($"<table style=\"{tableStyle}\">");

            // Add header row
            html.AppendLine("<thead>");
            html.AppendLine("<tr>");
            foreach (DataColumn column in dt.Columns)
            {
                html.AppendLine($"<th style=\"{headerStyle}\">{column.ColumnName}</th>");
            }
            html.AppendLine("</tr>");
            html.AppendLine("</thead>");

            // Add data rows
            html.AppendLine("<tbody>");
            foreach (DataRow row in dt.Rows)
            {
                html.AppendLine($"<tr style=\"{rowStyle}\">");
                foreach (DataColumn column in dt.Columns)
                {
                    string cellData = row[column].ToString();

                    // If the text length exceeds a threshold, say 50 characters, display a "View" link instead
                    if (ShortenColumn(cellData))
                    {
                        //
                        cellData = $"<a  href=\"javascript:void(0);\" title=\'{MakeJavaScriptSafe(cellData)}\'  onclick=\"triggerDialogFromClient('Column: {column.ColumnName}','{MakeJavaScriptSafe(cellData)}')\">{cellData.Substring(0, 70)}..</a>";
                    }

                    html.AppendLine($"<td style=\"{cellStyle}\">{FormatSqlWithPreTags(cellData)}</td>");
                }
                html.AppendLine("</tr>");
            }
            html.AppendLine("</tbody>");

            // Close the table
            html.AppendLine("</table>");

            string Tbl = html.ToString();

            return Tbl;
        }


        private static bool ShortenColumn(string input)
        {
            bool rValue = false;

            if (input.Length > 200 )
            {
                rValue = true;

                // Regular expression to match any HTML tags
                var regex = new Regex("<.*?>", RegexOptions.Compiled);

                rValue = !regex.IsMatch(input);
            }

            return rValue;
        }

        public static string ConvertToHtmlTableCompact(DataTable dt)
        {
            string tableStyle = "border:1px solid #34373b;width:100%;font-family: Arial, sans-serif; font-size: 13px;";
            string headerStyle = "background-color: #1b1d20; font-weight: bold;padding: 4px;";
            string rowStyle = "border-bottom:1px solid #34373b; padding: 4px;";

            if (dt == null)
            {
                throw new ArgumentNullException(nameof(dt), "DataTable should not be null.");
            }

            StringBuilder html = new StringBuilder();

            // Open the table
            html.AppendLine($"<table style=\"{tableStyle}\">");

            // Iterate through each row in the DataTable
            foreach (DataRow row in dt.Rows)
            {
                // For each row, display each column name and value in two columns
                foreach (DataColumn column in dt.Columns)
                {
                    string cValue = row[column].ToString();

                    if (cValue.Length > 150)
                    {
                        html.AppendLine($"<tr >");
                        html.AppendLine($"<td colspan=\"2\"style=\"{headerStyle}\">{column.ColumnName}</td></tr>");

                        // Data
                        html.AppendLine($"<tr >");
                        html.AppendLine($"<td colspan=\"2\" style=\"{rowStyle}\">{FormatSqlWithPreTags(cValue)}</td>");
                        html.AppendLine("</tr>");
                    }
                    else
                    {
                        html.AppendLine($"<tr>");
                        html.AppendLine($"<td width=\"120\" style=\"{headerStyle}\">{column.ColumnName}:</td><td style=\"{rowStyle}\">{cValue}</td></tr>");
                    }
                    // Header

                }
            }

            // Close the table
            html.AppendLine("</table>");

            string Tbl = html.ToString();

            return Tbl;
        }

        public static string ConvertToHtmlTableCompactRows(DataTable dt)
        {
            string tableStyle = "border:1px solid #34373b;width:100%;font-family: Arial, sans-serif; font-size: 13px;";
            string headerStyle = "background-color: #1b1d20; font-weight: bold;padding: 4px;";
            string headerStyle2 = "background-color: #4b3b3b; font-weight: bold;padding: 4px;";
            string rowStyle = "border-bottom:1px solid #34373b; padding: 4px;";
            
            if (dt == null)
            {
                throw new ArgumentNullException(nameof(dt), "DataTable should not be null.");
            }

            StringBuilder html = new StringBuilder();

            int i = 0;
            // Iterate through each row in the DataTable
            foreach (DataRow row in dt.Rows)
            {
                html.AppendLine($"<table style=\"{tableStyle}\">");

                html.AppendLine($"<tr >");
                html.AppendLine($"<td colspan=\"2\"style=\"{headerStyle2}\">Row {i.ToString()}</td></tr>");

                // For each row, display each column name and value in two columns
                foreach (DataColumn column in dt.Columns)
                {
                    string cValue = row[column].ToString();

                    if (cValue.Length > 150)
                    {
                        html.AppendLine($"<tr >");
                        html.AppendLine($"<td colspan=\"2\"style=\"{headerStyle}\">{column.ColumnName}</td></tr>");

                        // Data
                        html.AppendLine($"<tr >");
                        html.AppendLine($"<td colspan=\"2\" style=\"{rowStyle}\">{FormatSqlWithPreTags(cValue)}</td>");
                        html.AppendLine("</tr>");
                    }
                    else
                    {
                        html.AppendLine($"<tr>");
                        html.AppendLine($"<td width=\"120\" style=\"{headerStyle}\">{column.ColumnName}:</td><td style=\"{rowStyle}\">{cValue}</td></tr>");
                    }
                    // Header

                }
                // Close the table
                html.AppendLine("</table><br><br>");
                i++;
            }

           

            string Tbl = html.ToString();

            return Tbl;
        }


        static string FormatSqlWithPreTags(string inputSql)
        {
            string rValue = inputSql;
            Regex SqlKeywordsRegex = new Regex(@"\b(select|create table|HASHBYTES|insert bulk|create view|create index)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

            if (string.IsNullOrWhiteSpace(inputSql))
                return rValue;

            bool containsPreTags = Regex.IsMatch(inputSql, @"<pre>.*</pre>", RegexOptions.Singleline);

            // Remove <pre> and </pre> tags if exist
            if (containsPreTags)
            {
                inputSql = Regex.Replace(inputSql, @"</?pre>", string.Empty, RegexOptions.IgnoreCase);

                // Format SQL if it contains specified keywords
                if (SqlKeywordsRegex.IsMatch(inputSql))
                {
                    // Create an instance of the SQL Formatter
                    SqlFormattingManager formattingManager = new SqlFormattingManager();

                    // Format the SQL string
                    rValue = formattingManager.Format(inputSql);
                    rValue = $"<pre style = \"max-width: 700; white-space: pre-wrap; overflow: auto;\">{rValue}</pre>";
                }
            }

            // Re-add <pre> and </pre> for web rendering
            return rValue;
        }

        public static string GetTblJavascript()
        {
            string rValue = @"<div id=""myModal"" class=""modal"">
    <div class=""modal-content"">
        <span class=""close"">&times;</span>
        <p id=""modalText"">Modal text will appear here</p>
    </div>
</div>
<script>// Open modal and set content
function openModalWithText(text) {
    const modal = document.getElementById(""myModal"");
    const modalText = document.getElementById(""modalText"");
    
    modalText.textContent = text;
    modal.style.display = ""block"";
}

// Get the modal and close button
var modal = document.getElementById(""myModal"");
var span = document.getElementsByClassName(""close"")[0];

// Close the modal when the close button is clicked
span.onclick = function() {
    modal.style.display = ""none"";
}

// Close the modal when the window outside of the modal content is clicked
window.onclick = function(event) {
    if (event.target == modal) {
        modal.style.display = ""none"";
    }
}
</script>";

            return rValue;
        }

    }
}
