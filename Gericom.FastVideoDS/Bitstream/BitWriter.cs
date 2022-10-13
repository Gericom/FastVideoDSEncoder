using System.IO;
using System.Numerics;

namespace Gericom.FastVideoDS.Bitstream
{
    public class BitWriter
    {
        private readonly MemoryStream _stream = new();

        private uint _bits = 0;
        private int _bitCount = 0;

        public void WriteBits(uint value, int nrBits)
        {
            if (nrBits <= 0)
                return;
            _bits |= (value & (1u << nrBits) - 1) << 32 - nrBits - _bitCount;
            _bitCount += nrBits;
            if (_bitCount >= 16)
                Flush();
        }

        //Elias gamma coding
        public void WriteUnsignedVarInt(uint value)
        {
            int NrBits = 32 - BitOperations.LeadingZeroCount((value + 1) / 2);
            WriteBits(0, NrBits);
            WriteBits(1, 1); //stop bit
            value -= (1u << NrBits) - 1;
            WriteBits(value, NrBits);
        }

        public void WriteSignedVarInt(int value)
        {
            uint val;
            if (value <= 0)
                val = (uint)(1 - value * 2);
            else
                val = (uint)(value * 2);
            int nrBits = 32 - BitOperations.LeadingZeroCount(val / 2);
            WriteBits(0, nrBits);
            WriteBits(1, 1); //stop bit
            val -= 1u << nrBits;
            WriteBits(val, nrBits);
        }

        public void Flush()
        {
            if (_bitCount <= 0)
                return;
            _stream.WriteByte((byte)(_bits >> 16 & 0xFF));
            _stream.WriteByte((byte)(_bits >> 24 & 0xFF));
            _bitCount -= 16;
            _bits <<= 16;
        }

        // public byte[] PeekStream()
        // {
        //     if (BitCount <= 0) 
        //         return _stream.ToArray();
        //     var res = new List<byte>();
        //     res.AddRange(_stream.GetBuffer());
        //     res.Add((byte)((Bits >> 16) & 0xFF));
        //     res.Add((byte)((Bits >> 24) & 0xFF));
        //     return res.ToArray();
        // }

        public byte[] ToArray()
        {
            Flush();
            return _stream.ToArray();
        }

        public static int GetUnsignedVarIntBitCount(uint value)
        {
            int result = 0;
            int nrBits = 32 - BitOperations.LeadingZeroCount((value + 1) / 2);
            result += nrBits;
            result++; //stop bit
            result += nrBits;
            return result;
        }

        public static int GetSignedVarIntBitCount(int value)
        {
            int result = 0;
            uint val;
            if (value <= 0)
                val = (uint)(1 - value * 2);
            else
                val = (uint)(value * 2);
            int nrBits = 32 - BitOperations.LeadingZeroCount(val / 2);
            result += nrBits;
            result++; //stop bit
            result += nrBits;
            return result;
        }
    }
}