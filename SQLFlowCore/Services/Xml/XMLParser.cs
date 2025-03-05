using System;
using System.Data;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.XPath;
using System.Xml.Xsl;

namespace SQLFlowCore.Services.Xml
{
    /// <summary>
    /// Provides methods and properties for parsing XML data.
    /// </summary>
    /// <remarks>
    /// The XmlParser class is a concrete implementation of the abstract ParseBase class. It provides functionality for parsing XML data using XPath expressions. It supports both in-memory and streaming methods of loading XML data. The class also provides methods for adding namespaces, evaluating XPath expressions, reading records, and reading to the end of the XML data. It also exposes properties for accessing user settings and columns.
    /// </remarks>
    internal class XmlParser : ParseBase
    {
        private XmlTextReader _xmlReader;
        private XPathNodeIterator _patternIterator;
        private ColumnCollection _columns;
        private bool _hasMoreData;
        private XPathNavigator _records;
        private XPathExpression _xPath;
        private XmlNamespaceManager _nsManager;
        internal bool Initialized;

        /// <summary>
        /// Initializes a new instance of the <see cref="XmlParser"/> class with the specified XPath expression.
        /// </summary>
        /// <param name="xPath">The XPath expression to navigate through the XML data.</param>
        /// <remarks>
        /// This constructor initializes a new instance of the <see cref="XmlParser"/> class, sets the <see cref="XmlNamespaceManager"/> to a new instance of <see cref="CustomContext"/>, sets the <see cref="UserSettings"/> to a new instance of <see cref="UserSettings"/>, compiles the XPath expression and assigns it to <see cref="_xPath"/>, and initializes the <see cref="ColumnCollection"/> with the <see cref="XmlNamespaceManager"/>.
        /// </remarks>

        private XmlParser(string xPath)
        {
            _hasMoreData = true;
            _nsManager = new CustomContext();
            Settings = new UserSettings();
            _xPath = new XmlDocument().CreateNavigator().Compile(xPath);
            _columns = new ColumnCollection(_nsManager);
        }

        public XmlParser(TextReader inputStream, string xPath) : this(inputStream, xPath, LoadMethod.Streaming)
        {
        }

        public XmlParser(string fileName, string xPath) : this(fileName, xPath, LoadMethod.Streaming)
        {
        }

        public XmlParser(Stream inputStream, Encoding encoding, string xPath) : this(inputStream, encoding, xPath, LoadMethod.Streaming)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="XmlParser"/> class with the specified TextReader, XPath expression, and LoadMethod.
        /// </summary>
        /// <param name="inputStream">The TextReader to read the XML data from.</param>
        /// <param name="xPath">The XPath expression to navigate through the XML data.</param>
        /// <param name="loadMethod">The method of loading XML data. Can be either InMemory or Streaming.</param>
        /// <remarks>
        /// If the LoadMethod is Streaming, an XmlTextReader is created with the provided TextReader, and DtdProcessing is set to Prohibit. 
        /// The XmlTextReader is then used to initialize an XmlParserNavigator.
        /// If the LoadMethod is InMemory, the XML data is loaded into an XPathDocument, which is then used to create an XPathNavigator.
        /// </remarks>
        public XmlParser(TextReader inputStream, string xPath, LoadMethod loadMethod) : this(xPath)
        {
            if (loadMethod == LoadMethod.Streaming)
            {
                XmlTextReader reader1 = new XmlTextReader(inputStream);
                reader1.DtdProcessing = DtdProcessing.Prohibit;
                _xmlReader = reader1;
                _records = new XmlParserNavigator(_xmlReader);
            }
            else
            {
                using (inputStream)
                {
                    XmlTextReader reader = new XmlTextReader(inputStream);
                    reader.DtdProcessing = DtdProcessing.Prohibit;
                    _records = new XPathDocument(reader).CreateNavigator();
                }
            }
        }

        public XmlParser(string fileName, string xPath, LoadMethod loadMethod) : this(new StreamReader(new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)), xPath, loadMethod)
        {
        }

        public XmlParser(Stream inputStream, Encoding encoding, string xPath, LoadMethod loadMethod) : this(new StreamReader(inputStream, encoding, false), xPath, loadMethod)
        {
        }

        public void AddNamespace(string prefix, string uri)
        {
            _nsManager.AddNamespace(prefix, uri);
        }

        /// <summary>
        /// Initializes the XML parser if it has not been initialized yet.
        /// </summary>
        /// <remarks>
        /// This method sets the context for the XPath expression, selects the nodes that match the XPath expression, and sets the Initialized property to true.
        /// It should be called before any operations that require the parser to be initialized.
        /// </remarks>
        private void CheckInit()
        {
            if (!Initialized)
            {
                _xPath.SetContext(_nsManager);
                _patternIterator = _records.Select(_xPath);
                Initialized = true;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!Disposed)
            {
                if (disposing)
                {
                    _patternIterator = null;
                    _records = null;
                    _columns = null;
                    _nsManager = null;
                    _xPath = null;
                }
                if (_xmlReader != null)
                {
                    _xmlReader.Close();
                }
                Disposed = true;
            }
        }

