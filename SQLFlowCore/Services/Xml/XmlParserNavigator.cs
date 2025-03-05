using System;
using System.Xml;
using System.Xml.XPath;

namespace SQLFlowCore.Services.Xml
{
    /// <summary>
    /// Represents a navigator over an XML document that is used for parsing XML data.
    /// This class cannot be inherited.
    /// </summary>
    /// <remarks>
    /// The XmlParserNavigator class extends the XPathNavigator abstract base class, 
    /// and provides a cursor model for navigating and editing XML data.
    /// </remarks>
    internal class XmlParserNavigator : XPathNavigator
    {
        private XmlReader _xmlReader;
        private int _lastDepth;
        private XPathNavigator _evaluator;

        /// <summary>
        /// Initializes a new instance of the <see cref="XmlParserNavigator"/> class.
        /// </summary>
        /// <param name="xmlReader">The <see cref="XmlReader"/> instance used for XML parsing.</param>

        public XmlParserNavigator(XmlReader xmlReader)
        {
            _xmlReader = xmlReader;
        }

        public override XPathNavigator Clone()
        {
            _lastDepth = _xmlReader.Depth;
            XmlParserNavigator navigator1 = new XmlParserNavigator(_xmlReader)
            {
                _lastDepth = _lastDepth
            };
            return navigator1;
        }

        public override object Evaluate(XPathExpression expr)
        {
            if (NodeType == XPathNodeType.Root)
            {
                return base.Evaluate(expr);
            }
            if (_evaluator == null)
            {
                _evaluator = new XPathDocument(new XmlNodeWrapper(_xmlReader), XmlSpace.None).CreateNavigator();
                if (_xmlReader.NodeType == XmlNodeType.Attribute)
                {
                    _xmlReader.MoveToElement();
                }
                _evaluator.MoveToFirstChild();
            }
            return _evaluator.Evaluate(expr);
        }

        public override string GetAttribute(string localName, string namespaceUri)
        {
            return _xmlReader.GetAttribute(localName, namespaceUri);
        }

        public override string GetNamespace(string localName)
        {
            return _xmlReader.GetAttribute(localName);
        }

        public override bool IsSamePosition(XPathNavigator other)
        {
            return true;
        }

        public override bool MoveTo(XPathNavigator other)
        {
            _evaluator = null;
            return true;
        }

        public override bool MoveToAttribute(string localName, string namespaceUri)
        {
            return _xmlReader.MoveToAttribute(localName, namespaceUri);
        }

        public override bool MoveToFirst()
        {
            throw new NotSupportedException("XPath expressions referencing elements by index are not supported in a forward only stream.");
        }

        public override bool MoveToFirstAttribute()
        {
            return _xmlReader.MoveToFirstAttribute();
        }

        /// <summary>
        /// Moves the navigator to the first child of the current node.
        /// </summary>
        /// <returns>
        /// Returns true if the navigator is successfully moved to the first child of the current node; false if the current node has no children or is an empty element.
        /// </returns>
        /// <remarks>
        /// If the current node is an empty element or the reader is in the initial state, the method will return false or move the reader to the content, respectively. 
        /// If the current node is not an element, the method will return false. 
        /// After reading the node, if the node type is not an end element, the method will return true.
        /// </remarks>
        public override bool MoveToFirstChild()
        {
            _evaluator = null;
            if (_xmlReader.IsEmptyElement)
            {
                return false;
            }
            if (_xmlReader.ReadState == ReadState.Initial)
            {
                _xmlReader.MoveToContent();
                return true;
            }
            if (_xmlReader.NodeType != XmlNodeType.Element)
            {
                return false;
            }
            _xmlReader.Read();
            _lastDepth = _xmlReader.Depth;
            return _xmlReader.NodeType != XmlNodeType.EndElement;
        }

        public override bool MoveToFirstNamespace(XPathNamespaceScope namespaceScope)
        {
            return false;
        }

        public override bool MoveToId(string id)
        {
            return false;
        }

        public override bool MoveToNamespace(string localName)
        {
            return MoveToAttribute(localName, "http://www.w3.org/2000/xmlns/");
        }

        public override bool MoveToNext()
        {
            _evaluator = null;
            if (_xmlReader.EOF || _xmlReader.NodeType == XmlNodeType.Attribute)
            {
                return false;
            }
            while (_xmlReader.Depth > _lastDepth)
            {
                _xmlReader.Read();
            }
            _xmlReader.Skip();
            return _xmlReader.NodeType != XmlNodeType.EndElement && !_xmlReader.EOF;
        }

        public override bool MoveToNextAttribute()
        {
            return _xmlReader.MoveToNextAttribute();
        }

        public override bool MoveToNextNamespace(XPathNamespaceScope namespaceScope)
        {
            return false;
        }

        public override bool MoveToParent()
        {
            _evaluator = null;
            if (_xmlReader.NodeType != XmlNodeType.Attribute)
            {
                return false;
            }
            _xmlReader.MoveToElement();
            return true;
        }

        public override bool MoveToPrevious()
        {
            throw new NotSupportedException("XPath expressions referencing parent are not supported in a forward only stream.");
        }

        public override void MoveToRoot()
        {
            _evaluator = null;
        }

        /// <summary>
        /// Gets the type of the current node.
        /// </summary>
        /// <value>
        /// The <see cref="XPathNodeType"/> of the current node.
        /// </value>
        /// <remarks>
        /// If the reader is in the initial state, the node type is Root. 
        /// The node type is determined based on the underlying XmlReader's node type.
        /// </remarks>
        public override XPathNodeType NodeType
        {
            get
            {
                if (_xmlReader.ReadState == ReadState.Initial)
                {
                    return XPathNodeType.Root;
                }
                switch (_xmlReader.NodeType)
                {
                    case XmlNodeType.Element:
                        return XPathNodeType.Element;

                    case XmlNodeType.Attribute:
                        return XPathNodeType.Attribute;

                    case XmlNodeType.Text:
                        return XPathNodeType.Text;

                    case XmlNodeType.CDATA:
                        return XPathNodeType.Text;

                    case XmlNodeType.ProcessingInstruction:
                        return XPathNodeType.ProcessingInstruction;

                    case XmlNodeType.Comment:
                        return XPathNodeType.Comment;

                    case XmlNodeType.Whitespace:
                        return XPathNodeType.Whitespace;

                    case XmlNodeType.SignificantWhitespace:
                        return XPathNodeType.SignificantWhitespace;

                    case XmlNodeType.EndElement:
                        return XPathNodeType.Element;
                }
                return XPathNodeType.Text;
            }
        }

        public override string LocalName => _xmlReader.LocalName;

        public override string Name => _xmlReader.Name;

        public override string NamespaceURI => _xmlReader.NamespaceURI;

        public override string Prefix => _xmlReader.Prefix;

        public override string Value => _xmlReader.Value;

        public override string BaseURI => _xmlReader.BaseURI;

        public override string XmlLang => _xmlReader.XmlLang;

        public override bool IsEmptyElement => _xmlReader.IsEmptyElement;

        public override XmlNameTable NameTable => _xmlReader.NameTable;

        public override bool HasAttributes => _xmlReader.NodeType == XmlNodeType.Element && _xmlReader.HasAttributes;

        public override bool HasChildren => (_xmlReader.ReadState == ReadState.Initial || _xmlReader.NodeType == XmlNodeType.Element) && !_xmlReader.IsEmptyElement;
    }
}

