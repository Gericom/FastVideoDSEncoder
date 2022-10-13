using Gericom.FastVideoDS.Bitstream;

namespace Gericom.FastVideoDS
{
    public static class Vlc
    {
        public static readonly int[] BitLengthTable;

        static Vlc()
        {
            BitLengthTable = new int[2 * 64 * 128];
            ushort[] tabA = VlcTables.TableA;
            byte[]   tabB = VlcTables.TableB;
            for (int last = 0; last <= 1; last++)
            {
                for (int skip = 0; skip < 64; skip++)
                {
                    for (int value = -64; value < 64; value++)
                    {
                        int val = value;
                        if (val < 0)
                            val = -val;
                        if (val <= 31)
                        {
                            int idx = VlcTables.TableARefLinear[val * 64 * 2 + skip * 2 + last];
                            if (idx >= 0)
                            {
                                BitLengthTable[last * 128 * 64 + skip * 128 + (value + 64)] = tabA[idx] & 0xF;
                                continue;
                            }

                            int newskip = skip - tabB[(val | (last << 6)) + 0x80];
                            if (newskip >= 0)
                            {
                                idx = VlcTables.TableARefLinear[val * 64 * 2 + newskip * 2 + last];
                                if (idx >= 0)
                                {
                                    BitLengthTable[last * 128 * 64 + skip * 128 + (value + 64)] = 9 + (tabA[idx] & 0xF);
                                    continue;
                                }
                            }
                        }

                        int newval = val - tabB[skip | (last << 6)];
                        if (newval >= 0 && newval <= 31)
                        {
                            int idx = VlcTables.TableARefLinear[newval * 64 * 2 + skip * 2 + last];
                            if (idx >= 0)
                            {
                                BitLengthTable[last * 128 * 64 + skip * 128 + (value + 64)] = 8 + (tabA[idx] & 0xF);
                                continue;
                            }
                        }

                        BitLengthTable[last * 128 * 64 + skip * 128 + (value + 64)] = 28;
                    }
                }
            }
        }

        public static int CalcDctBitCount(int[] dct)
        {
            int lastNonZero = 0;
            for (int i = dct.Length - 1; i >= 0; i--)
            {
                if (dct[i] != 0)
                {
                    lastNonZero = i;
                    break;
                }
            }

            return CalcDctBitCount(dct, lastNonZero);
        }

        public static int CalcDctBitCount(int[] dct, int lastNonZero)
        {
            int bitCount = 0;

            int skip = 0;
            for (int i = 0; i < lastNonZero; i++)
            {
                if (dct[i] == 0)
                {
                    skip++;
                    continue;
                }

                int val = dct[i];
                if (val + 64 < 128)
                    bitCount += BitLengthTable[(skip * 128) + (val + 64)];
                else
                    bitCount += 28;
                skip = 0;
            }

            if (dct[lastNonZero] + 64 < 128)
                bitCount += BitLengthTable[128 * 64 + (skip * 128) + (dct[lastNonZero] + 64)];
            else
                bitCount += 28;

            return bitCount;
        }

        public static void EncodeDct(int[] dct, BitWriter b)
        {
            ushort[] tabA        = VlcTables.TableA;
            byte[]   tabB        = VlcTables.TableB;
            int      lastNonZero = 0;
            for (int i = 0; i < dct.Length; i++)
            {
                if (dct[i] != 0)
                    lastNonZero = i;
            }

            int skip = 0;
            for (int i = 0; i < dct.Length; i++)
            {
                if (dct[i] == 0 && lastNonZero != 0)
                {
                    skip++;
                    continue;
                }

                int val = dct[i];

                if (val < 0) val = -val;
                if (val <= 31)
                {
                    int idx = VlcTables.TableARefLinear[val * 64 * 2 + skip * 2 + ((i == lastNonZero) ? 1 : 0)];
                    if (idx >= 0)
                    {
                        int  nrbits = (tabA[idx] & 0xF);
                        uint tidx   = (uint)idx;
                        if (nrbits < 12)
                            tidx >>= (12 - nrbits);
                        else if (nrbits > 12)
                            tidx <<= (nrbits - 12);
                        if (dct[i] < 0) tidx |= 1;
                        b.WriteBits((uint)tidx, nrbits);
                        skip = 0;
                        goto end;
                    }

                    int newskip = skip - tabB[(val | (((i == lastNonZero) ? 1 : 0) << 6)) + 0x80];
                    if (newskip >= 0)
                    {
                        idx = VlcTables.TableARefLinear[
                            val * 64 * 2 + newskip * 2 +
                            ((i == lastNonZero) ? 1 : 0)];
                        if (idx >= 0)
                        {
                            b.WriteBits(3, 7);
                            b.WriteBits(1, 1);
                            b.WriteBits(0, 1);
                            int  nrbits = (tabA[idx] & 0xF);
                            uint tidx   = (uint)idx;
                            if (nrbits < 12)
                                tidx >>= (12 - nrbits);
                            else if (nrbits > 12)
                                tidx <<= (nrbits - 12);
                            if (dct[i] < 0) tidx |= 1;
                            b.WriteBits((uint)tidx, nrbits);
                            skip = 0;
                            goto end;
                        }
                    }
                }

                int newval = val - tabB[skip | (((i == lastNonZero) ? 1 : 0) << 6)];
                if (newval >= 0 && newval <= 31)
                {
                    int idx = VlcTables.TableARefLinear[newval * 64 * 2 + skip * 2 + ((i == lastNonZero) ? 1 : 0)];
                    if (idx >= 0)
                    {
                        b.WriteBits(3, 7);
                        b.WriteBits(0, 1);
                        int  nrbits = (tabA[idx] & 0xF);
                        uint tidx   = (uint)idx;
                        if (nrbits < 12)
                            tidx >>= (12 - nrbits);
                        else if (nrbits > 12)
                            tidx <<= (nrbits - 12);
                        if (dct[i] < 0) tidx |= 1;
                        b.WriteBits((uint)tidx, nrbits);
                        skip = 0;
                        goto end;
                    }
                }

                b.WriteBits(3, 7);
                b.WriteBits(1, 1);
                b.WriteBits(1, 1);
                if (i == lastNonZero)
                    b.WriteBits(1, 1);
                else
                    b.WriteBits(0, 1);
                b.WriteBits((uint)skip, 6);
                skip = 0;
                b.WriteBits((uint)dct[i], 12);
                end:
                if (i == lastNonZero)
                    break;
            }
        }
    }
}