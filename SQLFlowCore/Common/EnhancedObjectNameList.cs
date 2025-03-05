using System.Collections.Generic;

namespace SQLFlowCore.Common
{
    internal class EnhancedObjectNameList : List<ObjectName>
    {
        // Constructor to allow initialization with an existing list
        public EnhancedObjectNameList(IEnumerable<ObjectName> objectNames) : base(objectNames)
        {
        }

        public EnhancedObjectNameList() : base()
        {
        }

        // Method to get a list of quoted names
        public List<string> GetQuotedNamesList()
        {
            return ObjectNameProcessor.GetQuotedNamesList(this);
        }

        // Method to get a list of unquoted names
        public List<string> GetUnquotedNamesList()
        {
            return ObjectNameProcessor.GetUnquotedNamesList(this);
        }

        // Method to get a comma-separated string of quoted names
        public string GetQuotedNames()
        {
            return ObjectNameProcessor.GetQuotedNames(this);
        }

        // Method to get a comma-separated string of quoted names prefixed with "src."
        public string GetQuotedNamesWithSrc()
        {
            return ObjectNameProcessor.GetQuotedNamesWithSrc(this);
        }

        // Method to get a comma-separated string of quoted names prefixed with "trg."
        public string GetQuotedNamesWithTrg()
        {
            return ObjectNameProcessor.GetQuotedNamesWithTrg(this);
        }

        // Method to get a comma-separated string of unquoted names
        public string GetUnquotedNames()
        {
            return ObjectNameProcessor.GetUnquotedNames(this);
        }
    }
}
