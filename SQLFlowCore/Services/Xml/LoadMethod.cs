namespace SQLFlowCore.Services.Xml
{
    /// <summary>
    /// Specifies the method of loading XML data in the XmlParser.
    /// </summary>
    /// <remarks>
    /// This enum is used to determine how XML data is loaded into the XmlParser.
    /// </remarks>
    /// <value>
    /// A LoadMethod value that indicates whether the XML data is loaded into memory in its entirety (InMemory), 
    /// or if it is read and processed as a stream (Streaming).
    /// </value>
    internal enum LoadMethod
    {
        /// <summary>
        /// Indicates that the XML data is loaded into memory in its entirety.
        /// </summary>
        InMemory,
        /// <summary>
        /// Indicates that the XML data is read and processed as a stream.
        /// </summary>
        Streaming
    }
}

