using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Gericom.FastVideoDS.Bitstream;
using Gericom.FastVideoDS.Frames;
using Gericom.FastVideoDS.Utils;

namespace Gericom.FastVideoDS
{
    public class FastVideoDSEncoder
    {
        public enum FvFrameType
        {
            IFrame,
            PFrame,
            BFrame
        }

        public int Width  { get; }
        public int Height { get; }

        private int  _q;
        private bool _prevDataValid;

        private RefFrame _lastFrame;
        private int      _oldQ;

        private RefFrame _backRefFrame;
        private RefFrame _forwardRefFrame;

        private readonly FramePool _framePool;

        private readonly Queue<(RefFrame frame, bool forceIFrame)> _frameQueue = new();
        private readonly Queue<RefFrame>                           _bQueue     = new();

        private int _frameNumber = 0;

        private readonly int _maxBLength;
        private readonly int _maxGopLength;

        private int _gopLength;

        private bool _flushing = false;

        public bool FrameQueueEmpty => _frameQueue.Count == 0;

        private const float _lambda = 0.35f;

        private const float IBlockRatio = 0.6f;

        public FastVideoDSEncoder(int width, int height, int q, int maxBLength = 0, int maxGopLength = 250)
        {
            if (width <= 0 || (width & 0x7) != 0)
                throw new ArgumentOutOfRangeException(nameof(width), width, "Width should be > 0 and divisible by 8");
            if (height <= 0 || (height & 0x7) != 0)
                throw new ArgumentOutOfRangeException(nameof(height), height,
                    "Height should be > 0 and divisible by 8");
            // if (q < 12 || q > 52)
            //     throw new ArgumentOutOfRangeException(nameof(q), q, "For q should hold 12 <= q <= 52");

            if (maxBLength < 0)
                throw new ArgumentOutOfRangeException(nameof(maxBLength), maxBLength,
                    "maxBLength should be greater than or equal to zero");

            if (maxBLength != 0)
                throw new NotSupportedException("B frames are currently not supported");

            Width  = width;
            Height = height;

            _maxBLength   = maxBLength;
            _maxGopLength = maxGopLength;

            _framePool = new FramePool(Width, Height);

            _prevDataValid = false;

            _q    = q;
            _oldQ = q;
        }

        private static readonly int[] QTab4 =
        {
            262144 / 32, 262144 / 23,
            262144 / 23, 262144 / 64,
        };

        private static readonly int[] DeQTab4 =
        {
            32, 23,
            23, 64
        };

        private static readonly int[] QTab4P =
        {
            262144 / 32, 262144 / 23,
            262144 / 23, 262144 / 64
        };

        private static readonly int[] DeQTab4P =
        {
            32, 23,
            23, 64
        };

        // private static int[] QTab4 =
        // {
        //     262144 / 64, 262144 / 46,
        //     262144 / 46, 262144 / 128,
        // };
        //
        // private static int[] DeQTab4 =
        // {
        //     64, 46,
        //     46, 128
        // };

        // private static int[] QTab4P =
        // {
        //     262144 / 32, 262144 / 36,
        //     262144 / 36, 262144 / 48,
        // };
        //
        // private static int[] DeQTab4P =
        // {
        //     32, 36,
        //     36, 48
        // };

        private static bool Quantize4(int[] dct, int[] dst, int[] dstReconstructed)
        {
            for (int i = 0; i < 4; i++)
            {
                int f = Math.Min(((1 << 17) + (QTab4[i] >> 1)) / QTab4[i],
                    (((32 - 11) << 12) + (QTab4[i] >> 1)) / QTab4[i]);
                int quant = QTab4[i];
                if (dct[i] < 0)
                    dst[i] = -(((f - dct[i]) * quant) >> 18);
                else
                    dst[i] = ((f + dct[i]) * quant) >> 18;
            }

            int nrZeros = 0;
            for (int i = 0; i < 4; i++)
            {
                if (dst[i] == 0)
                    nrZeros++;
                else
                    nrZeros = 0;
            }

            for (int i = 0; i < 4; i++)
                dstReconstructed[i] = dst[i] * DeQTab4[i];

            return nrZeros == 4;
        }

        //based on dct_quantize_trellis_c in libavcodec/mpegvideo_enc.c
        private static int QuantizeCombTrellis(int[][] dcts, int[] dst)
        {
            for (int j = 0; j < 4; j++)
                for (int i = 0; i < 16; i++)
                    dst[j * 16 + i] = dcts[i][j];

            uint      bias       = 0;
            var       runTab     = new int[65];
            var       levelTab   = new int[65];
            var       scoreTab   = new int[65];
            var       survivor   = new int[65];
            int       lastRun    = 0;
            int       lastLevel  = 0;
            int       lastScore  = 0;
            var       coeff      = new int[2 * 64];
            var       coeffCount = new int[64];
            const int escLength  = 28;
            const int lambda     = 200; //3;//8;

            int startI      = 0;
            int lastNonZero = -1;
            int lastI       = startI;

            uint threshold1 = (1u << 18) - bias - 1u;
            uint threshold2 = (threshold1 << 1);

            for (int i = 63; i >= startI; i--)
            {
                int level = dst[i] * QTab4P[i >> 4];

                if ((uint)(level + threshold1) > threshold2)
                {
                    lastNonZero = i;
                    break;
                }
            }

            for (int i = startI; i <= lastNonZero; i++)
            {
                int level = dst[i] * QTab4P[i >> 4];

                if ((uint)(level + threshold1) > threshold2)
                {
                    if (level > 0)
                    {
                        level         = (int)((bias + level) >> 18);
                        coeff[i]      = level;
                        coeff[64 + i] = level - 1;
                    }
                    else
                    {
                        level         = (int)((bias - level) >> 18);
                        coeff[i]      = -level;
                        coeff[64 + i] = -level + 1;
                    }

                    coeffCount[i] = Math.Min(level, 2);
                }
                else
                {
                    coeff[i]      = (level >> 31) | 1;
                    coeffCount[i] = 1;
                }
            }

            if (lastNonZero < startI)
            {
                Array.Clear(dst, startI, 64 - startI);
                return lastNonZero;
            }

            scoreTab[startI] = 0;
            survivor[0]      = startI;
            int survivorCount = 1;

            for (int i = startI; i <= lastNonZero; i++)
            {
                int dctCoeff  = Math.Abs(dst[i]);
                int bestScore = 256 * 256 * 256 * 120;

                int zeroDistortion = dctCoeff * dctCoeff;

                for (int levelIndex = 0; levelIndex < coeffCount[i]; levelIndex++)
                {
                    int level  = coeff[levelIndex * 64 + i];
                    int alevel = Math.Abs(level);

                    int unquantCoeff = alevel * DeQTab4P[i >> 4];

                    int distortion = (unquantCoeff - dctCoeff) * (unquantCoeff - dctCoeff) - zeroDistortion;
                    level += 64;
                    if ((level & (~127)) == 0)
                    {
                        for (int j = survivorCount - 1; j >= 0; j--)
                        {
                            int run   = i - survivor[j];
                            int score = distortion + Vlc.BitLengthTable[run * 128 + level] * lambda;
                            score += scoreTab[i - run];

                            if (score < bestScore)
                            {
                                bestScore       = score;
                                runTab[i + 1]   = run;
                                levelTab[i + 1] = level - 64;
                            }
                        }

                        for (int j = survivorCount - 1; j >= 0; j--)
                        {
                            int run   = i - survivor[j];
                            int score = distortion + Vlc.BitLengthTable[64 * 128 + run * 128 + level] * lambda;
                            score += scoreTab[i - run];
                            if (score < lastScore)
                            {
                                lastScore = score;
                                lastRun   = run;
                                lastLevel = level - 64;
                                lastI     = i + 1;
                            }
                        }
                    }
                    else
                    {
                        distortion += escLength * lambda;
                        for (int j = survivorCount - 1; j >= 0; j--)
                        {
                            int run   = i - survivor[j];
                            int score = distortion + scoreTab[i - run];

                            if (score < bestScore)
                            {
                                bestScore       = score;
                                runTab[i + 1]   = run;
                                levelTab[i + 1] = level - 64;
                            }
                        }

                        for (int j = survivorCount - 1; j >= 0; j--)
                        {
                            int run   = i - survivor[j];
                            int score = distortion + scoreTab[i - run];
                            if (score < lastScore)
                            {
                                lastScore = score;
                                lastRun   = run;
                                lastLevel = level - 64;
                                lastI     = i + 1;
                            }
                        }
                    }
                }

                scoreTab[i + 1] = bestScore;

                if (lastNonZero <= 27)
                {
                    for (; survivorCount != 0; survivorCount--)
                    {
                        if (scoreTab[survivor[survivorCount - 1]] <= bestScore)
                            break;
                    }
                }
                else
                {
                    for (; survivorCount != 0; survivorCount--)
                    {
                        if (scoreTab[survivor[survivorCount - 1]] <= bestScore + lambda)
                            break;
                    }
                }

                survivor[survivorCount++] = i + 1;
            }

            int dc = Math.Abs(dst[0]);
            lastNonZero = lastI - 1;
            Array.Clear(dst, startI, 64 - startI);

            if (lastNonZero < startI)
                return lastNonZero;

            if (lastNonZero == 0 && startI == 0)
            {
                int bestLevel = 0;
                int bestScore = dc * dc;

                for (int i = 0; i < coeffCount[0]; i++)
                {
                    int level  = coeff[i * 64];
                    int alevel = Math.Abs(level);
                    int score;

                    int unquantCoeff = (alevel * DeQTab4P[i >> 4]) >> 3;
                    unquantCoeff =   (unquantCoeff + 4) >> 3;
                    unquantCoeff <<= 3 + 3;

                    int distortion = (unquantCoeff - dc) * (unquantCoeff - dc);
                    level += 64;
                    if (level == 0 + 64)
                        score = distortion;
                    else if ((level & (~127)) == 0)
                        score = distortion + Vlc.BitLengthTable[64 * 128 + level] * lambda;
                    else
                        score = distortion + escLength * lambda;

                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestLevel = level - 64;
                    }
                }

                dst[0] = bestLevel;
                if (bestLevel == 0)
                    return -1;
                else
                    return lastNonZero;
            }

            int i2 = lastI;

            dst[lastNonZero] =  lastLevel;
            i2               -= lastRun + 1;

            for (; i2 > startI; i2 -= runTab[i2] + 1)
                dst[i2 - 1] = levelTab[i2];

            return lastNonZero;
        }

