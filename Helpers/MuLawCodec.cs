using System;
using System.Threading;

namespace AudioCompressor.Helpers
{
    /// <summary>
    /// ITU-T G.711 μ-law (mu-law) nonlinear quantization codec.
    ///
    /// Compression ratio: 2:1 vs 16-bit PCM (8 bits per sample vs 16).
    /// The logarithmic companding curve allocates more quantization levels
    /// to quiet sounds, matching human hearing sensitivity.
    ///
    /// Round-trip fidelity: lossless up to 7-bit magnitude resolution.
    /// </summary>
    public static class MuLawCodec
    {
        private const double Mu = 255.0;

        // ── Encode ────────────────────────────────────────────────────────────

        /// <summary>
        /// Encodes normalized float samples [-1, 1] to 8-bit μ-law bytes.
        /// Output length equals input length (1 byte per sample).
        /// </summary>
        public static byte[] Encode(float[] samples, CancellationToken token = default)
        {
            byte[] output = new byte[samples.Length];
            for (int i = 0; i < samples.Length; i++)
            {
                if (token.IsCancellationRequested)
                    throw new OperationCanceledException(token);
                output[i] = EncodeOneSample(samples[i]);
            }
            return output;
        }

        // ── Decode ────────────────────────────────────────────────────────────

        /// <summary>
        /// Decodes 8-bit μ-law bytes back to normalized float samples [-1, 1].
        /// </summary>
        public static float[] Decode(byte[] encoded)
        {
            float[] samples = new float[encoded.Length];
            for (int i = 0; i < encoded.Length; i++)
                samples[i] = DecodeOneSample(encoded[i]);
            return samples;
        }

        // ── Internal ──────────────────────────────────────────────────────────

        /// <summary>
        /// Encodes one float sample to a μ-law byte per the ITU-T G.711 spec:
        ///   1. Apply companding: y = ln(1 + μ|x|) / ln(1 + μ)
        ///   2. Pack as: sign(1 bit) | magnitude(7 bits)
        ///   3. Bit-invert the whole byte (G.711 line convention)
        /// </summary>
        private static byte EncodeOneSample(float sample)
        {
            double x    = Math.Max(-1.0, Math.Min(1.0, sample));
            int    sign = x >= 0 ? 0 : 1;          // 0 = positive, 1 = negative
            double absX = Math.Abs(x);

            // Companding curve
            double y = Math.Log(1.0 + Mu * absX) / Math.Log(1.0 + Mu);

            // 7-bit magnitude
            int mag = Math.Clamp((int)Math.Round(y * 127.0), 0, 127);

            // G.711: MSB = 1 for positive, then bit-invert the whole byte
            byte b = (byte)(((1 - sign) << 7) | mag);
            return (byte)(b ^ 0xFF);
        }

        /// <summary>
        /// Decodes one μ-law byte to a float sample:
        ///   1. Undo bit-inversion
        ///   2. Extract sign and 7-bit magnitude
        ///   3. Apply inverse companding: x = (1/μ) * ((1+μ)^y - 1)
        /// </summary>
        private static float DecodeOneSample(byte encoded)
        {
            byte b    = (byte)(encoded ^ 0xFF);     // undo bit inversion
            int  sign = (b >> 7) & 1;               // 1 = positive
            int  mag  = b & 0x7F;

            double y    = mag / 127.0;
            double absX = (1.0 / Mu) * (Math.Pow(1.0 + Mu, y) - 1.0);
            double x    = sign == 1 ? absX : -absX;

            return (float)Math.Max(-1.0, Math.Min(1.0, x));
        }
    }
}
