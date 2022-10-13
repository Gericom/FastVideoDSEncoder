using System;

namespace Gericom.FastVideoDSEncoder
{
    public static class Adpcm
    {
        private static readonly int[] IndexTable =
        {
            -3, -3, -2, -1, 2, 4, 6, 8,
            -3, -3, -2, -1, 2, 4, 6, 8
        };

        private static readonly short[] StepTable =
        {
            7, 8, 9, 10, 11, 12, 13, 14,
            16, 17, 19, 21, 23, 25, 28,
            31, 34, 37, 41, 45, 50, 55,
            60, 66, 73, 80, 88, 97, 107,
            118, 130, 143, 157, 173, 190, 209,
            230, 253, 279, 307, 337, 371, 408,
            449, 494, 544, 598, 658, 724, 796,
            876, 963, 1060, 1166, 1282, 1411, 1552,
            1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026,
            4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630,
            9493, 10442, 11487, 12635, 13899, 15289, 16818,
            18500, 20350, 22385, 24623, 27086, 29794, 32767
        };

        public record AdpcmState(short LastSample, int LastIdx);

        private static int GetBestTableIndex(int diff)
        {
            int lowestDiff = int.MaxValue;
            int lowestIdx  = -1;
            for (int i = 0; i < StepTable.Length; i++)
            {
                int diff2 = Math.Abs(Math.Abs(diff) - StepTable[i]);
                if (diff2 < lowestDiff)
                {
                    lowestDiff = diff2;
                    lowestIdx  = i;
                }
            }

            return lowestIdx;
        }

        public static byte[] Encode(ReadOnlySpan<short> samples)
            => Encode(samples, null, true).data;

        public static (byte[] data, AdpcmState lastState) Encode(ReadOnlySpan<short> samples, AdpcmState state,
            bool emitHeader)
        {
            if ((samples.Length & 7) != 0)
                throw new Exception("Samples should be a multiple of 8");
            var   result     = new byte[4 + samples.Length / 2];
            var   resultSpan = result.AsSpan();
            int   idx;
            short last;
            if (state == null)
            {
                if (!emitHeader)
                    throw new ArgumentException(nameof(emitHeader));
                idx  = Math.Min(GetBestTableIndex(samples[1] - samples[0]) + 3, 88);
                last = (short)Math.Max(-0x7FFF, samples[0] - StepTable[idx] / 8);
            }
            else
            {
                idx  = state.LastIdx;
                last = state.LastSample;
            }

            int offset = 0;
            if (emitHeader)
            {
                resultSpan.WriteLe<short>(0, last);
                resultSpan.WriteLe<ushort>(2, (ushort)idx);
                offset += 4;
            }

            int error = 0;
            for (int i = 0; i < samples.Length; i += 8)
            {
                uint nibbles = 0;
                for (int j = 0; j < 8; j++)
                {
                    int step   = StepTable[idx];
                    int sample = samples[i + j];

                    int best      = -1;
                    int bestScore = int.MaxValue;
                    for (int k = 0; k < 16; k++)
                    {
                        int diff2   = (step * ((k & 7) * 2 + 1)) >> 3;
                        int sample3 = last + diff2 * (((k >> 3) & 1) == 1 ? -1 : 1);
                        if (sample3 < -0x7FFF)
                            sample3 = -0x7FFF;
                        if (sample3 > 0x7FFF)
                            sample3 = 0x7FFF;
                        int score = Math.Abs(sample3 - sample);
                        if (i + j + 1 < samples.Length)
                        {
                            int step2      = StepTable[Math.Clamp(idx + IndexTable[k], 0, 88)];
                            int bestScore2 = int.MaxValue;
                            for (int l = 0; l < 16; l++)
                            {
                                int diff3   = (step2 * ((l & 7) * 2 + 1)) >> 3;
                                int sample4 = sample3 + diff3 * (((l >> 3) & 1) == 1 ? -1 : 1);
                                if (sample4 < -0x7FFF)
                                    sample4 = -0x7FFF;
                                if (sample4 > 0x7FFF)
                                    sample4 = 0x7FFF;
                                int score2 = Math.Abs(sample4 - samples[i + j + 1]);
                                if (score2 < bestScore2)
                                    bestScore2 = score2;
                            }

                            score += bestScore2;
                        }

                        if (score < bestScore)
                        {
                            bestScore = score;
                            best      = k;
                        }
                    }

                    nibbles |= (uint)best << (j * 4);

                    int diff    = (step * ((best & 7) * 2 + 1)) >> 3;
                    int sample2 = last + diff * (((best >> 3) & 1) == 1 ? -1 : 1);
                    if (sample2 < -0x7FFF)
                        sample2 = -0x7FFF;
                    if (sample2 > 0x7FFF)
                        sample2 = 0x7FFF;
                    last =  (short)sample2;
                    idx  += IndexTable[best];
                    if (idx < 0)
                        idx = 0;
                    if (idx > 88)
                        idx = 88;
                }

                resultSpan.WriteLe<uint>(offset, nibbles);
                offset += 4;
            }

            return (result, new(last, idx));
        }

        public static short[] Decode(ReadOnlySpan<byte> samples)
        {
            if ((samples.Length & 3) != 0)
                throw new Exception("Samples should be a multiple of 4 bytes");
            var   outSamples = new short[(samples.Length - 4) * 2];
            short last       = samples.ReadLe<short>(0);
            int   idx        = samples.ReadLe<ushort>(2);
            int   offset     = 4;
            for (int i = 0; i < outSamples.Length; i += 8)
            {
                uint nibbles = samples.ReadLe<uint>(offset);
                offset += 4;
                for (int j = 0; j < 8; j++)
                {
                    uint nibble = nibbles & 0xF;
                    nibbles >>= 4;
                    int diff = (int)((StepTable[idx] * ((nibble & 7) * 2 + 1)) >> 3);

                    int sample = last + diff * (((nibble >> 3) & 1) == 1 ? -1 : 1);

                    if (sample < -0x7FFF)
                        sample = -0x7FFF;
                    if (sample > 0x7FFF)
                        sample = 0x7FFF;
                    outSamples[i + j] =  (short)sample;
                    last              =  (short)sample;
                    idx               += IndexTable[nibble];
                    if (idx < 0)
                        idx = 0;
                    if (idx > 88)
                        idx = 88;
                }
            }

            return outSamples;
        }
    }
}