        private static int QuantizeComb4PRD(int[][] dcts, int[] dst, int[][] reconstructed)
        {
            int lastZero = QuantizeCombTrellis(dcts, dst);
            for (int j = 0; j < 4; j++)
                for (int i = 0; i < 16; i++)
                    reconstructed[i][j] = dst[j * 16 + i] * DeQTab4P[j];
            if (lastZero < 0)
                return 1;
            return 1 + Vlc.CalcDctBitCount(dst, lastZero);
        }

        private static unsafe (ulong sadI, ulong sadP) SadIP(ReadOnlySpan<byte> original, ReadOnlySpan<byte> iFrame,
            ReadOnlySpan<byte> pFrame)
        {
            var sadI = Vector256<ulong>.Zero;
            var sadP = Vector256<ulong>.Zero;
            fixed (byte* pOriginal0 = original, pIFrame0 = iFrame, pPFrame0 = pFrame)
            {
                for (int i = 0; i < original.Length; i += 32)
                {
                    var originalPixels = Avx.LoadVector256(pOriginal0 + i);
                    var iPixels        = Avx.LoadVector256(pIFrame0 + i);
                    var pPixels        = Avx.LoadVector256(pPFrame0 + i);
                    var sadI2          = Avx2.SumAbsoluteDifferences(originalPixels, iPixels);
                    sadI = Avx2.Add(sadI, sadI2.AsUInt64());
                    var sadP2 = Avx2.SumAbsoluteDifferences(originalPixels, pPixels);
                    sadP = Avx2.Add(sadP, sadP2.AsUInt64());
                }
            }

            var resultI = Sse2.Add(sadI.GetLower(), sadI.GetUpper());
            var resultP = Sse2.Add(sadP.GetLower(), sadP.GetUpper());
            return (resultI.GetElement(0) + resultI.GetElement(1), resultP.GetElement(0) + resultP.GetElement(1));
        }

        private unsafe void GxAverage(ReadOnlySpan<byte> srcA, ReadOnlySpan<byte> srcB, Span<byte> dst)
        {
            fixed (byte* pA = srcA, pB = srcB, pDst = dst)
            {
                var bit0 = Vector256.Create((short)(1 << 2));
                for (int i = 0; i < srcA.Length; i += 8)
                {
                    ulong row  = *(ulong*)(pA + i);
                    ulong row2 = *(ulong*)(pB + i);

                    var a      = Avx2.ConvertToVector256Int16(Vector128.Create(row, row2).AsByte());
                    var isZero = Avx2.CompareEqual(a, Vector256<short>.Zero);
                    a = Avx2.Add(a, bit0);
                    a = Avx2.AndNot(isZero, a);
                    var b = Sse2.Add(a.GetLower(), a.GetUpper());
                    b                   = Sse2.ShiftRightLogical(b, 4);
                    b                   = Sse2.ShiftLeftLogical(b, 3);
                    *(ulong*)(pDst + i) = Sse2.PackUnsignedSaturate(b, Vector128<short>.Zero).AsUInt64().ToScalar();
                }
            }
        }

        public record EncFrame(byte[] Data, RefFrame DecFrame, FvFrameType Type, int FrameNumber);

        public void Flush()
        {
            _flushing = true;
        }

        public void SendFrame(RefFrame frame, bool forceIFrame = false)
        {
            if (_flushing)
                throw new Exception();

            _frameQueue.Enqueue((frame, forceIFrame));
        }

        public EncFrame ReceiveFrame()
        {
            if (_frameQueue.Count == 0)
                return null;

            int iFrameThreshold = (int)((Width / 8) * (Height / 8) * IBlockRatio);

            RefFrame frame;

            byte[]   data     = null;
            RefFrame decFrame = null;

            if (_bQueue.Count > 0)
            {
                if (_backRefFrame == null || _forwardRefFrame == null)
                    throw new Exception();

                frame = _bQueue.Dequeue();

                throw new NotImplementedException();
                // (data, decFrame, _) = EncodeBFrame(frame.Frame, _backRefFrame.Frame, _forwardRefFrame.Frame);
                // var p = EncodePFrame(frame.Frame, _backRefFrame.Frame);
                //
                // var (sadBR, sadPR) = SadIP(frame.Frame.R, decFrame.Frame.R, p.resultFrame.Frame.R);
                // var (sadBG, sadPG) = SadIP(frame.Frame.G, decFrame.Frame.G, p.resultFrame.Frame.G);
                // var (sadBB, sadPB) = SadIP(frame.Frame.B, decFrame.Frame.B, p.resultFrame.Frame.B);
                //
                // ulong sadB = sadBR + sadBG + sadBB;
                // ulong sadP = sadPR + sadPG + sadPB;

                frame.Unref();

                _backRefFrame.Unref();
                _backRefFrame = decFrame;
                _gopLength++;

                var result = new EncFrame(data, decFrame, FvFrameType.BFrame, _frameNumber++);
                if (_bQueue.Count == 0)
                {
                    _backRefFrame.Unref();
                    _backRefFrame    = _forwardRefFrame;
                    _forwardRefFrame = null;
                    _frameNumber++; //skip over the forward reference
                    _gopLength++;
                }

                return result;
            }

            if (_backRefFrame == null || _frameQueue.Peek().forceIFrame || _gopLength >= _maxGopLength)
            {
                (frame, _)       = _frameQueue.Dequeue();
                (data, decFrame) = EncodeIFrame(frame.Frame);
                frame.Unref();
                _backRefFrame?.Unref();
                _backRefFrame = decFrame;
                _gopLength    = 0;
                return new(data, decFrame, FvFrameType.IFrame, _frameNumber++);
            }

            if (_backRefFrame != null && _forwardRefFrame == null)
            {
                if (_frameQueue.Count < _maxBLength + 1 && !_flushing)
                    return null;

                int maxCount = Math.Min(_frameQueue.Count, _maxBLength + 1);
                if (_gopLength + maxCount > _maxGopLength)
                    maxCount = _maxGopLength - _gopLength;

                var frames = new RefFrame[maxCount];
                int count  = 0;
                for (int i = 0; i < maxCount; i++)
                {
                    var f = _frameQueue.Peek();
                    if (f.forceIFrame)
                        break;

                    var (newResultData, newResultFrame, iBlockCount) = EncodePFrame(f.frame.Frame, _backRefFrame.Frame);

                    if (iBlockCount > iFrameThreshold)
                    {
                        newResultFrame.Unref();
                        break;
                    }

                    data = newResultData;
                    decFrame?.Unref();
                    decFrame = newResultFrame;

                    frames[i] = f.frame;
                    _frameQueue.Dequeue();
                    count++;
                }

                if (count == 0)
                {
                    //Next frame will be an I frame
                    (frame, _)       = _frameQueue.Dequeue();
                    (data, decFrame) = EncodeIFrame(frame.Frame);
                    frame.Unref();
                    _backRefFrame?.Unref();
                    _backRefFrame = decFrame;
                    _gopLength    = 0;

                    return new(data, decFrame, FvFrameType.IFrame, _frameNumber++);
                }
                else if (count == 1)
                {
                    //Frame becomes regular P frame, and we have the data ready too
                    frames[0].Unref();
                    _backRefFrame?.Unref();
                    _backRefFrame = decFrame;
                    _gopLength++;

                    return new(data, decFrame, FvFrameType.PFrame, _frameNumber++);
                }
                else
                {
                    //count-1 B frames + 1 P frame
                    for (int i = 0; i < count - 1; i++)
                        _bQueue.Enqueue(frames[i]);

                    frames[count - 1].Unref();
                    _forwardRefFrame = decFrame;

                    return new(data, decFrame, FvFrameType.PFrame, _frameNumber + _bQueue.Count);
                }
            }

            return null;
        }

