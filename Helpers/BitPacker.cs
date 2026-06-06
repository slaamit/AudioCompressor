using System.Collections.Generic;

namespace AudioCompressor.Helpers
{
    /// <summary>
    /// Writes arbitrary-width integer values tightly packed into a byte array, LSB-first.
    /// Example: two 4-bit values (0b1010, 0b0101) → one byte 0b01011010.
    /// </summary>
    internal sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private int _curByte;
        private int _bitPos;

        public void WriteBits(int value, int bits)
        {
            for (int i = 0; i < bits; i++)
            {
                if (((value >> i) & 1) == 1)
                    _curByte |= 1 << _bitPos;

                if (++_bitPos == 8)
                {
                    _bytes.Add((byte)_curByte);
                    _curByte = 0;
                    _bitPos  = 0;
                }
            }
        }

        /// <summary>Flushes any partial byte and returns the packed buffer.</summary>
        public byte[] ToArray()
        {
            if (_bitPos > 0)
                _bytes.Add((byte)_curByte);   // flush partial byte (high bits are 0)
            return _bytes.ToArray();
        }
    }

    /// <summary>
    /// Reads arbitrary-width integer values from a tightly packed byte array, LSB-first.
    /// Must be used with a buffer produced by <see cref="BitWriter"/>.
    /// </summary>
    internal sealed class BitReader
    {
        private readonly byte[] _data;
        private int _bytePos;
        private int _bitPos;

        public BitReader(byte[] data) => _data = data;

        public int ReadBits(int bits)
        {
            int value = 0;
            for (int i = 0; i < bits; i++)
            {
                if (_bytePos < _data.Length && ((_data[_bytePos] >> _bitPos) & 1) == 1)
                    value |= 1 << i;

                if (++_bitPos == 8) { _bytePos++; _bitPos = 0; }
            }
            return value;
        }
    }
}
