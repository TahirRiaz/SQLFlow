using System.Xml;

namespace SQLFlowCore.Services.Xml
{
    /// <summary>
    /// Provides a wrapper around the XmlReader class, allowing for additional functionality and control.
    /// </summary>
    /// <remarks>
    /// This class is used to wrap an XmlReader instance, providing additional methods and properties to control and manipulate XML data.
    /// </remarks>
    internal class XmlNodeWrapper : XmlReader
    {
        private XmlReader _reader;
        private int _depth;
        private bool _stillReading = true;
        private bool _initialized;
        private bool _startedWithEmpty;

        /// <summary>
        /// Initializes a new instance of the <see cref="XmlNodeWrapper"/> class.
        /// </summary>
        /// <param name="wrapped">The <see cref="XmlReader"/> instance to be wrapped.</param>
        /// <remarks>
        /// This constructor initializes the wrapper with the provided XmlReader instance, 
        /// and sets the initial depth and empty element status based on the wrapped reader.
        /// </remarks>
        public XmlNodeWrapper(XmlReader wrapped)
        {
            _reader = wrapped;
            _depth = _reader.Depth;
            _startedWithEmpty = _reader.IsEmptyElement;
        }

        public override void Close()
        {
            _reader.Close();
        }

        public override string GetAttribute(int i)
        {
            return _reader.GetAttribute(i);
        }

        public override string GetAttribute(string name)
        {
            return _reader.GetAttribute(name);
        }

        public override string GetAttribute(string name, string namespaceUri)
        {
            return _reader.GetAttribute(name, namespaceUri);
        }

        public override bool IsStartElement()
        {
            return _reader.IsStartElement();
        }

        public override bool IsStartElement(string name)
        {
            return _reader.IsStartElement(name);
        }

        public override bool IsStartElement(string localname, string ns)
        {
            return _reader.IsStartElement(localname, ns);
        }

        public override string LookupNamespace(string prefix)
        {
            return _reader.LookupNamespace(prefix);
        }

        public override void MoveToAttribute(int i)
        {
            _reader.MoveToAttribute(i);
        }

        public override bool MoveToAttribute(string name)
        {
            return _reader.MoveToAttribute(name);
        }

        public override bool MoveToAttribute(string name, string ns)
        {
            return _reader.MoveToAttribute(name, ns);
        }

        public override XmlNodeType MoveToContent()
        {
            return _reader.MoveToContent();
        }

        public override bool MoveToElement()
        {
            return _reader.MoveToElement();
        }

        public override bool MoveToFirstAttribute()
        {
            return _reader.MoveToFirstAttribute();
        }

        public override bool MoveToNextAttribute()
        {
            return _reader.MoveToNextAttribute();
        }

        /// <summary>
        /// Reads the next node from the stream.
        /// </summary>
        /// <returns>
        /// true if the next node was read successfully; false if there are no more nodes to read.
        /// </returns>
        /// <remarks>
        /// This method is an override of the Read method in the XmlReader class. It provides additional functionality by maintaining the reading state of the XML document. 
        /// It ensures that the reading process continues as long as the current node depth is greater than the initial depth. 
        /// The method also handles the initialization of the reading process, taking into account whether the XML document started with an empty element.
        /// </remarks>
        public override bool Read()
        {
            if (_initialized)
            {
                if (!_stillReading)
                {
                    return false;
                }
                _reader.Read();
                _stillReading = _reader.Depth > _depth;
                return true;
            }
            _initialized = true;
            if (!_startedWithEmpty)
            {
                _reader.Read();
                return true;
            }
            _stillReading = false;
            return false;
        }

        public override bool ReadAttributeValue()
        {
            return _reader.ReadAttributeValue();
        }

        public override string ReadElementString()
        {
            return _reader.ReadElementString();
        }

        public override string ReadElementString(string name)
        {
            return _reader.ReadElementString(name);
        }

        public override string ReadElementString(string localname, string ns)
        {
            return _reader.ReadElementString(localname, ns);
        }

        public override void ReadEndElement()
        {
            _reader.ReadEndElement();
        }

        public override string ReadInnerXml()
        {
            return _reader.ReadInnerXml();
        }

        public override string ReadOuterXml()
        {
            return _reader.ReadOuterXml();
        }

        public override void ReadStartElement()
        {
            _reader.ReadStartElement();
        }

        public override void ReadStartElement(string name)
        {
            _reader.ReadStartElement(name);
        }

        public override void ReadStartElement(string localname, string ns)
        {
            _reader.ReadStartElement(localname, ns);
        }

        public override string ReadString()
        {
            return _reader.ReadString();
        }

        public override void ResolveEntity()
        {
            _reader.ResolveEntity();
        }

        public override void Skip()
        {
            _reader.Skip();
        }

        public override XmlNodeType NodeType => _reader.NodeType;

        public override string Name => _reader.Name;

        public override string LocalName => _reader.LocalName;

        public override string NamespaceURI => _reader.NamespaceURI;

        public override string Prefix => _reader.Prefix;

        public override bool HasValue => _reader.HasValue;

        public override string Value => _reader.Value;

        public override int Depth => _reader.Depth;

        public override string BaseURI => _reader.BaseURI;

        public override bool IsEmptyElement => _reader.IsEmptyElement;

        public override bool IsDefault => _reader.IsDefault;

        public override char QuoteChar => _reader.QuoteChar;

        public override XmlSpace XmlSpace => _reader.XmlSpace;

        public override string XmlLang => _reader.XmlLang;

        public override int AttributeCount => _reader.AttributeCount;

        public override string this[int i] => _reader[i];

        public override string this[string name] => _reader[name];

        public override string this[string name, string namespaceUri] => _reader[name, namespaceUri];

        public override bool EOF => _reader.EOF;

        public override ReadState ReadState => _reader.ReadState;

        public override XmlNameTable NameTable => _reader.NameTable;

        public override bool HasAttributes => _reader.HasAttributes;

        public override bool CanResolveEntity => _reader.CanResolveEntity;
    }
}