        public string Evaluate(string xPath)
        {
            XPathExpression expr = new XmlDocument().CreateNavigator().Compile(xPath);
            expr.SetContext(_nsManager);
            return Evaluate(expr);
        }

        private string Evaluate(XPathExpression expr)
        {
            object obj2 = _patternIterator.Current.Evaluate(expr);
            string str = "";
            if (expr.ReturnType != XPathResultType.NodeSet)
            {
                str = obj2.ToString();
            }
            else
            {
                XPathNodeIterator iterator = obj2 as XPathNodeIterator;
                if (!iterator.MoveNext())
                {
                    str = "";
                }
                else
                {
                    str = iterator.Current.Value;
                    if (Settings.trimWhitespace)
                    {
                        str = str.Trim();
                    }
                    if (iterator.MoveNext())
                    {
                        throw new NotSupportedException("Expression, " + expr.Expression + ", resulted in more than one result.");
                    }
                }
            }
            return str;
        }

        ~XmlParser()
        {
            Dispose(false);
        }

        public static XmlParser Parse(string data, string xPath)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data), "Data can not be null.");
            }
            return new XmlParser(new StringReader(data), xPath, LoadMethod.InMemory);
        }

        /// <summary>
        /// Reads the next record from the XML data source.
        /// </summary>
        /// <returns>
        /// Returns true if there are more records to read; otherwise, false.
        /// </returns>
        /// <remarks>
        /// This method advances the reader to the next record in the XML data source. 
        /// It also updates the values array with the values of the current record.
        /// </remarks>
        public override bool ReadRecord()
        {
            CheckDisposed();
            CheckInit();
            if (_hasMoreData)
            {
                _hasMoreData = _patternIterator.MoveNext();
                if (values.Length < _columns.Count)
                {
                    values = new string[_columns.Count];
                }
                int index = 0;
                while (true)
                {
                    if (index >= _columns.Count)
                    {
                        ColumnsCount = _columns.Count;
                        if (_hasMoreData)
                        {
                            currentRecord += 1L;
                        }
                        break;
                    }
                    values[index] = Evaluate(_columns[index].Expression);
                    index++;
                }
            }
            return _hasMoreData;
        }

        public DataTable ReadToEnd()
        {
            return ReadToEnd(0UL);
        }

        /// <summary>
        /// Reads all records from the XML data source and returns them as a DataTable.
        /// </summary>
        /// <param name="maxRecords">The maximum number of records to read. If this value is 0, all records will be read.</param>
        /// <returns>A DataTable containing the records read from the XML data source. Each column in the DataTable corresponds to a column in the XML data source.</returns>

        public DataTable ReadToEnd(ulong maxRecords)
        {
            DataTable table = new DataTable();
            table.BeginLoadData();
            for (int i = 0; i < _columns.Count; i++)
            {
                int num2 = i + 1;
                table.Columns.Add("Column" + num2);
            }
            foreach (string str in _columns.Names)
            {
                table.Columns[_columns.GetIndex(str)].ColumnName = str;
            }
            bool flag = maxRecords != 0L;
            DataRowCollection rows = table.Rows;
            while ((!flag || currentRecord < maxRecords) && ReadRecord())
            {
                object[] values = Values;
                rows.Add(values);
            }
            table.EndLoadData();
            return table;
        }

        public UserSettings Settings { get; }

        public string this[string columnName]
        {
            get
            {
                CheckDisposed();
                return base[_columns.GetIndex(columnName)];
            }
        }

        public ColumnCollection Columns
        {
            get
            {
                CheckDisposed();
                return _columns;
            }
        }

        public sealed class Column
        {
            internal XPathExpression Expression;

            public Column(string xPath)
            {
                Expression = new XmlDocument().CreateNavigator().Compile(xPath);
            }
        }

        /// <summary>
        /// Represents a collection of columns for the XML Parser. This class extends the NamedColCollection.
        /// </summary>
        /// <remarks>
        /// Each column in the collection is associated with an XPathExpression and a namespace manager.
        /// </remarks>
        public class ColumnCollection : NamedColCollection
        {
            internal XmlNamespaceManager NsManager;

            internal ColumnCollection(XmlNamespaceManager nsManager)
            {
                NsManager = nsManager;
            }

            public void Add(Column column)
            {
                Add(column, null);
            }

            public void Add(string xPath)
            {
                Add(new Column(xPath));
            }

            public void Add(Column column, string columnName)
            {
                base.Add(column, columnName);
                column.Expression.SetContext(NsManager);
            }

            public void Add(string xPath, string columnName)
            {
                Add(new Column(xPath), columnName);
            }


#pragma warning disable CS0108 // 'XmlParser.ColumnCollection.this[int]' hides inherited member 'NamedColCollection.this[int]'. Use the new keyword if hiding was intended.
            public Column this[int columnIndex]
#pragma warning restore CS0108 // 'XmlParser.ColumnCollection.this[int]' hides inherited member 'NamedColCollection.this[int]'. Use the new keyword if hiding was intended.

            {
                get
                {
                    return base[columnIndex] as Column;
                }
                set
                {
                    base[columnIndex] = value;
                    this[columnIndex].Expression.SetContext(NsManager);
                }
            }

#pragma warning disable CS0108 // 'XmlParser.ColumnCollection.this[string]' hides inherited member 'NamedColCollection.this[string]'. Use the new keyword if hiding was intended.
            public Column this[string columnName]
#pragma warning restore CS0108 // 'XmlParser.ColumnCollection.this[string]' hides inherited member 'NamedColCollection.this[string]'. Use the new keyword if hiding was intended.
            {
                get
                {
                    return base[columnName] as Column;
                }
                set
                {
                    base[columnName] = value;
                    this[columnName].Expression.SetContext(NsManager);
                }
            }
        }

        /// <summary>
        /// Represents a custom XSLT context for XML parsing.
        /// </summary>
        /// <remarks>
        /// This class extends the <see cref="XsltContext"/> and provides custom implementations for its methods.
        /// It is used internally in the <see cref="XmlParser"/> class.
        /// </remarks>
        private class CustomContext : XsltContext
        {
            public CustomContext() : base(new NameTable())
            {
            }

            public override int CompareDocument(string baseUri, string nextbaseUri)
            {
                return 0;
            }

            public override string LookupNamespace(string prefix)
            {
                string text1 = base.LookupNamespace(NameTable.Get(prefix));
                if (text1 == null)
                {
                    throw new XsltException("Undeclared namespace prefix: " + prefix, null);
                }
                return text1;
            }

            public override bool PreserveWhitespace(XPathNavigator node)
            {
                return true;
            }

            public override IXsltContextFunction ResolveFunction(string prefix, string name, XPathResultType[] argTypes)
            {
                if (prefix.Length == 0 && (name == "min" || name == "max" || name == "avg"))
                {
                    return new CustomFunction(name, 1, 1, null, XPathResultType.Number);
                }
                if (prefix.Length > 0)
                {
                    throw new XPathException(prefix + ":" + name + " is an unrecognized function.", null);
                }
                throw new XPathException(name + " is an unrecognized function.", null);
            }

            public override IXsltContextVariable ResolveVariable(string prefix, string name)
            {
                if (prefix.Length > 0)
                {
                    throw new XPathException(prefix + ":" + name + " is an unrecognized variable.", null);
                }
                throw new XPathException(name + " is an unrecognized variable.", null);
            }

            public override bool Whitespace => false;
        }

        /// <summary>
        /// Represents a custom function that can be used in an XSLT context.
        /// </summary>
        /// <remarks>
        /// This class implements the IXsltContextFunction interface and provides a mechanism to invoke custom functions such as "avg", "min", and "max" within an XSLT context.
        /// </remarks>
        private class CustomFunction : IXsltContextFunction
        {
            private string _functionName;

            public CustomFunction(string name, int minArgs, int maxArgs, XPathResultType[] argTypes, XPathResultType returnType)
            {
                _functionName = name;
                Minargs = minArgs;
                Maxargs = maxArgs;
                ArgTypes = argTypes;
                ReturnType = returnType;
            }

            public object Invoke(XsltContext xsltContext, object[] args, XPathNavigator docContext)
            {
                string functionName = _functionName;
                switch (functionName)
                {
                    case "avg":
                        {
                            int num = 0;
                            double num2 = 0.0;
                            XPathNodeIterator iterator = args[0] as XPathNodeIterator;
                            while (iterator.MoveNext())
                            {
                                num++;
                                num2 += double.Parse(iterator.Current.Value);
                            }
                            return num2 / num;
                        }
                    case "min":
                        {
                            XPathNodeIterator iterator2 = args[0] as XPathNodeIterator;
                            iterator2.MoveNext();
                            double num3 = double.Parse(iterator2.Current.Value);
                            while (iterator2.MoveNext())
                            {
                                double num4 = double.Parse(iterator2.Current.Value);
                                if (num4 < num3)
                                {
                                    num3 = num4;
                                }
                            }
                            return num3;
                        }
                }

                if (functionName != "max")
                {
                    return null;
                }
                XPathNodeIterator iterator3 = args[0] as XPathNodeIterator;
                iterator3.MoveNext();
                double num5 = double.Parse(iterator3.Current.Value);
                while (iterator3.MoveNext())
                {
                    double num6 = double.Parse(iterator3.Current.Value);
                    if (num6 > num5)
                    {
                        num5 = num6;
                    }
                }
                return num5;
            }

            public int Minargs { get; }

            public int Maxargs { get; }

            public XPathResultType[] ArgTypes { get; }

            public XPathResultType ReturnType { get; }
        }

        /// <summary>
        /// Represents user-specific settings for the XML parser.
        /// </summary>
        public class UserSettings
        {
            internal bool trimWhitespace = true;

            public UserSettings()
            {
            }

            /// <summary>
            /// Gets or sets a value indicating whether whitespace should be trimmed.
            /// </summary>
            /// <value>
            ///   <c>true</c> if whitespace should be trimmed; otherwise, <c>false</c>.
            /// </value>
            public bool TrimWhitespace
            {
                get => trimWhitespace;
                set => trimWhitespace = value;
            }
        }
    }
}

