using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Gericom.FastVideoDS.Utils
{
    public static class FrameUtil
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void GetTile8(byte[] data, int stride, int srcX, int srcY, byte[] result)
        {
            fixed (byte* dst = &result[0], src = &data[srcY * stride + srcX])
            {
                ((ulong*)dst)[0] = *(ulong*)(src);
                ((ulong*)dst)[1] = *(ulong*)(src + 1 * stride);
                ((ulong*)dst)[2] = *(ulong*)(src + 2 * stride);
                ((ulong*)dst)[3] = *(ulong*)(src + 3 * stride);
                ((ulong*)dst)[4] = *(ulong*)(src + 4 * stride);
                ((ulong*)dst)[5] = *(ulong*)(src + 5 * stride);
                ((ulong*)dst)[6] = *(ulong*)(src + 6 * stride);
                ((ulong*)dst)[7] = *(ulong*)(src + 7 * stride);
            }
        }

        public static byte[] GetTile(byte[] data, int stride, int srcX, int srcY, int width, int height)
        {
            var result = new byte[height * width];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    result[y * width + x] = data[(y + srcY) * stride + (x + srcX)];
                }
            }

            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void GetTile2x2Step2(byte[] data, int stride, int srcX, int srcY, byte[] dst)
        {
            dst[0] = data[(srcY) * stride + srcX];
            dst[1] = data[(srcY) * stride + srcX + 2];
            dst[2] = data[(srcY + 2) * stride + srcX];
            dst[3] = data[(srcY + 2) * stride + srcX + 2];
        }

        public static byte[] GetTile(byte[] data, int stride, int srcX, int srcY, int width, int height, int step)
        {
            var result = new byte[height * width];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    result[y * width + x] = data[(y * step + srcY) * stride + x * step + srcX];
                }
            }

            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static unsafe void GetTileHalf8(byte[] data, int width, int height, int srcX, int srcY, byte[] result)
        {
            if (((srcX | srcY) & 1) == 0)
            {
                if (srcX >> 1 >= 0 && (srcX >> 1) + 7 < width &&
                    srcY >> 1 >= 0 && (srcY >> 1) + 7 < height)
                {
                    fixed (byte* dst = &result[0], src = &data[(srcY >> 1) * width + (srcX >> 1)])
                    {
                        ((ulong*)dst)[0] = *(ulong*)(src);
                        ((ulong*)dst)[1] = *(ulong*)(src + 1 * width);
                        ((ulong*)dst)[2] = *(ulong*)(src + 2 * width);
                        ((ulong*)dst)[3] = *(ulong*)(src + 3 * width);
                        ((ulong*)dst)[4] = *(ulong*)(src + 4 * width);
                        ((ulong*)dst)[5] = *(ulong*)(src + 5 * width);
                        ((ulong*)dst)[6] = *(ulong*)(src + 6 * width);
                        ((ulong*)dst)[7] = *(ulong*)(src + 7 * width);
                    }
                }
                else
                {
                    for (int y = 0; y < 8; y++)
                    {
                        int y1 = Math.Clamp(y + (srcY >> 1), 0, height - 1);
                        for (int x = 0; x < 8; x++)
                        {
                            int x1 = Math.Clamp(x + (srcX >> 1), 0, width - 1);
                            result[y * 8 + x] = data[y1 * width + x1];
                        }
                    }
                }
            }
            else if ((srcY & 1) == 0)
            {
                if (srcX >> 1 >= 0 && (srcX >> 1) + 8 < width &&
                    srcY >> 1 >= 0 && (srcY >> 1) + 7 < height)
                {
                    fixed (byte* dst = &result[0], src = &data[(srcY >> 1) * width + (srcX >> 1)])
                    {
                        var bit0 = Vector256.Create((short)(1 << 2));
                        for (int y = 0; y < 8; y++)
                        {
                            ulong row  = *(ulong*)(src + y * width);
                            ulong row2 = (row >> 8) | ((ulong)src[y * width + 8] << 56);

                            var a      = Avx2.ConvertToVector256Int16(Vector128.Create(row, row2).AsByte());
                            var isZero = Avx2.CompareEqual(a, Vector256<short>.Zero);
                            a = Avx2.Add(a, bit0);
                            a = Avx2.AndNot(isZero, a);
                            var b = Sse2.Add(a.GetLower(), a.GetUpper());
                            b = Sse2.ShiftRightLogical(b, 4);
                            b = Sse2.ShiftLeftLogical(b, 3);
                            *(ulong*)(dst + y * 8) = Sse2.PackUnsignedSaturate(b, Vector128<short>.Zero).AsUInt64()
                                .ToScalar();
                        }
                    }

                    // for (int y = 0; y < 8; y++)
                    // {
                    //     for (int x = 0; x < 8; x++)
                    //     {
                    //         int a = data[(y + (srcY >> 1)) * width + x + (srcX >> 1)] >> 3 << 1;
                    //         if (a != 0)
                    //             a++;
                    //         int b = data[(y + (srcY >> 1)) * width + x + (srcX >> 1) + 1] >> 3 << 1;
                    //         if (b != 0)
                    //             b++;
                    //         result[y * 8 + x] = (byte) ((a * 16 + b * 16) >> 6 << 3);
                    //     }
                    // }
                }
                else
                {
                    for (int y = 0; y < 8; y++)
                    {
                        int y1 = Math.Clamp(y + (srcY >> 1), 0, height - 1);
                        for (int x = 0; x < 8; x++)
                        {
                            int x1 = Math.Clamp(x + (srcX >> 1), 0, width - 1);
                            int x2 = Math.Clamp(x + (srcX >> 1) + 1, 0, width - 1);
                            int a  = data[y1 * width + x1] >> 3 << 1;
                            if (a != 0)
                                a++;
                            int b = data[y1 * width + x2] >> 3 << 1;
                            if (b != 0)
                                b++;
                            result[y * 8 + x] = (byte)((a * 16 + b * 16) >> 6 << 3);
                        }
                    }
                }
            }
            else if ((srcX & 1) == 0)
            {
                if (srcX >> 1 >= 0 && (srcX >> 1) + 7 < width &&
                    srcY >> 1 >= 0 && (srcY >> 1) + 8 < height)
                {
                    fixed (byte* dst = &result[0], src = &data[(srcY >> 1) * width + (srcX >> 1)])
                    {
                        var bit0 = Vector256.Create((short)(1 << 2));
                        var ac = Avx2.ConvertToVector256Int16(
                            Vector128.Create(*(ulong*)(src + 0 * width), *(ulong*)(src + 2 * width)).AsByte());
                        var isZero = Avx2.CompareEqual(ac, Vector256<short>.Zero);
                        ac = Avx2.Add(ac, bit0);
                        ac = Avx2.AndNot(isZero, ac);

                        var bd = Avx2.ConvertToVector256Int16(
                            Vector128.Create(*(ulong*)(src + 1 * width), *(ulong*)(src + 3 * width)).AsByte());
                        isZero = Avx2.CompareEqual(bd, Vector256<short>.Zero);
                        bd     = Avx2.Add(bd, bit0);
                        bd     = Avx2.AndNot(isZero, bd);


                        var aBcD = Avx2.Add(ac, bd);
                        aBcD = Avx2.ShiftRightLogical(aBcD, 4);
                        aBcD = Avx2.ShiftLeftLogical(aBcD, 3);


                        var eg = Avx2.ConvertToVector256Int16(
                            Vector128.Create(*(ulong*)(src + 4 * width), *(ulong*)(src + 6 * width)).AsByte());
                        isZero = Avx2.CompareEqual(eg, Vector256<short>.Zero);
                        eg     = Avx2.Add(eg, bit0);
                        eg     = Avx2.AndNot(isZero, eg);

                        var ce   = Vector256.Create(ac.GetUpper(), eg.GetLower());
                        var bCdE = Avx2.Add(bd, ce);
                        bCdE = Avx2.ShiftRightLogical(bCdE, 4);
                        bCdE = Avx2.ShiftLeftLogical(bCdE, 3);
                        Avx.Store(dst, Avx2.PackUnsignedSaturate(aBcD, bCdE));

                        var fh = Avx2.ConvertToVector256Int16(
                            Vector128.Create(*(ulong*)(src + 5 * width), *(ulong*)(src + 7 * width)).AsByte());
                        isZero = Avx2.CompareEqual(fh, Vector256<short>.Zero);
                        fh     = Avx2.Add(fh, bit0);
                        fh     = Avx2.AndNot(isZero, fh);

                        var eFgH = Avx2.Add(eg, fh);
                        eFgH = Avx2.ShiftRightLogical(eFgH, 4);
                        eFgH = Avx2.ShiftLeftLogical(eFgH, 3);

                        var last       = Sse41.ConvertToVector128Int16(src + 8 * width);
                        var isZeroLast = Sse2.CompareEqual(last, Vector128<short>.Zero);
                        last = Sse2.Add(last, Vector128.Create((short)(1 << 2)));
                        last = Sse2.AndNot(isZeroLast, last);

                        var gi   = Vector256.Create(eg.GetUpper(), last);
                        var fGhI = Avx2.Add(fh, gi);
                        fGhI = Avx2.ShiftRightLogical(fGhI, 4);
                        fGhI = Avx2.ShiftLeftLogical(fGhI, 3);
                        Avx.Store(dst + 4 * 8, Avx2.PackUnsignedSaturate(eFgH, fGhI));
                    }

                    // for (int y = 0; y < 8; y++)
                    // {
                    //     for (int x = 0; x < 8; x++)
                    //     {
                    //         int a = data[(y + (srcY >> 1)) * width + x + (srcX >> 1)] >> 3 << 1;
                    //         if (a != 0)
                    //             a++;
                    //         int b = data[(y + (srcY >> 1) + 1) * width + x + (srcX >> 1)] >> 3 << 1;
                    //         if (b != 0)
                    //             b++;
                    //         if(result[y * 8 + x] != (byte)((a * 16 + b * 16) >> 6 << 3))
                    //         {
                    //
                    //         }
                    //         result[y * 8 + x] = (byte) ((a * 16 + b * 16) >> 6 << 3);
                    //     }
                    // }
                }
                else
                {
                    for (int y = 0; y < 8; y++)
                    {
                        int y1 = Math.Clamp(y + (srcY >> 1), 0, height - 1);
                        int y2 = Math.Clamp(y + (srcY >> 1) + 1, 0, height - 1);
                        for (int x = 0; x < 8; x++)
                        {
                            int x1 = Math.Clamp(x + (srcX >> 1), 0, width - 1);
                            int a  = data[y1 * width + x1] >> 3 << 1;
                            if (a != 0)
                                a++;
                            int b = data[y2 * width + x1] >> 3 << 1;
                            if (b != 0)
                                b++;
                            result[y * 8 + x] = (byte)((a * 16 + b * 16) >> 6 << 3);
                        }
                    }
                }
            }
            else
            {
                if (srcX >> 1 >= 0 && (srcX >> 1) + 8 < width &&
                    srcY >> 1 >= 0 && (srcY >> 1) + 8 < height)
                {
                    fixed (byte* dst = &result[0], src = &data[(srcY >> 1) * width + (srcX >> 1)])
                    {
                        var bit0 = Vector256.Create((short)(1 << 2));
                        for (int y = 0; y < 8; y++)
                        {
                            var a = Avx2.ConvertToVector256Int16(
                                Vector128.Create(*(ulong*)(src + y * width), *(ulong*)(src + (y + 1) * width + 1))
                                    .AsByte());
                            var isZero = Avx2.CompareEqual(a, Vector256<short>.Zero);
                            a = Avx2.Add(a, bit0);
                            a = Avx2.AndNot(isZero, a);
                            var b = Sse2.Add(a.GetLower(), a.GetUpper());
                            b = Sse2.ShiftRightLogical(b, 4);
                            b = Sse2.ShiftLeftLogical(b, 3);
                            *(ulong*)(dst + y * 8) = Sse2.PackUnsignedSaturate(b, Vector128<short>.Zero).AsUInt64()
                                .ToScalar();
                        }
                    }

                    // for (int y = 0; y < 8; y++)
                    // {
                    //     for (int x = 0; x < 8; x++)
                    //     {
                    //         int a = data[(y + (srcY >> 1)) * width + x + (srcX >> 1)] >> 3 << 1;
                    //         if (a != 0)
                    //             a++;
                    //         int b = data[(y + (srcY >> 1) + 1) * width + x + (srcX >> 1) + 1] >> 3 << 1;
                    //         if (b != 0)
                    //             b++;
                    //         result[y * 8 + x] = (byte) ((a * 16 + b * 16) >> 6 << 3);
                    //     }
                    // }
                }
                else
                {
                    for (int y = 0; y < 8; y++)
                    {
                        int y1 = Math.Clamp(y + (srcY >> 1), 0, height - 1);
                        int y2 = Math.Clamp(y + (srcY >> 1) + 1, 0, height - 1);
                        for (int x = 0; x < 8; x++)
                        {
                            int x1 = Math.Clamp(x + (srcX >> 1), 0, width - 1);
                            int x2 = Math.Clamp(x + (srcX >> 1) + 1, 0, width - 1);
                            int a  = data[y1 * width + x1] >> 3 << 1;
                            if (a != 0)
                                a++;
                            int b = data[y2 * width + x2] >> 3 << 1;
                            if (b != 0)
                                b++;
                            result[y * 8 + x] = (byte)((a * 16 + b * 16) >> 6 << 3);
                        }
                    }
                }
            }
        }

        // public static unsafe byte[] GetTileHalf(byte[,] data, int srcX, int srcY, int width, int height)
        // {
        //     var result = new byte[height * width];
        //     if (((srcX | srcY) & 1) == 0)
        //     {
        //         if (srcX >> 1 >= 0 && (srcX >> 1) + width - 1 < data.GetLength(1) &&
        //             srcY >> 1 >= 0 && (srcY >> 1) + height - 1 < data.GetLength(0))
        //         {
        //             fixed (byte* dst = &result[0])
        //             {
        //                 for (int y = 0; y < height; y++)
        //                 {
        //                     fixed (byte* src = &data[(srcY >> 1) + y, srcX >> 1])
        //                     {
        //                         Buffer.MemoryCopy(src, dst + y * width, width, width);
        //                     }
        //                 }
        //             }
        //         }
        //         else
        //         {
        //             for (int y = 0; y < height; y++)
        //             {
        //                 for (int x = 0; x < width; x++)
        //                 {
        //                     int x1 = MathUtil.Clamp(x + (srcX >> 1), 0, data.GetLength(1) - 1);
        //                     int y1 = MathUtil.Clamp(y + (srcY >> 1), 0, data.GetLength(0) - 1);
        //                     result[y * width + x] = data[y1, x1];
        //                 }
        //             }
        //         }
        //     }
        //     else if ((srcY & 1) == 0)
        //     {
        //         for (int y = 0; y < height; y++)
        //         {
        //             for (int x = 0; x < width; x++)
        //             {
        //                 int x1 = MathUtil.Clamp(x + (srcX >> 1), 0, data.GetLength(1) - 1);
        //                 int x2 = MathUtil.Clamp(x + (srcX >> 1) + 1, 0, data.GetLength(1) - 1);
        //                 int y1 = MathUtil.Clamp(y + (srcY >> 1), 0, data.GetLength(0) - 1);
        //                 int a  = data[y1, x1] >> 3 << 1;
        //                 if (a != 0)
        //                     a++;
        //                 int b = data[y1, x2] >> 3 << 1;
        //                 if (b != 0)
        //                     b++;
        //                 result[y * width + x] = (byte) ((a * 16 + b * 16) >> 6 << 3);
        //                 //(byte) (((data[y1, x1] + data[y1, x2] + 8) >> 1) & 0xF8);
        //             }
        //         }
        //     }
        //     else if ((srcX & 1) == 0)
        //     {
        //         for (int y = 0; y < height; y++)
        //         {
        //             for (int x = 0; x < width; x++)
        //             {
        //                 int x1 = MathUtil.Clamp(x + (srcX >> 1), 0, data.GetLength(1) - 1);
        //                 int y1 = MathUtil.Clamp(y + (srcY >> 1), 0, data.GetLength(0) - 1);
        //                 int y2 = MathUtil.Clamp(y + (srcY >> 1) + 1, 0, data.GetLength(0) - 1);
        //                 int a  = data[y1, x1] >> 3 << 1;
        //                 if (a != 0)
        //                     a++;
        //                 int b = data[y2, x1] >> 3 << 1;
        //                 if (b != 0)
        //                     b++;
        //                 result[y * width + x] = (byte) ((a * 16 + b * 16) >> 6 << 3);
        //                 // result[y * width + x] = (byte) (((data[y1, x1] + data[y2, x1] + 8) >> 1) & 0xF8);
        //             }
        //         }
        //     }
        //     else
        //     {
        //         for (int y = 0; y < height; y++)
        //         {
        //             for (int x = 0; x < width; x++)
        //             {
        //                 int x1 = MathUtil.Clamp(x + (srcX >> 1), 0, data.GetLength(1) - 1);
        //                 int x2 = MathUtil.Clamp(x + (srcX >> 1) + 1, 0, data.GetLength(1) - 1);
        //                 int y1 = MathUtil.Clamp(y + (srcY >> 1), 0, data.GetLength(0) - 1);
        //                 int y2 = MathUtil.Clamp(y + (srcY >> 1) + 1, 0, data.GetLength(0) - 1);
        //                 int a  = data[y1, x1] >> 3 << 1;
        //                 if (a != 0)
        //                     a++;
        //                 int b = data[y2, x2] >> 3 << 1;
        //                 if (b != 0)
        //                     b++;
        //                 result[y * width + x] = (byte) ((a * 16 + b * 16) >> 6 << 3);
        //                 // result[y * width + x] = (byte) (((data[y1, x1] + data[y2, x2] + 8) >> 1) & 0xF8);
        //             }
        //         }
        //     }
        //
        //     return result;
        // }

        public static void SetTile(byte[,] data, int dstX, int dstY, int width, int height, byte[] src)
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    data[y + dstY, x + dstX] = src[y * width + x];
        }

        public static void SetTile(byte[] data, int stride, int dstX, int dstY, int width, int height, int step,
            byte[] src)
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    data[(y * step + dstY) * stride + x * step + dstX] = src[y * width + x];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetTile2x2Step2(byte[] data, int stride, int dstX, int dstY, byte[] src)
        {
            data[dstY * stride + dstX]           = src[0];
            data[dstY * stride + dstX + 2]       = src[1];
            data[(dstY + 2) * stride + dstX]     = src[2];
            data[(dstY + 2) * stride + dstX + 2] = src[3];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void SetTile8(byte[] data, int stride, int dstX, int dstY, byte[] src)
        {
            fixed (byte* pSrc = &src[0], pDst = &data[dstY * stride + dstX])
            {
                *(ulong*)(pDst)              = ((ulong*)pSrc)[0];
                *(ulong*)(pDst + 1 * stride) = ((ulong*)pSrc)[1];
                *(ulong*)(pDst + 2 * stride) = ((ulong*)pSrc)[2];
                *(ulong*)(pDst + 3 * stride) = ((ulong*)pSrc)[3];
                *(ulong*)(pDst + 4 * stride) = ((ulong*)pSrc)[4];
                *(ulong*)(pDst + 5 * stride) = ((ulong*)pSrc)[5];
                *(ulong*)(pDst + 6 * stride) = ((ulong*)pSrc)[6];
                *(ulong*)(pDst + 7 * stride) = ((ulong*)pSrc)[7];
            }
        }

        // public static void SetTile(byte[] data, int stride, int dstX, int dstY, int width, int height, byte[,] src)
        // {
        //     for (int y = 0; y < height; y++)
        //         for (int x = 0; x < width; x++)
        //             data[(y + dstY) * stride + x + dstX] = src[y, x];
        // }

        public static void SetTile(byte[] data, int stride, int dstX, int dstY, int width, int height, int step,
            byte[,] src)
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    data[(y * step + dstY) * stride + x * step + dstX] = src[y, x];
        }

        public static unsafe byte[] GetBlockPixels16x16(byte[] Data, int X, int Y, int Stride, int Offset)
        {
            byte[] values = new byte[256];
            fixed (byte* pVals = &values[0])
            {
                ulong* pLVals = (ulong*)pVals;
                for (int y3 = 0; y3 < 16; y3++)
                {
                    fixed (byte* pData = &Data[(Y + y3) * Stride + X + Offset])
                    {
                        *pLVals++ = *((ulong*)pData);
                        *pLVals++ = *((ulong*)(pData + 8));
                    }
                }
            }

            return values;
        }

        public static unsafe byte[] GetBlockPixels8x8(byte[] Data, int X, int Y, int Stride, int Offset)
        {
            byte[] values = new byte[64];
            fixed (byte* pVals = &values[0], pData = &Data[Y * Stride + X + Offset])
            {
                ulong* pLVals = (ulong*)pVals;
                *pLVals++ = *((ulong*)pData);
                *pLVals++ = *((ulong*)(pData + Stride));
                *pLVals++ = *((ulong*)(pData + Stride * 2));
                *pLVals++ = *((ulong*)(pData + Stride * 3));
                *pLVals++ = *((ulong*)(pData + Stride * 4));
                *pLVals++ = *((ulong*)(pData + Stride * 5));
                *pLVals++ = *((ulong*)(pData + Stride * 6));
                *pLVals++ = *((ulong*)(pData + Stride * 7));
                /*ulong* pLVals = (ulong*)pVals;
                for (int y3 = 0; y3 < 8; y3++)
                {
                    fixed (byte* pData = &Data[(Y + y3) * Stride + X + Offset])
                    {
                        *pLVals++ = *((ulong*)pData);
                    }
                }*/
            }

            return values;
        }

        public static unsafe byte[] GetBlockPixels4x4(byte[] Data, int X, int Y, int Stride, int Offset)
        {
            byte[] values = new byte[16];
            fixed (byte* pVals = &values[0], pData = &Data[Y * Stride + X + Offset])
            {
                uint* pLVals = (uint*)pVals;
                *pLVals++ = *((uint*)pData);
                *pLVals++ = *((uint*)(pData + Stride));
                *pLVals++ = *((uint*)(pData + Stride * 2));
                *pLVals++ = *((uint*)(pData + Stride * 3));
            }

            return values;
        }

        public static unsafe void SetBlockPixels4x4(byte[] Data, int X, int Y, int Stride, int Offset, byte[] Values)
        {
            fixed (byte* pVals = &Values[0], pData = &Data[Y * Stride + X + Offset])
            {
                uint* pLVals = (uint*)pVals;
                *((uint*)pData)                = *pLVals++;
                *((uint*)(pData + Stride))     = *pLVals++;
                *((uint*)(pData + Stride * 2)) = *pLVals++;
                *((uint*)(pData + Stride * 3)) = *pLVals++;
            }
        }

        public static unsafe void SetBlockPixels8x8(byte[] Data, int X, int Y, int Stride, int Offset, byte[] Values)
        {
            fixed (byte* pVals = &Values[0], pData = &Data[Y * Stride + X + Offset])
            {
                ulong* pLVals = (ulong*)pVals;
                *((ulong*)pData)                = *pLVals++;
                *((ulong*)(pData + Stride))     = *pLVals++;
                *((ulong*)(pData + Stride * 2)) = *pLVals++;
                *((ulong*)(pData + Stride * 3)) = *pLVals++;
                *((ulong*)(pData + Stride * 4)) = *pLVals++;
                *((ulong*)(pData + Stride * 5)) = *pLVals++;
                *((ulong*)(pData + Stride * 6)) = *pLVals++;
                *((ulong*)(pData + Stride * 7)) = *pLVals++;
            }
        }


        public static unsafe int Sad64(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            fixed (byte* pA = a, pB = b)
            {
                var a0    = Avx.LoadVector256(pA);
                var b0    = Avx.LoadVector256(pB);
                var sad0  = Avx2.SumAbsoluteDifferences(a0, b0);
                var a1    = Avx.LoadVector256(pA + 32);
                var b1    = Avx.LoadVector256(pB + 32);
                var sad1  = Avx2.SumAbsoluteDifferences(a1, b1);
                var diff  = Avx2.Add(sad0.AsInt32(), sad1.AsInt32());
                var diff2 = Sse2.Add(diff.GetLower(), diff.GetUpper());
                return diff2.GetElement(0) + diff2.GetElement(2);
            }
        }

        public static unsafe ulong Sad(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            var   sad = Vector256<ulong>.Zero;
            ulong result;
            fixed (byte* pA0 = a, pB0 = b)
            {
                int i;
                for (i = 0; i + 31 < a.Length; i += 32)
                {
                    var a0 = Avx.LoadVector256(pA0 + i);
                    var b0 = Avx.LoadVector256(pB0 + i);
                    sad = Avx2.Add(sad, Avx2.SumAbsoluteDifferences(a0, b0).AsUInt64());
                }

                var result2 = Sse2.Add(sad.GetLower(), sad.GetUpper());
                result = result2.GetElement(0) + result2.GetElement(1);
                for (; i < a.Length; i++)
                    result += (ulong)Math.Abs(pA0[i] - pB0[i]);
            }

            return result;
        }
    }
}