        private (byte[] resultData, RefFrame resultFrame) EncodeIFrame(Rgb555Frame frame)
        {
            if (frame.Width != Width)
                throw new Exception("Invalid frame width!");
            if (frame.Height != Height)
                throw new Exception("Invalid frame height!");

            var decRefFrame = _framePool.AcquireFrame();
            var decFrame    = decRefFrame.Frame;
            var bw          = new BitWriter();
            bw.WriteBits(0, 1); //0 = I frame, 1 = P frame
            bw.WriteBits((uint)_q, 6);

            var quantsR  = new int[16][];
            var quantsG  = new int[16][];
            var quantsB  = new int[16][];
            var deQuants = new int[16][];
            for (int i = 0; i < 16; i++)
            {
                quantsR[i]  = new int[4];
                quantsG[i]  = new int[4];
                quantsB[i]  = new int[4];
                deQuants[i] = new int[4];
            }

            var data    = new byte[4];
            var dataG   = new byte[4];
            var intData = new int[4];
            var dct     = new int[4];
            var undct   = new byte[4];

            int lastGDC = 0;
            for (int y = 0; y < Height; y += 8)
            {
                for (int x = 0; x < Width; x += 8)
                {
                    int i = 0;
                    for (int y3 = 0; y3 < 8; y3 += 4)
                    {
                        for (int x3 = 0; x3 < 8; x3 += 4)
                        {
                            for (int y2 = 0; y2 < 2; y2++)
                            {
                                for (int x2 = 0; x2 < 2; x2++)
                                {
                                    FrameUtil.GetTile2x2Step2(frame.G, frame.Width, x + x2 + x3, y + y2 + y3, data);
                                    if ((x3 + x2) == 0 && (y3 + y2) == 0)
                                        Array.Clear(dataG, 0, 4);
                                    else if (x2 == 0 && y2 != 0)
                                        FrameUtil.GetTile2x2Step2(
                                            decFrame.G, decFrame.Width, x + x3, y + y2 + y3 - 1, dataG);
                                    else if (x2 == 0 && x3 != 0)
                                        FrameUtil.GetTile2x2Step2(
                                            decFrame.G, decFrame.Width, x + x2 + x3 - 4, y + y2 + y3, dataG);
                                    else if (x2 == 0 && y2 == 0 && y3 != 0)
                                        FrameUtil.GetTile2x2Step2(
                                            decFrame.G, decFrame.Width, x + x2 + x3, y + y2 + y3 - 4, dataG);
                                    else
                                        FrameUtil.GetTile2x2Step2(
                                            decFrame.G, decFrame.Width, x + x2 + x3 - 1, y + y2 + y3, dataG);

                                    for (int j = 0; j < 4; j++)
                                        intData[j] = data[j] - dataG[j];
                                    Dct.Dct4NoDiv(intData, dct);
                                    Quantize4(dct, quantsG[i], deQuants[i]);
                                    Dct.IDct4(deQuants[i], dataG, undct);
                                    FrameUtil.SetTile2x2Step2(decFrame.G, decFrame.Width, x + x2 + x3, y + y2 + y3,
                                        undct);
                                    i++;
                                }
                            }
                        }
                    }

                    var combDct2 = new int[64];
                    for (int j = 0; j < 4; j++)
                    {
                        for (i = 0; i < 16; i++)
                        {
                            combDct2[j * 16 + i] = quantsG[i][j];
                        }
                    }

                    combDct2[0] -= lastGDC;
                    lastGDC     =  quantsG[0][0];

                    Vlc.EncodeDct(combDct2, bw);

                    i = 0;
                    for (int y3 = 0; y3 < 8; y3 += 4)
                    {
                        for (int x3 = 0; x3 < 8; x3 += 4)
                        {
                            for (int y2 = 0; y2 < 2; y2++)
                            {
                                for (int x2 = 0; x2 < 2; x2++)
                                {
                                    FrameUtil.GetTile2x2Step2(frame.R, frame.Width, x + x2 + x3, y + y2 + y3, data);
                                    if ((x3 + x2) == 0 && (y3 + y2) == 0)
                                        FrameUtil.GetTile2x2Step2(
                                            decFrame.G, decFrame.Width, x + x2 + x3, y + y2 + y3, dataG);
                                    else if (x2 == 0 && y2 != 0)
                                        FrameUtil.GetTile2x2Step2(
                                            decFrame.R, decFrame.Width, x + x3, y + y2 + y3 - 1, dataG);
                                    else if (x2 == 0 && x3 != 0)
                                        FrameUtil.GetTile2x2Step2(
                                            decFrame.R, decFrame.Width, x + x2 + x3 - 4, y + y2 + y3, dataG);
                                    else if (x2 == 0 && y2 == 0 && y3 != 0)
                                        FrameUtil.GetTile2x2Step2(
                                            decFrame.R, decFrame.Width, x + x2 + x3, y + y2 + y3 - 4, dataG);
                                    else
                                        FrameUtil.GetTile2x2Step2(
                                            decFrame.R, decFrame.Width, x + x2 + x3 - 1, y + y2 + y3, dataG);
                                    for (int j = 0; j < 4; j++)
                                        intData[j] = data[j] - dataG[j];
                                    Dct.Dct4NoDiv(intData, dct);
                                    Quantize4(dct, quantsR[i], deQuants[i]);
                                    Dct.IDct4(deQuants[i], dataG, undct);
                                    FrameUtil.SetTile2x2Step2(decFrame.R, decFrame.Width, x + x2 + x3, y + y2 + y3,
                                        undct);
                                    i++;
                                }
                            }
                        }
                    }

                    for (int j = 0; j < 4; j++)
                    {
                        for (i = 0; i < 16; i++)
                        {
                            combDct2[j * 16 + i] = quantsR[i][j];
                        }
                    }

                    Vlc.EncodeDct(combDct2, bw);

                    i = 0;
                    for (int y3 = 0; y3 < 8; y3 += 4)
                    {
                        for (int x3 = 0; x3 < 8; x3 += 4)
                        {
                            for (int y2 = 0; y2 < 2; y2++)
                            {
                                for (int x2 = 0; x2 < 2; x2++)
                                {
                                    FrameUtil.GetTile2x2Step2(frame.B, frame.Width, x + x2 + x3, y + y2 + y3, data);
                                    if ((x3 + x2) == 0 && (y3 + y2) == 0)
                                        FrameUtil.GetTile2x2Step2(
                                            decFrame.G, decFrame.Width, x + x2 + x3, y + y2 + y3, dataG);
                                    else if (x2 == 0 && y2 != 0)
                                        FrameUtil.GetTile2x2Step2(
                                            decFrame.B, decFrame.Width, x + x3, y + y2 + y3 - 1, dataG);
                                    else if (x2 == 0 && x3 != 0)
                                        FrameUtil.GetTile2x2Step2(
                                            decFrame.B, decFrame.Width, x + x2 + x3 - 4, y + y2 + y3, dataG);
                                    else if (x2 == 0 && y2 == 0 && y3 != 0)
                                        FrameUtil.GetTile2x2Step2(
                                            decFrame.B, decFrame.Width, x + x2 + x3, y + y2 + y3 - 4, dataG);
                                    else
                                        FrameUtil.GetTile2x2Step2(
                                            decFrame.B, decFrame.Width, x + x2 + x3 - 1, y + y2 + y3, dataG);
                                    for (int j = 0; j < 4; j++)
                                        intData[j] = data[j] - dataG[j];
                                    Dct.Dct4NoDiv(intData, dct);
                                    Quantize4(dct, quantsB[i], deQuants[i]);
                                    Dct.IDct4(deQuants[i], dataG, undct);
                                    FrameUtil.SetTile2x2Step2(decFrame.B, decFrame.Width, x + x2 + x3, y + y2 + y3,
                                        undct);
                                    i++;
                                }
                            }
                        }
                    }

                    for (int j = 0; j < 4; j++)
                    {
                        for (i = 0; i < 16; i++)
                        {
                            combDct2[j * 16 + i] = quantsB[i][j];
                        }
                    }

                    Vlc.EncodeDct(combDct2, bw);
                }
            }


            bw.Flush();
            return (bw.ToArray(), decRefFrame);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe bool AllZero(int[] src)
        {
            fixed (int* pSrc = src)
            {
                var a = Avx.LoadVector256(pSrc);
                a = Avx2.Or(a, Avx.LoadVector256(pSrc + 1 * 8));
                a = Avx2.Or(a, Avx.LoadVector256(pSrc + 2 * 8));
                a = Avx2.Or(a, Avx.LoadVector256(pSrc + 3 * 8));
                a = Avx2.Or(a, Avx.LoadVector256(pSrc + 4 * 8));
                a = Avx2.Or(a, Avx.LoadVector256(pSrc + 5 * 8));
                a = Avx2.Or(a, Avx.LoadVector256(pSrc + 6 * 8));
                a = Avx2.Or(a, Avx.LoadVector256(pSrc + 7 * 8));
                return Avx.TestZ(a, Vector256<int>.AllBitsSet);
            }
        }

        private class BlockConfig
        {
            public readonly int[][] DctsR     = new int[16][];
            public readonly int[][] QuantsR   = new int[16][];
            public readonly int[][] DeQuantsR = new int[16][];
            public readonly int[]   CombDctR  = new int[64];

            public readonly int[][] DctsG     = new int[16][];
            public readonly int[][] QuantsG   = new int[16][];
            public readonly int[][] DeQuantsG = new int[16][];
            public readonly int[]   CombDctG  = new int[64];

            public readonly int[][] DctsB     = new int[16][];
            public readonly int[][] QuantsB   = new int[16][];
            public readonly int[][] DeQuantsB = new int[16][];
            public readonly int[]   CombDctB  = new int[64];

            public BlockConfig()
            {
                for (int i = 0; i < 16; i++)
                {
                    DctsR[i]     = new int[4];
                    QuantsR[i]   = new int[4];
                    DeQuantsR[i] = new int[4];
                    DctsG[i]     = new int[4];
                    QuantsG[i]   = new int[4];
                    DeQuantsG[i] = new int[4];
                    DctsB[i]     = new int[4];
                    QuantsB[i]   = new int[4];
                    DeQuantsB[i] = new int[4];
                }
            }
        }

        private (byte[] resultData, RefFrame resultFrame, int iBlockCount) EncodePFrame(Rgb555Frame frame,
            Rgb555Frame refFrame)
        {
            if (frame.Width != Width)
                throw new Exception("Invalid frame width!");
            if (frame.Height != Height)
                throw new Exception("Invalid frame height!");

            int iBlockCount = 0;

            var decRefFrame = _framePool.AcquireFrame();
            var decFrame    = decRefFrame.Frame;
            var vectorBw    = new BitWriter();
            var dctBw       = new BitWriter();
            vectorBw.WriteBits(1, 1); //0 = I frame, 1 = P frame
            // bw.WriteVarIntSigned(_q - _oldQ);

            var iBlockConfig = new BlockConfig();
            var pBlockConfig = new BlockConfig();

            var vecBuf = new MotionVector[2][];
            vecBuf[0] = new MotionVector[Width / 8];
            vecBuf[1] = new MotionVector[Width / 8];
            int curVecBuf = 0;

            var lastVec = new MotionVector(0, 0);

            var predR     = new byte[64];
            var predG     = new byte[64];
            var predB     = new byte[64];
            var fullDataR = new byte[64];
            var fullDataG = new byte[64];
            var fullDataB = new byte[64];
            var tmp       = new byte[64];
            var data      = new byte[4];
            var dataG     = new byte[4];
            var intData   = new int[4];
            var dct       = new int[4];
            var undct     = new byte[4];

            // int lastGDC = 0;
            for (int y = 0; y < Height; y += 8)
            {
                for (int x = 0; x < Width; x += 8)
                {
                    int predX = 0;
                    int predY = 0;

                    var mvecX = new List<int>();
                    var mvecY = new List<int>();
                    if (x != 0)
                    {
                        mvecX.Add(vecBuf[curVecBuf][(x >> 3) - 1].X);
                        mvecY.Add(vecBuf[curVecBuf][(x >> 3) - 1].Y);
                    }

                    if (y != 0)
                    {
                        mvecX.Add(vecBuf[1 - curVecBuf][x >> 3].X);
                        mvecY.Add(vecBuf[1 - curVecBuf][x >> 3].Y);
                        if (x != Width - 8)
                        {
                            mvecX.Add(vecBuf[1 - curVecBuf][(x >> 3) + 1].X);
                            mvecY.Add(vecBuf[1 - curVecBuf][(x >> 3) + 1].Y);
                        }
                    }

                    if (mvecX.Count != 0)
                    {
                        mvecX.Sort();
                        mvecY.Sort();
                        predX = mvecX[mvecX.Count / 2];
                        predY = mvecY[mvecY.Count / 2];
                    }

                    lastVec.X = predX;
                    lastVec.Y = predY;

                    FrameUtil.GetTile8(frame.R, frame.Width, x, y, fullDataR);
                    FrameUtil.GetTile8(frame.G, frame.Width, x, y, fullDataG);
                    FrameUtil.GetTile8(frame.B, frame.Width, x, y, fullDataB);

                    // Rgb555Frame refFrame = _lastFrame;

                    int bestScore;
                    var vec = MotionEstimation.FindMotionVector(fullDataR, fullDataG, fullDataB, refFrame,
                        new MotionVector(x << 1, y << 1),
                        new MotionVector(lastVec.X + (x << 1), lastVec.Y + (y << 1)),
                        out bestScore);

                    FrameUtil.GetTileHalf8(refFrame.R, refFrame.Width, refFrame.Height, vec.X, vec.Y, predR);
                    FrameUtil.GetTileHalf8(refFrame.G, refFrame.Width, refFrame.Height, vec.X, vec.Y, predG);
                    FrameUtil.GetTileHalf8(refFrame.B, refFrame.Width, refFrame.Height, vec.X, vec.Y, predB);

                    vec.X -= x << 1;
                    vec.Y -= y << 1;

                    int iBitCount = 1;
                    var iTmpG     = new byte[64];
                    var iTmpR     = new byte[64];
                    var iTmpB     = new byte[64];
                    {
                        int i = 0;
                        for (int y3 = 0; y3 < 8; y3 += 4)
                        {
                            for (int x3 = 0; x3 < 8; x3 += 4)
                            {
                                for (int y2 = 0; y2 < 2; y2++)
                                {
                                    for (int x2 = 0; x2 < 2; x2++)
                                    {
                                        FrameUtil.GetTile2x2Step2(frame.G, frame.Width, x + x2 + x3, y + y2 + y3, data);
                                        if ((x3 + x2) == 0 && (y3 + y2) == 0)
                                            Array.Clear(dataG, 0, 4);
                                        else if (x2 == 0 && y2 != 0)
                                            FrameUtil.GetTile2x2Step2(iTmpG, 8, x3, y2 + y3 - 1, dataG);
                                        else if (x2 == 0 && x3 != 0)
                                            FrameUtil.GetTile2x2Step2(iTmpG, 8, x2 + x3 - 4, y2 + y3, dataG);
                                        else if (x2 == 0 && y2 == 0 && y3 != 0)
                                            FrameUtil.GetTile2x2Step2(iTmpG, 8, x2 + x3, y2 + y3 - 4, dataG);
                                        else
                                            FrameUtil.GetTile2x2Step2(iTmpG, 8, x2 + x3 - 1, y2 + y3, dataG);

                                        for (int j = 0; j < 4; j++)
                                            intData[j] = data[j] - dataG[j];
                                        Dct.Dct4NoDiv(intData, dct);
                                        Quantize4(dct, iBlockConfig.QuantsG[i], iBlockConfig.DeQuantsG[i]);
                                        Dct.IDct4(iBlockConfig.DeQuantsG[i], dataG, undct);
                                        FrameUtil.SetTile2x2Step2(iTmpG, 8, x2 + x3, y2 + y3, undct);
                                        i++;
                                    }
                                }
                            }
                        }

                        for (int j = 0; j < 4; j++)
                        {
                            for (i = 0; i < 16; i++)
                            {
                                iBlockConfig.CombDctG[j * 16 + i] = iBlockConfig.QuantsG[i][j];
                            }
                        }

                        // iBlockConfig.CombDctG[0] -= 512 * QTab4[0] >> 18;//lastGDC;
                        // lastGDC = quantsG[0][0];

                        iBitCount += Vlc.CalcDctBitCount(iBlockConfig.CombDctG);
                        // EncodeDCT(combDct2, 1, bw);

                        i = 0;
                        for (int y3 = 0; y3 < 8; y3 += 4)
                        {
                            for (int x3 = 0; x3 < 8; x3 += 4)
                            {
                                for (int y2 = 0; y2 < 2; y2++)
                                {
                                    for (int x2 = 0; x2 < 2; x2++)
                                    {
                                        FrameUtil.GetTile2x2Step2(frame.R, frame.Width, x + x2 + x3, y + y2 + y3, data);
                                        if ((x3 + x2) == 0 && (y3 + y2) == 0)
                                            FrameUtil.GetTile2x2Step2(iTmpG, 8, x2 + x3, y2 + y3, dataG);
                                        else if (x2 == 0 && y2 != 0)
                                            FrameUtil.GetTile2x2Step2(iTmpR, 8, x3, y2 + y3 - 1, dataG);
                                        else if (x2 == 0 && x3 != 0)
                                            FrameUtil.GetTile2x2Step2(iTmpR, 8, x2 + x3 - 4, y2 + y3, dataG);
                                        else if (x2 == 0 && y2 == 0 && y3 != 0)
                                            FrameUtil.GetTile2x2Step2(iTmpR, 8, x2 + x3, y2 + y3 - 4, dataG);
                                        else
                                            FrameUtil.GetTile2x2Step2(iTmpR, 8, x2 + x3 - 1, y2 + y3, dataG);
                                        for (int j = 0; j < 4; j++)
                                            intData[j] = data[j] - dataG[j];
                                        Dct.Dct4NoDiv(intData, dct);
                                        Quantize4(dct, iBlockConfig.QuantsR[i], iBlockConfig.DeQuantsR[i]);
                                        Dct.IDct4(iBlockConfig.DeQuantsR[i], dataG, undct);
                                        FrameUtil.SetTile2x2Step2(iTmpR, 8, x2 + x3, y2 + y3, undct);
                                        i++;
                                    }
                                }
                            }
                        }

                        for (int j = 0; j < 4; j++)
                        {
                            for (i = 0; i < 16; i++)
                            {
                                iBlockConfig.CombDctR[j * 16 + i] = iBlockConfig.QuantsR[i][j];
                            }
                        }

                        iBitCount += Vlc.CalcDctBitCount(iBlockConfig.CombDctR);
                        // EncodeDCT(combDct2, 1, bw);

                        i = 0;
                        for (int y3 = 0; y3 < 8; y3 += 4)
                        {
                            for (int x3 = 0; x3 < 8; x3 += 4)
                            {
                                for (int y2 = 0; y2 < 2; y2++)
                                {
                                    for (int x2 = 0; x2 < 2; x2++)
                                    {
                                        FrameUtil.GetTile2x2Step2(frame.B, frame.Width, x + x2 + x3, y + y2 + y3, data);
                                        if ((x3 + x2) == 0 && (y3 + y2) == 0)
                                            FrameUtil.GetTile2x2Step2(iTmpG, 8, x2 + x3, y2 + y3, dataG);
                                        else if (x2 == 0 && y2 != 0)
                                            FrameUtil.GetTile2x2Step2(iTmpB, 8, x3, y2 + y3 - 1, dataG);
                                        else if (x2 == 0 && x3 != 0)
                                            FrameUtil.GetTile2x2Step2(iTmpB, 8, x2 + x3 - 4, y2 + y3, dataG);
                                        else if (x2 == 0 && y2 == 0 && y3 != 0)
                                            FrameUtil.GetTile2x2Step2(iTmpB, 8, x2 + x3, y2 + y3 - 4, dataG);
                                        else
                                            FrameUtil.GetTile2x2Step2(iTmpB, 8, x2 + x3 - 1, y2 + y3, dataG);
                                        for (int j = 0; j < 4; j++)
                                            intData[j] = data[j] - dataG[j];
                                        Dct.Dct4NoDiv(intData, dct);
                                        Quantize4(dct, iBlockConfig.QuantsB[i], iBlockConfig.DeQuantsB[i]);
                                        Dct.IDct4(iBlockConfig.DeQuantsB[i], dataG, undct);
                                        FrameUtil.SetTile2x2Step2(iTmpB, 8, x2 + x3, y2 + y3, undct);
                                        i++;
                                    }
                                }
                            }
                        }

                        for (int j = 0; j < 4; j++)
                        {
                            for (i = 0; i < 16; i++)
                            {
                                iBlockConfig.CombDctB[j * 16 + i] = iBlockConfig.QuantsB[i][j];
                            }
                        }

                        iBitCount += Vlc.CalcDctBitCount(iBlockConfig.CombDctB);
                        // EncodeDCT(combDct2, 1, bw);
                    }

                    int pBitCount = 0;
                    var pTmpG     = new byte[64];
                    var pTmpR     = new byte[64];
                    var pTmpB     = new byte[64];
                    {
                        pBitCount++;
                        if (vec != lastVec)
                        {
                            pBitCount += BitWriter.GetSignedVarIntBitCount(vec.X - lastVec.X);
                            pBitCount += BitWriter.GetSignedVarIntBitCount(vec.Y - lastVec.Y);
                        }

                        int i = 0;
                        for (int y3 = 0; y3 < 8; y3 += 4)
                        {
                            for (int x3 = 0; x3 < 8; x3 += 4)
                            {
                                for (int y2 = 0; y2 < 2; y2++)
                                {
                                    for (int x2 = 0; x2 < 2; x2++)
                                    {
                                        FrameUtil.GetTile2x2Step2(frame.G, frame.Width, x + x2 + x3, y + y2 + y3, data);
                                        int r = 0;
                                        for (int ry = y3 + y2; ry < y3 + y2 + 4; ry += 2)
                                            for (int rx = x3 + x2; rx < x3 + x2 + 4; rx += 2)
                                                dataG[r++] = predG[ry * 8 + rx];
                                        for (int j = 0; j < 4; j++)
                                            intData[j] = data[j] - dataG[j];
                                        Dct.Dct4NoDiv(intData, pBlockConfig.DctsG[i]);
                                        i++;
                                    }
                                }
                            }
                        }

                        pBitCount += QuantizeComb4PRD(pBlockConfig.DctsG, pBlockConfig.CombDctG,
                            pBlockConfig.DeQuantsG);
                        i = 0;
                        for (int y3 = 0; y3 < 8; y3 += 4)
                        {
                            for (int x3 = 0; x3 < 8; x3 += 4)
                            {
                                for (int y2 = 0; y2 < 2; y2++)
                                {
                                    for (int x2 = 0; x2 < 2; x2++)
                                    {
                                        int r = 0;
                                        for (int ry = y3 + y2; ry < y3 + y2 + 4; ry += 2)
                                            for (int rx = x3 + x2; rx < x3 + x2 + 4; rx += 2)
                                                dataG[r++] = predG[ry * 8 + rx];
                                        Dct.IDct4(pBlockConfig.DeQuantsG[i], dataG, undct);
                                        FrameUtil.SetTile2x2Step2(pTmpG, 8, x2 + x3, y2 + y3, undct);
                                        i++;
                                    }
                                }
                            }
                        }

                        // for (int j = 0; j < 4; j++)
                        // {
                        //     for (i = 0; i < 16; i++)
                        //     {
                        //         pBlockConfig.CombDctG[j * 16 + i] = pBlockConfig.QuantsG[i][j];
                        //     }
                        // }
                        //
                        // pBitCount++;
                        // if (pBlockConfig.CombDctG.Any(a => a != 0))
                        //     pBitCount += CalcDCTBitCount(pBlockConfig.CombDctG, 1);

                        // pBitCount += CalcBitsQuantsP2(pBlockConfig.QuantsG);

                        //pBitCount += CalcBitsQuantsP(pBlockConfig.QuantsG);

                        i = 0;
                        for (int y3 = 0; y3 < 8; y3 += 4)
                        {
                            for (int x3 = 0; x3 < 8; x3 += 4)
                            {
                                for (int y2 = 0; y2 < 2; y2++)
                                {
                                    for (int x2 = 0; x2 < 2; x2++)
                                    {
                                        FrameUtil.GetTile2x2Step2(frame.R, frame.Width, x + x2 + x3, y + y2 + y3, data);
                                        int r = 0;
                                        for (int ry = y3 + y2; ry < y3 + y2 + 4; ry += 2)
                                            for (int rx = x3 + x2; rx < x3 + x2 + 4; rx += 2)
                                                dataG[r++] = predR[ry * 8 + rx];
                                        for (int j = 0; j < 4; j++)
                                            intData[j] = data[j] - dataG[j];
                                        Dct.Dct4NoDiv(intData, pBlockConfig.DctsR[i]);
                                        // Quantize4P(pBlockConfig.DctsR[i], pBlockConfig.QuantsR[i], pBlockConfig.DeQuantsR[i]);
                                        // FrameUtil.SetTile(pTmpR, x2 + x3, y2 + y3, 2, 2, 2,
                                        //     DCTUtil.IDCT4(pBlockConfig.DeQuantsR[i], dataG));
                                        i++;
                                    }
                                }
                            }
                        }

                        pBitCount += QuantizeComb4PRD(pBlockConfig.DctsR, pBlockConfig.CombDctR,
                            pBlockConfig.DeQuantsR);

                        i = 0;
                        for (int y3 = 0; y3 < 8; y3 += 4)
                        {
                            for (int x3 = 0; x3 < 8; x3 += 4)
                            {
                                for (int y2 = 0; y2 < 2; y2++)
                                {
                                    for (int x2 = 0; x2 < 2; x2++)
                                    {
                                        int r = 0;
                                        for (int ry = y3 + y2; ry < y3 + y2 + 4; ry += 2)
                                            for (int rx = x3 + x2; rx < x3 + x2 + 4; rx += 2)
                                                dataG[r++] = predR[ry * 8 + rx];
                                        Dct.IDct4(pBlockConfig.DeQuantsR[i], dataG, undct);
                                        FrameUtil.SetTile2x2Step2(pTmpR, 8, x2 + x3, y2 + y3, undct);
                                        i++;
                                    }
                                }
                            }
                        }

                        // for (int j = 0; j < 4; j++)
                        // {
                        //     for (i = 0; i < 16; i++)
                        //     {
                        //         pBlockConfig.CombDctR[j * 16 + i] = pBlockConfig.QuantsR[i][j];
                        //     }
                        // }
                        //
                        // pBitCount++;
                        // if (pBlockConfig.CombDctR.Any(a => a != 0))
                        //     pBitCount += CalcDCTBitCount(pBlockConfig.CombDctR, 1);

                        // pBitCount += CalcBitsQuantsP2(pBlockConfig.QuantsR);
                        // pBitCount += CalcDCTBitCount(pBlockConfig.CombDctR, 1);//pBitCount += CalcBitsQuantsP(pBlockConfig.QuantsR);

                        i = 0;
                        for (int y3 = 0; y3 < 8; y3 += 4)
                        {
                            for (int x3 = 0; x3 < 8; x3 += 4)
                            {
                                for (int y2 = 0; y2 < 2; y2++)
                                {
                                    for (int x2 = 0; x2 < 2; x2++)
                                    {
                                        FrameUtil.GetTile2x2Step2(frame.B, frame.Width, x + x2 + x3, y + y2 + y3, data);
                                        int r = 0;
                                        for (int ry = y3 + y2; ry < y3 + y2 + 4; ry += 2)
                                            for (int rx = x3 + x2; rx < x3 + x2 + 4; rx += 2)
                                                dataG[r++] = predB[ry * 8 + rx];
                                        for (int j = 0; j < 4; j++)
                                            intData[j] = data[j] - dataG[j];
                                        Dct.Dct4NoDiv(intData, pBlockConfig.DctsB[i]);
                                        // Quantize4P(dct, pBlockConfig.QuantsB[i], pBlockConfig.DeQuantsB[i]);
                                        // FrameUtil.SetTile(pTmpB, x2 + x3, y2 + y3, 2, 2, 2,
                                        //     DCTUtil.IDCT4(pBlockConfig.DeQuantsB[i], dataG));
                                        i++;
                                    }
                                }
                            }
                        }

                        pBitCount += QuantizeComb4PRD(pBlockConfig.DctsB, pBlockConfig.CombDctB,
                            pBlockConfig.DeQuantsB);

                        i = 0;
                        for (int y3 = 0; y3 < 8; y3 += 4)
                        {
                            for (int x3 = 0; x3 < 8; x3 += 4)
                            {
                                for (int y2 = 0; y2 < 2; y2++)
                                {
                                    for (int x2 = 0; x2 < 2; x2++)
                                    {
                                        int r = 0;
                                        for (int ry = y3 + y2; ry < y3 + y2 + 4; ry += 2)
                                            for (int rx = x3 + x2; rx < x3 + x2 + 4; rx += 2)
                                                dataG[r++] = predB[ry * 8 + rx];
                                        Dct.IDct4(pBlockConfig.DeQuantsB[i], dataG, undct);
                                        FrameUtil.SetTile2x2Step2(pTmpB, 8, x2 + x3, y2 + y3, undct);
                                        i++;
                                    }
                                }
                            }
                        }

                        // for (int j = 0; j < 4; j++)
                        // {
                        //     for (i = 0; i < 16; i++)
                        //     {
                        //         pBlockConfig.CombDctB[j * 16 + i] = pBlockConfig.QuantsB[i][j];
                        //     }
                        // }
                        //
                        // pBitCount++;
                        // if (pBlockConfig.CombDctB.Any(a => a != 0))
                        //     pBitCount += CalcDCTBitCount(pBlockConfig.CombDctB, 1);

                        // pBitCount += CalcBitsQuantsP2(pBlockConfig.QuantsB);
                        // pBitCount += CalcDCTBitCount(pBlockConfig.CombDctB, 1);//pBitCount += CalcBitsQuantsP(pBlockConfig.QuantsB);
                    }

                    FrameUtil.GetTile8(frame.G, frame.Width, x, y, tmp);
                    int diffI = FrameUtil.Sad64(iTmpG, tmp);
                    int diffP = FrameUtil.Sad64(pTmpG, tmp);
                    FrameUtil.GetTile8(frame.R, frame.Width, x, y, tmp);
                    diffI += FrameUtil.Sad64(iTmpR, tmp);
                    diffP += FrameUtil.Sad64(pTmpR, tmp);
                    FrameUtil.GetTile8(frame.B, frame.Width, x, y, tmp);
                    diffI += FrameUtil.Sad64(iTmpB, tmp);
                    diffP += FrameUtil.Sad64(pTmpB, tmp);

                    if (iBitCount + diffI * _lambda < pBitCount + diffP * _lambda)
                    {
                        // dctCoefCount += iBlockConfig.CombDctG.Count(a => a != 0);
                        // dctCoefCount += iBlockConfig.CombDctR.Count(a => a != 0);
                        // dctCoefCount += iBlockConfig.CombDctB.Count(a => a != 0);
                        dctBw.WriteBits(1, 1); //I-block
                        vectorBw.WriteBits(1, 1);
                        Vlc.EncodeDct(iBlockConfig.CombDctG, dctBw);
                        Vlc.EncodeDct(iBlockConfig.CombDctR, dctBw);
                        Vlc.EncodeDct(iBlockConfig.CombDctB, dctBw);

                        FrameUtil.SetTile8(decFrame.R, decFrame.Width, x, y, iTmpR);
                        FrameUtil.SetTile8(decFrame.G, decFrame.Width, x, y, iTmpG);
                        FrameUtil.SetTile8(decFrame.B, decFrame.Width, x, y, iTmpB);

                        vecBuf[curVecBuf][x >> 3] = lastVec;

                        iBlockCount++;
                    }
                    else
                    {
                        dctBw.WriteBits(0, 1); //P-block
                        if (vec == lastVec)
                        {
                            vectorBw.WriteBits(1, 1);
                        }
                        else
                        {
                            vectorBw.WriteBits(0, 1);
                            vectorBw.WriteSignedVarInt(vec.X - lastVec.X);
                            vectorBw.WriteSignedVarInt(vec.Y - lastVec.Y);
                        }

                        // EncodeQuantsP(bw, pBlockConfig.QuantsG);
                        // EncodeQuantsP(bw, pBlockConfig.QuantsR);
                        // EncodeQuantsP(bw, pBlockConfig.QuantsB);
                        // EncodeQuantsP2(bw, pBlockConfig.QuantsG);
                        // EncodeQuantsP2(bw, pBlockConfig.QuantsR);
                        // EncodeQuantsP2(bw, pBlockConfig.QuantsB);

                        if (!AllZero(pBlockConfig.CombDctG))
                        {
                            dctBw.WriteBits(1, 1);
                            Vlc.EncodeDct(pBlockConfig.CombDctG, dctBw);
                        }
                        else
                            dctBw.WriteBits(0, 1);

                        if (!AllZero(pBlockConfig.CombDctR))
                        {
                            dctBw.WriteBits(1, 1);
                            Vlc.EncodeDct(pBlockConfig.CombDctR, dctBw);
                        }
                        else
                            dctBw.WriteBits(0, 1);

                        if (!AllZero(pBlockConfig.CombDctB))
                        {
                            dctBw.WriteBits(1, 1);
                            Vlc.EncodeDct(pBlockConfig.CombDctB, dctBw);
                        }
                        else
                            dctBw.WriteBits(0, 1);

                        FrameUtil.SetTile8(decFrame.R, decFrame.Width, x, y, pTmpR);
                        FrameUtil.SetTile8(decFrame.G, decFrame.Width, x, y, pTmpG);
                        FrameUtil.SetTile8(decFrame.B, decFrame.Width, x, y, pTmpB);

                        vecBuf[curVecBuf][x >> 3] = vec;
                        // dctCoefCount += pBlockConfig.CombDctG.Count(a => a != 0);
                        // dctCoefCount += pBlockConfig.CombDctR.Count(a => a != 0);
                        // dctCoefCount += pBlockConfig.CombDctB.Count(a => a != 0);
                    }
                }

                curVecBuf = 1 - curVecBuf;
            }

            // Console.WriteLine(dctCoefCount);

            var vecData = vectorBw.ToArray();
            var dctData = dctBw.ToArray();

            var finalData = new byte[vecData.Length + dctData.Length];
            vecData.CopyTo(finalData, 0);
            dctData.CopyTo(finalData, vecData.Length);

            return (finalData, decRefFrame, iBlockCount);
        }

        // private (byte[] resultData, RefFrame resultFrame, int iBlockCount) EncodeBFrame(Rgb555Frame frame,
        //     Rgb555Frame backRefFrame, Rgb555Frame forwardRefFrame)
        // {
        //     if (frame.Width != Width)
        //         throw new Exception("Invalid frame width!");
        //     if (frame.Height != Height)
        //         throw new Exception("Invalid frame height!");
        //
        //     // var refFrame = new Rgb555Frame(frame.Width, frame.Height);
        //     // GxAverage(backRefFrame.R, forwardRefFrame.R, refFrame.R);
        //     // GxAverage(backRefFrame.G, forwardRefFrame.G, refFrame.G);
        //     // GxAverage(backRefFrame.B, forwardRefFrame.B, refFrame.B);
        //
        //     int iBlockCount = 0;
        //
        //     var decRefFrame = _framePool.AcquireFrame();
        //     var decFrame    = decRefFrame.Frame;
        //     var vectorBw    = new BitWriter();
        //     var dctBw       = new BitWriter();
        //     vectorBw.WriteBits(1, 1); //0 = I frame, 1 = P frame
        //     // bw.WriteVarIntSigned(_q - _oldQ);
        //
        //     var iBlockConfig = new BlockConfig();
        //     var pBlockConfig = new BlockConfig();
        //
        //     var vecBuf = new MotionVector[2][];
        //     vecBuf[0] = new MotionVector[Width / 8];
        //     vecBuf[1] = new MotionVector[Width / 8];
        //     int curVecBuf = 0;
        //
        //     var lastVec = new MotionVector(0, 0);
        //
        //     var predR     = new byte[64];
        //     var predG     = new byte[64];
        //     var predB     = new byte[64];
        //     var fullDataR = new byte[64];
        //     var fullDataG = new byte[64];
        //     var fullDataB = new byte[64];
        //     var tmp       = new byte[64];
        //     var data      = new byte[4];
        //     var dataG     = new byte[4];
        //     var intData   = new int[4];
        //     var dct       = new int[4];
        //     var undct     = new byte[4];
        //
        //     // int lastGDC = 0;
        //     for (int y = 0; y < Height; y += 8)
        //     {
        //         for (int x = 0; x < Width; x += 8)
        //         {
        //             int predX = 0;
        //             int predY = 0;
        //
        //             var mvecX = new List<int>();
        //             var mvecY = new List<int>();
        //             if (x != 0)
        //             {
        //                 mvecX.Add(vecBuf[curVecBuf][(x >> 3) - 1].X);
        //                 mvecY.Add(vecBuf[curVecBuf][(x >> 3) - 1].Y);
        //             }
        //
        //             if (y != 0)
        //             {
        //                 mvecX.Add(vecBuf[1 - curVecBuf][x >> 3].X);
        //                 mvecY.Add(vecBuf[1 - curVecBuf][x >> 3].Y);
        //                 if (x != Width - 8)
        //                 {
        //                     mvecX.Add(vecBuf[1 - curVecBuf][(x >> 3) + 1].X);
        //                     mvecY.Add(vecBuf[1 - curVecBuf][(x >> 3) + 1].Y);
        //                 }
        //             }
        //
        //             if (mvecX.Count != 0)
        //             {
        //                 mvecX.Sort();
        //                 mvecY.Sort();
        //                 predX = mvecX[mvecX.Count / 2];
        //                 predY = mvecY[mvecY.Count / 2];
        //             }
        //
        //             lastVec.X = predX;
        //             lastVec.Y = predY;
        //
        //             FrameUtil.GetTile8(frame.R, frame.Width, x, y, fullDataR);
        //             FrameUtil.GetTile8(frame.G, frame.Width, x, y, fullDataG);
        //             FrameUtil.GetTile8(frame.B, frame.Width, x, y, fullDataB);
        //
        //             // Rgb555Frame refFrame = _lastFrame;
        //
        //             // int         bestScore;
        //             // Rgb555Frame bestFrame = refFrame;
        //             // var vec = MotionEstimationUtil.FindMotionVector(fullDataR, fullDataG, fullDataB, 
        //             //     refFrame, 8, 8,
        //             //     new (x << 1, y << 1),
        //             //     new (lastVec.X + (x << 1), lastVec.Y + (y << 1)), 7, out bestScore);
        //             MotionVector vec;
        //             MotionVector vec2;
        //             int          bestScore;
        //             Rgb555Frame  bestFrame;
        //
        //             {
        //                 int backScore;
        //                 var vecBack = MotionEstimation.FindMotionVector(fullDataR, fullDataG, fullDataB,
        //                     backRefFrame,
        //                     new(x << 1, y << 1),
        //                     new(lastVec.X + (x << 1), lastVec.Y + (y << 1)), out backScore);
        //
        //                 // if (backScore < bestScore)
        //                 {
        //                     vec       = vecBack;
        //                     bestScore = backScore;
        //                     bestFrame = backRefFrame;
        //                 }
        //
        //                 int forwardScore;
        //                 var vecForward = MotionEstimation.FindMotionVector(fullDataR, fullDataG, fullDataB,
        //                     forwardRefFrame,
        //                     new(x << 1, y << 1),
        //                     new(lastVec.X + (x << 1), lastVec.Y + (y << 1)), out forwardScore);
        //
        //                 if (forwardScore < bestScore)
        //                 {
        //                     vec       = vecForward;
        //                     bestScore = forwardScore;
        //                     bestFrame = forwardRefFrame;
        //                 }
        //
        //                 var predBackR = new byte[64];
        //                 var predBackG = new byte[64];
        //                 var predBackB = new byte[64];
        //
        //                 var predForwardR = new byte[64];
        //                 var predForwardG = new byte[64];
        //                 var predForwardB = new byte[64];
        //
        //                 FrameUtil.GetTileHalf8(backRefFrame.R, backRefFrame.Width, backRefFrame.Height, vecBack.X,
        //                     vecBack.Y, predBackR);
        //                 FrameUtil.GetTileHalf8(backRefFrame.G, backRefFrame.Width, backRefFrame.Height, vecBack.X,
        //                     vecBack.Y, predBackG);
        //                 FrameUtil.GetTileHalf8(backRefFrame.B, backRefFrame.Width, backRefFrame.Height, vecBack.X,
        //                     vecBack.Y, predBackB);
        //
        //                 FrameUtil.GetTileHalf8(forwardRefFrame.R, forwardRefFrame.Width, forwardRefFrame.Height,
        //                     vecForward.X, vecForward.Y, predForwardR);
        //                 FrameUtil.GetTileHalf8(forwardRefFrame.G, forwardRefFrame.Width, forwardRefFrame.Height,
        //                     vecForward.X, vecForward.Y, predForwardG);
        //                 FrameUtil.GetTileHalf8(forwardRefFrame.B, forwardRefFrame.Width, forwardRefFrame.Height,
        //                     vecForward.X, vecForward.Y, predForwardB);
        //
        //                 GxAverage(predBackR, predForwardR, predR);
        //                 GxAverage(predBackG, predForwardG, predG);
        //                 GxAverage(predBackB, predForwardB, predB);
        //
        //                 int mixScore = FrameUtil.Sad64(fullDataR, predR);
        //                 mixScore += FrameUtil.Sad64(fullDataG, predG);
        //                 mixScore += FrameUtil.Sad64(fullDataB, predB);
        //
        //                 if (mixScore < bestScore)
        //                 {
        //                     vec       = vecBack;
        //                     vec2      = vecForward;
        //                     bestFrame = null;
        //                 }
        //             }
        //
        //             if (bestFrame != null)
        //             {
        //                 FrameUtil.GetTileHalf8(bestFrame.R, bestFrame.Width, bestFrame.Height, vec.X, vec.Y, predR);
        //                 FrameUtil.GetTileHalf8(bestFrame.G, bestFrame.Width, bestFrame.Height, vec.X, vec.Y, predG);
        //                 FrameUtil.GetTileHalf8(bestFrame.B, bestFrame.Width, bestFrame.Height, vec.X, vec.Y, predB);
        //             }
        //
        //             vec.X -= x << 1;
        //             vec.Y -= y << 1;
        //
        //             int iBitCount = 1;
        //             var iTmpG     = new byte[64];
        //             var iTmpR     = new byte[64];
        //             var iTmpB     = new byte[64];
        //             {
        //                 int i = 0;
        //                 for (int y3 = 0; y3 < 8; y3 += 4)
        //                 {
        //                     for (int x3 = 0; x3 < 8; x3 += 4)
        //                     {
        //                         for (int y2 = 0; y2 < 2; y2++)
        //                         {
        //                             for (int x2 = 0; x2 < 2; x2++)
        //                             {
        //                                 FrameUtil.GetTile2x2Step2(frame.G, frame.Width, x + x2 + x3, y + y2 + y3, data);
        //                                 if ((x3 + x2) == 0 && (y3 + y2) == 0)
        //                                     Array.Clear(dataG, 0, 4);
        //                                 else if (x2 == 0 && y2 != 0)
        //                                     FrameUtil.GetTile2x2Step2(iTmpG, 8, x3, y2 + y3 - 1, dataG);
        //                                 else if (x2 == 0 && x3 != 0)
        //                                     FrameUtil.GetTile2x2Step2(iTmpG, 8, x2 + x3 - 4, y2 + y3, dataG);
        //                                 else if (x2 == 0 && y2 == 0 && y3 != 0)
        //                                     FrameUtil.GetTile2x2Step2(iTmpG, 8, x2 + x3, y2 + y3 - 4, dataG);
        //                                 else
        //                                     FrameUtil.GetTile2x2Step2(iTmpG, 8, x2 + x3 - 1, y2 + y3, dataG);
        //
        //                                 for (int j = 0; j < 4; j++)
        //                                     intData[j] = data[j] - dataG[j];
        //                                 Dct.Dct4NoDiv(intData, dct);
        //                                 Quantize4(dct, iBlockConfig.QuantsG[i], iBlockConfig.DeQuantsG[i]);
        //                                 Dct.IDct4(iBlockConfig.DeQuantsG[i], dataG, undct);
        //                                 FrameUtil.SetTile2x2Step2(iTmpG, 8, x2 + x3, y2 + y3, undct);
        //                                 i++;
        //                             }
        //                         }
        //                     }
        //                 }
        //
        //                 for (int j = 0; j < 4; j++)
        //                 {
        //                     for (i = 0; i < 16; i++)
        //                     {
        //                         iBlockConfig.CombDctG[j * 16 + i] = iBlockConfig.QuantsG[i][j];
        //                     }
        //                 }
        //
        //                 // iBlockConfig.CombDctG[0] -= 512 * QTab4[0] >> 18;//lastGDC;
        //                 // lastGDC = quantsG[0][0];
        //
        //                 iBitCount += Vlc.CalcDctBitCount(iBlockConfig.CombDctG);
        //                 // EncodeDCT(combDct2, 1, bw);
        //
        //                 i = 0;
        //                 for (int y3 = 0; y3 < 8; y3 += 4)
        //                 {
        //                     for (int x3 = 0; x3 < 8; x3 += 4)
        //                     {
        //                         for (int y2 = 0; y2 < 2; y2++)
        //                         {
        //                             for (int x2 = 0; x2 < 2; x2++)
        //                             {
        //                                 FrameUtil.GetTile2x2Step2(frame.R, frame.Width, x + x2 + x3, y + y2 + y3, data);
        //                                 if ((x3 + x2) == 0 && (y3 + y2) == 0)
        //                                     FrameUtil.GetTile2x2Step2(iTmpG, 8, x2 + x3, y2 + y3, dataG);
        //                                 else if (x2 == 0 && y2 != 0)
        //                                     FrameUtil.GetTile2x2Step2(iTmpR, 8, x3, y2 + y3 - 1, dataG);
        //                                 else if (x2 == 0 && x3 != 0)
        //                                     FrameUtil.GetTile2x2Step2(iTmpR, 8, x2 + x3 - 4, y2 + y3, dataG);
        //                                 else if (x2 == 0 && y2 == 0 && y3 != 0)
        //                                     FrameUtil.GetTile2x2Step2(iTmpR, 8, x2 + x3, y2 + y3 - 4, dataG);
        //                                 else
        //                                     FrameUtil.GetTile2x2Step2(iTmpR, 8, x2 + x3 - 1, y2 + y3, dataG);
        //                                 for (int j = 0; j < 4; j++)
        //                                     intData[j] = data[j] - dataG[j];
        //                                 Dct.Dct4NoDiv(intData, dct);
        //                                 Quantize4(dct, iBlockConfig.QuantsR[i], iBlockConfig.DeQuantsR[i]);
        //                                 Dct.IDct4(iBlockConfig.DeQuantsR[i], dataG, undct);
        //                                 FrameUtil.SetTile2x2Step2(iTmpR, 8, x2 + x3, y2 + y3, undct);
        //                                 i++;
        //                             }
        //                         }
        //                     }
        //                 }
        //
        //                 for (int j = 0; j < 4; j++)
        //                 {
        //                     for (i = 0; i < 16; i++)
        //                     {
        //                         iBlockConfig.CombDctR[j * 16 + i] = iBlockConfig.QuantsR[i][j];
        //                     }
        //                 }
        //
        //                 iBitCount += Vlc.CalcDctBitCount(iBlockConfig.CombDctR);
        //                 // EncodeDCT(combDct2, 1, bw);
        //
        //                 i = 0;
        //                 for (int y3 = 0; y3 < 8; y3 += 4)
        //                 {
        //                     for (int x3 = 0; x3 < 8; x3 += 4)
        //                     {
        //                         for (int y2 = 0; y2 < 2; y2++)
        //                         {
        //                             for (int x2 = 0; x2 < 2; x2++)
        //                             {
        //                                 FrameUtil.GetTile2x2Step2(frame.B, frame.Width, x + x2 + x3, y + y2 + y3, data);
        //                                 if ((x3 + x2) == 0 && (y3 + y2) == 0)
        //                                     FrameUtil.GetTile2x2Step2(iTmpG, 8, x2 + x3, y2 + y3, dataG);
        //                                 else if (x2 == 0 && y2 != 0)
        //                                     FrameUtil.GetTile2x2Step2(iTmpB, 8, x3, y2 + y3 - 1, dataG);
        //                                 else if (x2 == 0 && x3 != 0)
        //                                     FrameUtil.GetTile2x2Step2(iTmpB, 8, x2 + x3 - 4, y2 + y3, dataG);
        //                                 else if (x2 == 0 && y2 == 0 && y3 != 0)
        //                                     FrameUtil.GetTile2x2Step2(iTmpB, 8, x2 + x3, y2 + y3 - 4, dataG);
        //                                 else
        //                                     FrameUtil.GetTile2x2Step2(iTmpB, 8, x2 + x3 - 1, y2 + y3, dataG);
        //                                 for (int j = 0; j < 4; j++)
        //                                     intData[j] = data[j] - dataG[j];
        //                                 Dct.Dct4NoDiv(intData, dct);
        //                                 Quantize4(dct, iBlockConfig.QuantsB[i], iBlockConfig.DeQuantsB[i]);
        //                                 Dct.IDct4(iBlockConfig.DeQuantsB[i], dataG, undct);
        //                                 FrameUtil.SetTile2x2Step2(iTmpB, 8, x2 + x3, y2 + y3, undct);
        //                                 i++;
        //                             }
        //                         }
        //                     }
        //                 }
        //
        //                 for (int j = 0; j < 4; j++)
        //                 {
        //                     for (i = 0; i < 16; i++)
        //                     {
        //                         iBlockConfig.CombDctB[j * 16 + i] = iBlockConfig.QuantsB[i][j];
        //                     }
        //                 }
        //
        //                 iBitCount += Vlc.CalcDctBitCount(iBlockConfig.CombDctB);
        //                 // EncodeDCT(combDct2, 1, bw);
        //             }
        //
        //             int pBitCount = 0;
        //             var pTmpG     = new byte[64];
        //             var pTmpR     = new byte[64];
        //             var pTmpB     = new byte[64];
        //             {
        //                 pBitCount++;
        //                 if (vec != lastVec)
        //                 {
        //                     pBitCount += BitWriter.GetSignedVarIntBitCount(vec.X - lastVec.X);
        //                     pBitCount += BitWriter.GetSignedVarIntBitCount(vec.Y - lastVec.Y);
        //                 }
        //
        //                 int i = 0;
        //                 for (int y3 = 0; y3 < 8; y3 += 4)
        //                 {
        //                     for (int x3 = 0; x3 < 8; x3 += 4)
        //                     {
        //                         for (int y2 = 0; y2 < 2; y2++)
        //                         {
        //                             for (int x2 = 0; x2 < 2; x2++)
        //                             {
        //                                 FrameUtil.GetTile2x2Step2(frame.G, frame.Width, x + x2 + x3, y + y2 + y3, data);
        //                                 int r = 0;
        //                                 for (int ry = y3 + y2; ry < y3 + y2 + 4; ry += 2)
        //                                     for (int rx = x3 + x2; rx < x3 + x2 + 4; rx += 2)
        //                                         dataG[r++] = predG[ry * 8 + rx];
        //                                 for (int j = 0; j < 4; j++)
        //                                     intData[j] = data[j] - dataG[j];
        //                                 Dct.Dct4NoDiv(intData, pBlockConfig.DctsG[i]);
        //                                 i++;
        //                             }
        //                         }
        //                     }
        //                 }
        //
        //                 pBitCount += QuantizeComb4PRD(pBlockConfig.DctsG, pBlockConfig.CombDctG,
        //                     pBlockConfig.DeQuantsG);
        //                 i = 0;
        //                 for (int y3 = 0; y3 < 8; y3 += 4)
        //                 {
        //                     for (int x3 = 0; x3 < 8; x3 += 4)
        //                     {
        //                         for (int y2 = 0; y2 < 2; y2++)
        //                         {
        //                             for (int x2 = 0; x2 < 2; x2++)
        //                             {
        //                                 int r = 0;
        //                                 for (int ry = y3 + y2; ry < y3 + y2 + 4; ry += 2)
        //                                     for (int rx = x3 + x2; rx < x3 + x2 + 4; rx += 2)
        //                                         dataG[r++] = predG[ry * 8 + rx];
        //                                 Dct.IDct4(pBlockConfig.DeQuantsG[i], dataG, undct);
        //                                 FrameUtil.SetTile2x2Step2(pTmpG, 8, x2 + x3, y2 + y3, undct);
        //                                 i++;
        //                             }
        //                         }
        //                     }
        //                 }
        //
        //                 // for (int j = 0; j < 4; j++)
        //                 // {
        //                 //     for (i = 0; i < 16; i++)
        //                 //     {
        //                 //         pBlockConfig.CombDctG[j * 16 + i] = pBlockConfig.QuantsG[i][j];
        //                 //     }
        //                 // }
        //                 //
        //                 // pBitCount++;
        //                 // if (pBlockConfig.CombDctG.Any(a => a != 0))
        //                 //     pBitCount += CalcDCTBitCount(pBlockConfig.CombDctG, 1);
        //
        //                 // pBitCount += CalcBitsQuantsP2(pBlockConfig.QuantsG);
        //
        //                 //pBitCount += CalcBitsQuantsP(pBlockConfig.QuantsG);
        //
        //                 i = 0;
        //                 for (int y3 = 0; y3 < 8; y3 += 4)
        //                 {
        //                     for (int x3 = 0; x3 < 8; x3 += 4)
        //                     {
        //                         for (int y2 = 0; y2 < 2; y2++)
        //                         {
        //                             for (int x2 = 0; x2 < 2; x2++)
        //                             {
        //                                 FrameUtil.GetTile2x2Step2(frame.R, frame.Width, x + x2 + x3, y + y2 + y3, data);
        //                                 int r = 0;
        //                                 for (int ry = y3 + y2; ry < y3 + y2 + 4; ry += 2)
        //                                     for (int rx = x3 + x2; rx < x3 + x2 + 4; rx += 2)
        //                                         dataG[r++] = predR[ry * 8 + rx];
        //                                 for (int j = 0; j < 4; j++)
        //                                     intData[j] = data[j] - dataG[j];
        //                                 Dct.Dct4NoDiv(intData, pBlockConfig.DctsR[i]);
        //                                 // Quantize4P(pBlockConfig.DctsR[i], pBlockConfig.QuantsR[i], pBlockConfig.DeQuantsR[i]);
        //                                 // FrameUtil.SetTile(pTmpR, x2 + x3, y2 + y3, 2, 2, 2,
        //                                 //     DCTUtil.IDCT4(pBlockConfig.DeQuantsR[i], dataG));
        //                                 i++;
        //                             }
        //                         }
        //                     }
        //                 }
        //
        //                 pBitCount += QuantizeComb4PRD(pBlockConfig.DctsR, pBlockConfig.CombDctR,
        //                     pBlockConfig.DeQuantsR);
        //
        //                 i = 0;
        //                 for (int y3 = 0; y3 < 8; y3 += 4)
        //                 {
        //                     for (int x3 = 0; x3 < 8; x3 += 4)
        //                     {
        //                         for (int y2 = 0; y2 < 2; y2++)
        //                         {
        //                             for (int x2 = 0; x2 < 2; x2++)
        //                             {
        //                                 int r = 0;
        //                                 for (int ry = y3 + y2; ry < y3 + y2 + 4; ry += 2)
        //                                     for (int rx = x3 + x2; rx < x3 + x2 + 4; rx += 2)
        //                                         dataG[r++] = predR[ry * 8 + rx];
        //                                 Dct.IDct4(pBlockConfig.DeQuantsR[i], dataG, undct);
        //                                 FrameUtil.SetTile2x2Step2(pTmpR, 8, x2 + x3, y2 + y3, undct);
        //                                 i++;
        //                             }
        //                         }
        //                     }
        //                 }
        //
        //                 // for (int j = 0; j < 4; j++)
        //                 // {
        //                 //     for (i = 0; i < 16; i++)
        //                 //     {
        //                 //         pBlockConfig.CombDctR[j * 16 + i] = pBlockConfig.QuantsR[i][j];
        //                 //     }
        //                 // }
        //                 //
        //                 // pBitCount++;
        //                 // if (pBlockConfig.CombDctR.Any(a => a != 0))
        //                 //     pBitCount += CalcDCTBitCount(pBlockConfig.CombDctR, 1);
        //
        //                 // pBitCount += CalcBitsQuantsP2(pBlockConfig.QuantsR);
        //                 // pBitCount += CalcDCTBitCount(pBlockConfig.CombDctR, 1);//pBitCount += CalcBitsQuantsP(pBlockConfig.QuantsR);
        //
        //                 i = 0;
        //                 for (int y3 = 0; y3 < 8; y3 += 4)
        //                 {
        //                     for (int x3 = 0; x3 < 8; x3 += 4)
        //                     {
        //                         for (int y2 = 0; y2 < 2; y2++)
        //                         {
        //                             for (int x2 = 0; x2 < 2; x2++)
        //                             {
        //                                 FrameUtil.GetTile2x2Step2(frame.B, frame.Width, x + x2 + x3, y + y2 + y3, data);
        //                                 int r = 0;
        //                                 for (int ry = y3 + y2; ry < y3 + y2 + 4; ry += 2)
        //                                     for (int rx = x3 + x2; rx < x3 + x2 + 4; rx += 2)
        //                                         dataG[r++] = predB[ry * 8 + rx];
        //                                 for (int j = 0; j < 4; j++)
        //                                     intData[j] = data[j] - dataG[j];
        //                                 Dct.Dct4NoDiv(intData, pBlockConfig.DctsB[i]);
        //                                 // Quantize4P(dct, pBlockConfig.QuantsB[i], pBlockConfig.DeQuantsB[i]);
        //                                 // FrameUtil.SetTile(pTmpB, x2 + x3, y2 + y3, 2, 2, 2,
        //                                 //     DCTUtil.IDCT4(pBlockConfig.DeQuantsB[i], dataG));
        //                                 i++;
        //                             }
        //                         }
        //                     }
        //                 }
        //
        //                 pBitCount += QuantizeComb4PRD(pBlockConfig.DctsB, pBlockConfig.CombDctB,
        //                     pBlockConfig.DeQuantsB);
        //
        //                 i = 0;
        //                 for (int y3 = 0; y3 < 8; y3 += 4)
        //                 {
        //                     for (int x3 = 0; x3 < 8; x3 += 4)
        //                     {
        //                         for (int y2 = 0; y2 < 2; y2++)
        //                         {
        //                             for (int x2 = 0; x2 < 2; x2++)
        //                             {
        //                                 int r = 0;
        //                                 for (int ry = y3 + y2; ry < y3 + y2 + 4; ry += 2)
        //                                     for (int rx = x3 + x2; rx < x3 + x2 + 4; rx += 2)
        //                                         dataG[r++] = predB[ry * 8 + rx];
        //                                 Dct.IDct4(pBlockConfig.DeQuantsB[i], dataG, undct);
        //                                 FrameUtil.SetTile2x2Step2(pTmpB, 8, x2 + x3, y2 + y3, undct);
        //                                 i++;
        //                             }
        //                         }
        //                     }
        //                 }
        //
        //                 // for (int j = 0; j < 4; j++)
        //                 // {
        //                 //     for (i = 0; i < 16; i++)
        //                 //     {
        //                 //         pBlockConfig.CombDctB[j * 16 + i] = pBlockConfig.QuantsB[i][j];
        //                 //     }
        //                 // }
        //                 //
        //                 // pBitCount++;
        //                 // if (pBlockConfig.CombDctB.Any(a => a != 0))
        //                 //     pBitCount += CalcDCTBitCount(pBlockConfig.CombDctB, 1);
        //
        //                 // pBitCount += CalcBitsQuantsP2(pBlockConfig.QuantsB);
        //                 // pBitCount += CalcDCTBitCount(pBlockConfig.CombDctB, 1);//pBitCount += CalcBitsQuantsP(pBlockConfig.QuantsB);
        //             }
        //
        //             FrameUtil.GetTile8(frame.G, frame.Width, x, y, tmp);
        //             int diffI = FrameUtil.Sad64(iTmpG, tmp);
        //             int diffP = FrameUtil.Sad64(pTmpG, tmp);
        //             FrameUtil.GetTile8(frame.R, frame.Width, x, y, tmp);
        //             diffI += FrameUtil.Sad64(iTmpR, tmp);
        //             diffP += FrameUtil.Sad64(pTmpR, tmp);
        //             FrameUtil.GetTile8(frame.B, frame.Width, x, y, tmp);
        //             diffI += FrameUtil.Sad64(iTmpB, tmp);
        //             diffP += FrameUtil.Sad64(pTmpB, tmp);
        //
        //             if (iBitCount + diffI * _lambda < pBitCount + diffP * _lambda)
        //             {
        //                 // dctCoefCount += iBlockConfig.CombDctG.Count(a => a != 0);
        //                 // dctCoefCount += iBlockConfig.CombDctR.Count(a => a != 0);
        //                 // dctCoefCount += iBlockConfig.CombDctB.Count(a => a != 0);
        //                 dctBw.WriteBits(1, 1); //I-block
        //                 vectorBw.WriteBits(1, 1);
        //                 Vlc.EncodeDct(iBlockConfig.CombDctG, dctBw);
        //                 Vlc.EncodeDct(iBlockConfig.CombDctR, dctBw);
        //                 Vlc.EncodeDct(iBlockConfig.CombDctB, dctBw);
        //
        //                 FrameUtil.SetTile8(decFrame.R, decFrame.Width, x, y, iTmpR);
        //                 FrameUtil.SetTile8(decFrame.G, decFrame.Width, x, y, iTmpG);
        //                 FrameUtil.SetTile8(decFrame.B, decFrame.Width, x, y, iTmpB);
        //
        //                 vecBuf[curVecBuf][x >> 3] = lastVec;
        //
        //                 iBlockCount++;
        //             }
        //             else
        //             {
        //                 dctBw.WriteBits(0, 1); //P-block
        //                 if (vec == lastVec)
        //                 {
        //                     vectorBw.WriteBits(1, 1);
        //                 }
        //                 else
        //                 {
        //                     vectorBw.WriteBits(0, 1);
        //                     vectorBw.WriteSignedVarInt(vec.X - lastVec.X);
        //                     vectorBw.WriteSignedVarInt(vec.Y - lastVec.Y);
        //                 }
        //
        //                 // EncodeQuantsP(bw, pBlockConfig.QuantsG);
        //                 // EncodeQuantsP(bw, pBlockConfig.QuantsR);
        //                 // EncodeQuantsP(bw, pBlockConfig.QuantsB);
        //                 // EncodeQuantsP2(bw, pBlockConfig.QuantsG);
        //                 // EncodeQuantsP2(bw, pBlockConfig.QuantsR);
        //                 // EncodeQuantsP2(bw, pBlockConfig.QuantsB);
        //
        //                 if (!AllZero(pBlockConfig.CombDctG))
        //                 {
        //                     dctBw.WriteBits(1, 1);
        //                     Vlc.EncodeDct(pBlockConfig.CombDctG, dctBw);
        //                 }
        //                 else
        //                     dctBw.WriteBits(0, 1);
        //
        //                 if (!AllZero(pBlockConfig.CombDctR))
        //                 {
        //                     dctBw.WriteBits(1, 1);
        //                     Vlc.EncodeDct(pBlockConfig.CombDctR, dctBw);
        //                 }
        //                 else
        //                     dctBw.WriteBits(0, 1);
        //
        //                 if (!AllZero(pBlockConfig.CombDctB))
        //                 {
        //                     dctBw.WriteBits(1, 1);
        //                     Vlc.EncodeDct(pBlockConfig.CombDctB, dctBw);
        //                 }
        //                 else
        //                     dctBw.WriteBits(0, 1);
        //
        //                 FrameUtil.SetTile8(decFrame.R, decFrame.Width, x, y, pTmpR);
        //                 FrameUtil.SetTile8(decFrame.G, decFrame.Width, x, y, pTmpG);
        //                 FrameUtil.SetTile8(decFrame.B, decFrame.Width, x, y, pTmpB);
        //
        //                 vecBuf[curVecBuf][x >> 3] = vec;
        //                 // dctCoefCount += pBlockConfig.CombDctG.Count(a => a != 0);
        //                 // dctCoefCount += pBlockConfig.CombDctR.Count(a => a != 0);
        //                 // dctCoefCount += pBlockConfig.CombDctB.Count(a => a != 0);
        //             }
        //         }
        //
        //         curVecBuf = 1 - curVecBuf;
        //     }
        //
        //     // Console.WriteLine(dctCoefCount);
        //
        //     var vecData = vectorBw.ToArray();
        //     var dctData = dctBw.ToArray();
        //
        //     var finalData = new byte[vecData.Length + dctData.Length];
        //     vecData.CopyTo(finalData, 0);
        //     dctData.CopyTo(finalData, vecData.Length);
        //
        //     return (finalData, decRefFrame, iBlockCount);
        // }
    }
}