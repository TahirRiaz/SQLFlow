using System;
using System.Text;

namespace SQLFlowCore.Engine.Utils.DataExt
{
    /// <summary>
    /// Extends the StringBuilder class with helper methods.
    /// </summary>
    public static class StringBuilderExtensions
    {
        /// <summary>
        /// Appends a string to the StringBuilder if the condition is true.
        /// </summary>
        /// <param name="this">The StringBuilder instance to extend.</param>
        /// <param name="condition">The condition that must be met in order to append the string.</param>
        /// <param name="str">The string to append to the StringBuilder.</param>
        /// <returns>The StringBuilder instance.</returns>
        public static StringBuilder AppendIf(
            this StringBuilder @this,
            bool condition,
            string str)
        {
            if (@this == null) throw new ArgumentNullException(nameof(@this));

            if (condition) @this.Append(str);

            return @this;
        }
    }
}
