using System;
using System.Drawing;

namespace Gericom.FastVideoDS.Frames
{
    public class Rgb555Frame
    {
        public readonly int Width;
        public readonly int Height;

        public readonly byte[] R;
        public readonly byte[] G;
        public readonly byte[] B;

        public Rgb555Frame(int width, int height)
        {
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), width, "Width should be > 0");
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height), height, "Height should be > 0");
            Width = width;
            Height = height;
            R = new byte[height * width];
            G = new byte[height * width];
            B = new byte[height * width];
        }

        // public unsafe Bitmap ToBitmap()
        // {
        //     var b = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        //     var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.WriteOnly,
        //         PixelFormat.Format32bppArgb);
        //     for (int y = 0; y < b.Height; y++)
        //     {
        //         for (int x = 0; x < b.Width; x++)
        //         {
        //             // int pr = R[y, x] << 3; // * 255 / 31;
        //             // int pg = G[y, x] << 3; // * 255 / 31;
        //             // int pb = B[y, x] << 3; // * 255 / 31;
        //
        //             int pr = (R[y * Width + x] >> 3) * 255 / 31;
        //             int pg = (G[y * Width + x] >> 3) * 255 / 31;
        //             int pb = (B[y * Width + x] >> 3) * 255 / 31;
        //
        //             if (pr < 0) pr        = 0;
        //             else if (pr > 255) pr = 255;
        //             if (pg < 0) pg        = 0;
        //             else if (pg > 255) pg = 255;
        //             if (pb < 0) pb        = 0;
        //             else if (pb > 255) pb = 255;
        //
        //             *(int*)(((byte*)d.Scan0) + y * d.Stride + x * 4) = Color.FromArgb(pr, pg, pb).ToArgb();
        //         }
        //     }
        //
        //     b.UnlockBits(d);
        //     return b;
        // }

        private static readonly int[,] DitherMatrix =
        {
            { 0, 12, 3, 15 },
            { 8, 4, 11, 7 },
            { 2, 14, 1, 13 },
            { 10, 6, 9, 5 }
        };

        // public static unsafe Rgb555Frame FromBitmap(Bitmap b)
        // {
        //     var frame = new Rgb555Frame(b.Width, b.Height);
        //     var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly,
        //         PixelFormat.Format32bppArgb);
        //
        //     for (int y = 0; y < b.Height; y++)
        //     {
        //         for (int x = 0; x < b.Width; x++)
        //         {
        //             var c    = Color.FromArgb(*(int*)(((byte*)d.Scan0) + y * d.Stride + x * 4));
        //             int bias = (DitherMatrix[y & 3, x & 3] >> 1) - 4;
        //
        //             int pr = ((c.R + bias) >> 3); // * 255 / 31;
        //             int pg = ((c.G + bias) >> 3); // * 255 / 31;
        //             int pb = ((c.B + bias) >> 3); // * 255 / 31;
        //
        //             if (pr < 0) pr       = 0;
        //             else if (pr > 31) pr = 31;
        //             if (pg < 0) pg       = 0;
        //             else if (pg > 31) pg = 31;
        //             if (pb < 0) pb       = 0;
        //             else if (pb > 31) pb = 31;
        //
        //             frame.R[y * frame.Width + x] = (byte)(pr << 3); //((pr << 3) | (pr >> 2));
        //             frame.G[y * frame.Width + x] = (byte)(pg << 3); //((pg << 3) | (pg >> 2));
        //             frame.B[y * frame.Width + x] = (byte)(pb << 3); //((pb << 3) | (pb >> 2));
        //         }
        //     }
        //
        //     b.UnlockBits(d);
        //     return frame;
        // }

        //Based on http://www.thetenthplanet.de/archives/5367
        private static double ToLinear(double value) => Math.Pow(value, 2.2);

        private static readonly double[] ToLinear8 = new double[256];
        private static readonly double[] ToLinear5 = new double[32];

        static Rgb555Frame()
        {
            for (int i = 0; i < 256; i++)
                ToLinear8[i] = ToLinear(i / 255.0);

            for (int i = 0; i < 32; i++)
                ToLinear5[i] = ToLinear(i / 31.0);
        }

        private static int Dither(int color, double noise)
        {
            int c0 = color * 31 / 255;
            int c1 = Math.Clamp(c0 + 1, 0, 31);
            double discr = ToLinear5[c0] * (1 - noise) + ToLinear5[c1] * noise;
            return discr < ToLinear8[color] ? c1 : c0;
        }

        public static unsafe Rgb555Frame FromRgba32(byte* src, int width, int height, int stride)
        {
            var frame = new Rgb555Frame(width, height);
            frame.FromRgba32(src, stride);
            return frame;
        }

        public unsafe void FromRgba32(byte* src, int stride)
        {
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    var c = Color.FromArgb(*(int*)(src + y * stride + x * 4));

                    int bias = DitherMatrix[y & 3, x & 3];

                    int pr = Dither(c.R, bias / 16.0);
                    int pg = Dither(c.G, bias / 16.0);
                    int pb = Dither(c.B, bias / 16.0);

                    R[y * Width + x] = (byte)(pr << 3);
                    G[y * Width + x] = (byte)(pg << 3);
                    B[y * Width + x] = (byte)(pb << 3);
                }
            }
        }

        // public static unsafe RGB555Frame FromBitmap555(Bitmap b)
        // {
        //     var yuvFrame = new RGB555Frame(b.Width, b.Height);
        //     var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly,
        //         PixelFormat.Format32bppArgb);
        //     for (int y = 0; y < b.Height; y++)
        //     {
        //         for (int x = 0; x < b.Width; x++)
        //         {
        //             var c    = Color.FromArgb(*(int*)(((byte*)d.Scan0) + y * d.Stride + x * 4));
        //             int bias = (DitherMatrix[y & 3, x & 3] >> 1) - 4;
        //
        //             int pr = ((c.R + bias) >> 3);// * 255 / 31;
        //             int pg = ((c.G + bias) >> 3);// * 255 / 31;
        //             int pb = ((c.B + bias) >> 3);// * 255 / 31;
        //
        //             if (pr < 0) pr        = 0;
        //             else if (pr > 31) pr = 31;
        //             if (pg < 0) pg        = 0;
        //             else if (pg > 31) pg = 31;
        //             if (pb < 0) pb        = 0;
        //             else if (pb > 31) pb = 31;
        //
        //             yuvFrame.R[y, x] = (byte) pr;//((pr << 3) | (pr >> 2));
        //             yuvFrame.G[y, x] = (byte) pg;//((pg << 3) | (pg >> 2));
        //             yuvFrame.B[y, x] = (byte) pb;//((pb << 3) | (pb >> 2));
        //         }
        //     }
        //
        //     b.UnlockBits(d);
        //     return yuvFrame;
        // }
    }
}