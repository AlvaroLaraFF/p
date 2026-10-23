using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace AceEngine.Net
{
    /// <summary>Node of a minimal XML tree (local names, no namespaces): what is needed to read IGD descriptions and SOAP responses.</summary>
    internal sealed class XmlNodeLite
    {
        public string Name = "";
        public readonly StringBuilder TextBuilder = new StringBuilder();
        public readonly List<XmlNodeLite> Children = new List<XmlNodeLite>();
        public string Text { get { return TextBuilder.ToString().Trim(); } }

        public XmlNodeLite Child(string name)
        {
            foreach (var c in Children) if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        public string ChildText(string name)
        {
            var c = Child(name);
            return c == null ? null : c.Text;
        }

        /// <summary>First descendant (depth-first) with that name, or null.</summary>
        public XmlNodeLite Find(string name)
        {
            foreach (var c in Children)
            {
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
                var d = c.Find(name);
                if (d != null) return d;
            }
            return null;
        }
    }

    /// <summary>
    /// XML reader for untrusted input (the LAN): DTD prohibited (no entities or XXE), no external resolver, bounded size, depth and number of nodes,
    /// bounded text per node. Returns the root element or throws XmlException.
    /// </summary>
    internal static class SafeXml
    {
        public const int MaxDepth = 24, MaxNodes = 4000, MaxTextPerNode = 4096;

        public static XmlNodeLite Parse(byte[] data, int length)
        {
            if (data == null || length <= 0) throw new XmlException("empty document");
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = Math.Max(1024L, length * 2L),
                MaxCharactersFromEntities = 0,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true,
                CloseInput = true
            };
            // Some routers send spaces or newlines before the XML declaration (illegal in XML): they are skipped.
            int start = 0;
            while (start < length && start < 64 && (data[start] == (byte)' ' || data[start] == (byte)'\t' || data[start] == (byte)'\r' || data[start] == (byte)'\n')) start++;
            using (var reader = XmlReader.Create(new MemoryStream(data, start, length - start, false), settings))
            {
                XmlNodeLite root = null;
                var stack = new Stack<XmlNodeLite>();
                int nodes = 0;
                while (reader.Read())
                {
                    switch (reader.NodeType)
                    {
                        case XmlNodeType.Element:
                            if (reader.Depth > MaxDepth) throw new XmlException("XML too deep");
                            if (++nodes > MaxNodes) throw new XmlException("XML with too many elements");
                            var n = new XmlNodeLite { Name = reader.LocalName };
                            if (stack.Count == 0)
                            {
                                if (root != null) throw new XmlException("several root elements");
                                root = n;
                            }
                            else stack.Peek().Children.Add(n);
                            if (!reader.IsEmptyElement) stack.Push(n);
                            break;
                        case XmlNodeType.EndElement:
                            if (stack.Count > 0) stack.Pop();
                            break;
                        case XmlNodeType.Text:
                        case XmlNodeType.CDATA:
                        case XmlNodeType.SignificantWhitespace:
                            if (stack.Count > 0)
                            {
                                var tb = stack.Peek().TextBuilder;
                                if (tb.Length < MaxTextPerNode) tb.Append(reader.Value, 0, Math.Min(reader.Value.Length, MaxTextPerNode - tb.Length));
                            }
                            break;
                        case XmlNodeType.DocumentType:
                            throw new XmlException("DTD not allowed");
                    }
                }
                if (root == null) throw new XmlException("document without a root element");
                return root;
            }
        }
    }
}
