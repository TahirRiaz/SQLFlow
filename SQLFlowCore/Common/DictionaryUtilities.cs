using System;
using System.Collections.Generic;

namespace SQLFlowCore.Common
{
    internal class DictionaryUtilities
    {
        internal static string GetKeyFromValue(Dictionary<string, string> dictionary, string value)
        {
            foreach (var entry in dictionary)
            {
                if (string.Equals(entry.Value, value, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Key;
                }
            }
            throw new KeyNotFoundException($"No key found for the value: {value}");
        }
    }
}
