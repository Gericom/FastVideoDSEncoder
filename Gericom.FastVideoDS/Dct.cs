using System;

namespace Gericom.FastVideoDS
{
    public static class Dct
    {
        public static void Dct4NoDiv(int[] pixels, int[] dst)
        {
            int t0 = pixels[0] + pixels[1];
            int t1 = pixels[0] - pixels[1];
            int t2 = pixels[2] + pixels[3];
            int t3 = pixels[2] - pixels[3];

            dst[0] = t0 + t2;
            dst[1] = t0 - t2;
            dst[2] = t1 + t3;
            dst[3] = t1 - t3;
        }

        public static void IDct4(int[] dct, byte[] pixels, byte[] dst)
        {
            int r0 = dct[0] + 16;
            int t0 = r0 + dct[1];
            int t2 = r0 - dct[1];
            int t1 = dct[2] + dct[3];
            int t3 = dct[2] - dct[3];

            dst[0] = (byte)(Math.Clamp((pixels[0] >> 3) + (t0 + t1 >> 5), 0, 31) << 3);
            dst[1] = (byte)(Math.Clamp((pixels[1] >> 3) + (t0 - t1 >> 5), 0, 31) << 3);
            dst[2] = (byte)(Math.Clamp((pixels[2] >> 3) + (t2 + t3 >> 5), 0, 31) << 3);
            dst[3] = (byte)(Math.Clamp((pixels[3] >> 3) + (t2 - t3 >> 5), 0, 31) << 3);
        }
    }
}