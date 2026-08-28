using System;

namespace CelesteMusicPlayer
{
    /// <summary>复数值（纯 C#，避免依赖 NAudio 已移除的 DSP FFT 类型）。</summary>
    internal struct CF
    {
        public double Re;
        public double Im;

        public CF(double re, double im)
        {
            Re = re;
            Im = im;
        }
    }

    /// <summary>radix-2 Cooley-Tukey FFT（纯 C#）。</summary>
    internal static class FftProcessor
    {
        /// <summary>就地 FFT。len 必须是 2 的幂。inverse=false 为正变换，true 为逆变换（需自乘 1/N）。</summary>
        public static void Transform(CF[] data, bool inverse)
        {
            int n = data.Length;
            if (n < 2 || (n & (n - 1)) != 0)
            {
                throw new ArgumentException("FFT length must be a power of two.");
            }

            // bit-reversal permutation
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1)
                {
                    j ^= bit;
                }

                j ^= bit;
                if (i < j)
                {
                    CF tmp = data[i];
                    data[i] = data[j];
                    data[j] = tmp;
                }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = (inverse ? 2.0 : -2.0) * Math.PI / len;
                double wRe = Math.Cos(ang);
                double wIm = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double curRe = 1.0, curIm = 0.0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k;
                        int b = i + k + len / 2;
                        double tRe = data[b].Re * curRe - data[b].Im * curIm;
                        double tIm = data[b].Re * curIm + data[b].Im * curRe;
                        data[b].Re = data[a].Re - tRe;
                        data[b].Im = data[a].Im - tIm;
                        data[a].Re += tRe;
                        data[a].Im += tIm;

                        double nRe = curRe * wRe - curIm * wIm;
                        double nIm = curRe * wIm + curIm * wRe;
                        curRe = nRe;
                        curIm = nIm;
                    }
                }
            }
        }
    }
}
