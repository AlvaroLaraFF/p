using System;

namespace AceEngine.Bencode
{
    public sealed class BencodeException : Exception
    {
        public int Position { get; private set; }
        public BencodeException(string message, int position) : base(message + " (position " + position + ")")
        {
            Position = position;
        }
    }
}
