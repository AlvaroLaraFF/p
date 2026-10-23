using System;

namespace AceEngine.P2P
{
    /// <summary>MSB-first bit set (bit 0 = most significant bit of the first byte), like the BITFIELD message.</summary>
    public sealed class Bitfield
    {
        private readonly bool[] _bits;
        public int Length { get { return _bits.Length; } }
        public int Count { get; private set; }

        public Bitfield(int length)
        {
            if (length < 0) throw new ArgumentOutOfRangeException("length");
            _bits = new bool[length];
        }

        public bool this[int index]
        {
            get { return _bits[index]; }
        }

        /// <summary>Sets the bit. Returns true if it changed.</summary>
        public bool Set(int index, bool value = true)
        {
            if (index < 0 || index >= _bits.Length) return false;
            if (_bits[index] == value) return false;
            _bits[index] = value;
            Count += value ? 1 : -1;
            return true;
        }

        public bool IsComplete { get { return Count == _bits.Length && _bits.Length > 0; } }

        public void Clear()
        {
            Array.Clear(_bits, 0, _bits.Length);
            Count = 0;
        }

        public void SetAll()
        {
            for (int i = 0; i < _bits.Length; i++) _bits[i] = true;
            Count = _bits.Length;
        }

        public static int ByteLength(int bits) { return (bits + 7) / 8; }

        /// <summary>
        /// Parses a BITFIELD. Returns null if the length is not exact or the padding is not zero
        /// (Ace closes the connection in those cases).
        /// </summary>
        public static Bitfield FromBytes(byte[] data, int offset, int count, int bits, bool strictPadding = true)
        {
            if (count != ByteLength(bits)) return null;
            var bf = new Bitfield(bits);
            for (int i = 0; i < bits; i++)
            {
                if ((data[offset + i / 8] & (0x80 >> (i % 8))) != 0) bf.Set(i);
            }
            if (strictPadding && bits % 8 != 0)
            {
                int spare = 8 - bits % 8;
                int mask = (1 << spare) - 1;
                if ((data[offset + count - 1] & mask) != 0) return null;
            }
            return bf;
        }

        public byte[] ToBytes()
        {
            var r = new byte[ByteLength(_bits.Length)];
            for (int i = 0; i < _bits.Length; i++)
                if (_bits[i]) r[i / 8] |= (byte)(0x80 >> (i % 8));
            return r;
        }
    }
}
