using System;
using System.Runtime.CompilerServices;
using Gericom.FastVideoDS.Bitstream;
using Gericom.FastVideoDS.Frames;
using Gericom.FastVideoDS.Utils;

namespace Gericom.FastVideoDS
{
    public static class MotionEstimation
    {
        private static readonly MotionVector[] LdspDirections =
        {
            new(-2, 0),
            new(-1, -1),
            new(0, -2),
            new(1, -1),
            new(2, 0),
            new(1, 1),
            new(0, 2),
            new(-1, 1)
        };

        private static readonly MotionVector[] SdspDirections =
        {
            new(-1, 0),
            new(0, -1),
            new(1, 0),
            new(0, 1),
        };

        public static MotionVector FindMotionVector(byte[] targetR, byte[] targetG, byte[] targetB, Rgb555Frame src,
            MotionVector center, MotionVector cheap, out int bestScore)
        {
            const int lambda = 4;
            var block = new byte[64];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            int getDistortion(MotionVector vec)
            {
                if (center.Y < (src.Height >> 1) * 2 && vec.Y > (src.Height - 8) * 2 ||
                    center.Y >= (src.Height >> 1) * 2 && vec.Y < 0)
                    return 999999;

                FrameUtil.GetTileHalf8(src.R, src.Width, src.Height, vec.X, vec.Y, block);
                int score = FrameUtil.Sad64(targetR, block);
                FrameUtil.GetTileHalf8(src.G, src.Width, src.Height, vec.X, vec.Y, block);
                score += FrameUtil.Sad64(targetG, block);
                FrameUtil.GetTileHalf8(src.B, src.Width, src.Height, vec.X, vec.Y, block);
                score += FrameUtil.Sad64(targetB, block);

                return score;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            int getBitCount(MotionVector vec)
            {
                return vec == cheap ? 1 :
                    1 + BitWriter.GetSignedVarIntBitCount(vec.X - cheap.X)
                      + BitWriter.GetSignedVarIntBitCount(vec.Y - cheap.Y);
            }

            // var vectors = new HashSet<MotionVector>();
            bestScore = getDistortion(center);
            int bestBitCount = getBitCount(center);
            int bestRdScore = bestBitCount * lambda + bestScore;
            var bestRdVec = center;
            var bestVec = center;

            int score = getDistortion(cheap);
            int bitCount = getBitCount(cheap);
            int rdScore = bitCount * lambda + score;
            if (score < bestScore || score == bestScore && bitCount < bestBitCount)
            {
                bestScore = score;
                bestBitCount = bitCount;
                bestVec = cheap;
            }

            if (rdScore < bestRdScore || Math.Abs(rdScore - bestRdScore) < 0.001f && score < bestScore)
            {
                bestRdScore = rdScore;
                bestRdVec = cheap;
            }

            var searchCenter = cheap;

            int count = 0;
            int bestIdx;
            do
            {
                bestIdx = -1;
                for (int i = 0; i < LdspDirections.Length; i++)
                {
                    var vec = searchCenter + LdspDirections[i];
                    // if(vectors.Contains(vec))
                    //     continue;
                    // vectors.Add(vec);
                    score = getDistortion(vec);
                    bitCount = getBitCount(vec);
                    rdScore = bitCount * lambda + score;
                    if (score < bestScore || score == bestScore && bitCount < bestBitCount)
                    {
                        bestScore = score;
                        bestBitCount = bitCount;
                        bestVec = vec;
                        bestIdx = i;
                    }

                    if (rdScore < bestRdScore || Math.Abs(rdScore - bestRdScore) < 0.001f && score < bestScore)
                    {
                        bestRdScore = rdScore;
                        bestRdVec = vec;
                    }

                    count++;
                }

                searchCenter = bestVec;
            } while (bestIdx != -1 && count < 128); //32);

            count = 0;
            do
            {
                bestIdx = -1;
                for (int i = 0; i < SdspDirections.Length; i++)
                {
                    var vec = searchCenter + SdspDirections[i];
                    // if (vectors.Contains(vec))
                    //     continue;
                    // vectors.Add(vec);
                    score = getDistortion(vec);
                    bitCount = getBitCount(vec);
                    rdScore = bitCount * lambda + score;
                    if (score < bestScore || score == bestScore && bitCount < bestBitCount)
                    {
                        bestScore = score;
                        bestBitCount = bitCount;
                        bestVec = vec;
                        bestIdx = i;
                    }

                    if (rdScore < bestRdScore || Math.Abs(rdScore - bestRdScore) < 0.001f && score < bestScore)
                    {
                        bestRdScore = rdScore;
                        bestRdVec = vec;
                    }

                    count++;
                }

                searchCenter = bestVec;
            } while (bestIdx != -1 && count < 128); //32);

            return bestRdVec;
        }
    }
}