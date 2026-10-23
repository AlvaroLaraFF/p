using System;

namespace AceEngine.Transport
{
    /// <summary>Error reading a transport file.</summary>
    public sealed class TransportException : Exception
    {
        public TransportException(string message) : base(message) { }
        public TransportException(string message, Exception inner) : base(message, inner) { }
    }
}
