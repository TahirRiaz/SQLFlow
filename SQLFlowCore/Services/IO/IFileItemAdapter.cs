namespace SQLFlowCore.Services.IO
{
    /// <summary>
    /// Defines a contract for a file item adapter.
    /// </summary>
    /// <typeparam name="T">The type of the specific item to be adapted.</typeparam>
    internal interface IFileItemAdapter<T>
    {
        /// <summary>
        /// Converts a specific item to a generic file item.
        /// </summary>
        /// <param name="specificItem">The specific item to be converted.</param>
        /// <returns>A <see cref="GenericFileItem"/> that represents the converted specific item.</returns>
        GenericFileItem ConvertToGenericFileItem(T specificItem);
    }
}
