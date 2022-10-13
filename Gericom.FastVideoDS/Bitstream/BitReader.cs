using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Gericom.FastVideoDS.Bitstream
{
    public class BitReader
    {
        private readonly byte[] _data;
        private int _offset;
        public int BitsRemaining;
        public uint Bits;

        public BitReader(byte[] data)
        {
            _data = data;
            Bits = (uint)(BinaryPrimitives.ReadUInt16LittleEndian(data) << 16);
            BitsRemaining = 0;
            _offset = 2;
        }

        public uint ReadUnsignedVarInt()
        {
            int clz = BitOperations.LeadingZeroCount(Bits); //nr zeros
            Bits <<= clz;                                   //remove the zeros
            Bits += Bits;                                  //remove the stop bit
            int r9 = 32 - clz;                             //shift amount
            uint r6 = r9 == 32 ? 0 : Bits >> r9;
            r9 = 1;
            r6 += (uint)(r9 << clz);
            r6--;
            Bits <<= clz;
            BitsRemaining -= clz << 1;
            if (--BitsRemaining < 0)
                FillBits();
            return r6;
        }

        public void FillBits()
        {
            if (_offset >= _data.Length)
                return;
            uint r10 = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(_offset));
            _offset += 2;
            BitsRemaining += 0x10;
            int r9 = 0x10 - BitsRemaining;
            Bits |= r10 << r9;
        }

        private int ReadSignedVarInt()
        {
            int clz = BitOperations.LeadingZeroCount(Bits);
            Bits <<= clz;
            Bits += Bits;
            int r9 = 32 - clz;
            int r6 = r9 == 32 ? 0 : (int)(Bits >> r9);
            r9 = 1;
            r6 += r9 << clz;
            if ((r6 & 1) != 0)
                r6 = 1 - r6;
            r6 >>= 1;
            Bits <<= clz;
            BitsRemaining -= clz << 1;
            if (--BitsRemaining < 0)
                FillBits();
            return r6;
        }
    }
}