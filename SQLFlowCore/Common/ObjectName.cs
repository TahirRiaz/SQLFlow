using System.Collections.Generic;
using System.Linq;

namespace SQLFlowCore.Common
{
    internal class ObjectName
    {
        public string QuotedName { get; set; } = "";
        public string UnquotedName { get; set; } = "";

        public ObjectName(string quotedName, string unquotedName)
        {
            QuotedName = quotedName;
            UnquotedName = unquotedName;
        }


    }


    internal class ObjectNameProcessor
    {
        // Method to get a list of quoted names
        internal static List<string> GetQuotedNamesList(List<ObjectName> objectNames)
        {
            if (objectNames == null || !objectNames.Any())
            {
                return new List<string>();
            }
            return objectNames.Select(o => o.QuotedName).ToList();
        }

        // Method to get a list of unquoted names
        internal static List<string> GetUnquotedNamesList(List<ObjectName> objectNames)
        {
            if (objectNames == null || !objectNames.Any())
            {
                return new List<string>();
            }
            return objectNames.Select(o => o.UnquotedName).ToList();
        }

        internal static string GetQuotedNames(List<ObjectName> objectNames)
        {
            if (objectNames == null || !objectNames.Any())
            {
                return string.Empty;
            }

            var quotedNames = string.Join(", ", objectNames.Select(o => o.QuotedName));
            var unquotedNames = string.Join(", ", objectNames.Select(o => o.UnquotedName));

            return quotedNames;
        }

        internal static string GetQuotedNamesWithSrc(List<ObjectName> objectNames)
        {
            if (objectNames == null || !objectNames.Any())
            {
                return string.Empty;
            }
            var quotedNames = string.Join(", ", objectNames.Select(o => $"src.{o.QuotedName}"));
            return quotedNames;
        }

        internal static string GetQuotedNamesWithTrg(List<ObjectName> objectNames)
        {
            if (objectNames == null || !objectNames.Any())
            {
                return string.Empty;
            }
            var quotedNames = string.Join(", ", objectNames.Select(o => $"trg.{o.QuotedName}"));
            return quotedNames;
        }

        internal static string GetUnquotedNames(List<ObjectName> objectNames)
        {
            if (objectNames == null || !objectNames.Any())
            {
                return string.Empty;
            }
            var unquotedNames = string.Join(", ", objectNames.Select(o => o.UnquotedName));

            return unquotedNames;
        }



    }
}
