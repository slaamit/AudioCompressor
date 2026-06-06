using System;
using System.Threading;

namespace AudioCompressor.Helpers
{
    /// <summary>
    /// Differential PCM (DPCM) codec with real bit-packing.
    ///
    /// Instead of encoding absolute sample values, DPCM encodes the difference
    /// between each sample and the previous one.  Because adjacent audio samples
    /// are highly correlated, differences are small and need fewer bits.
    ///
    /// Bit-packing: values are stored at exactly `bits` bits per sample using
    /// <see cref="BitWriter"/>/<see cref="BitReader"/>, so the output is
    /// (samples × bits / 8) bytes — a real size reduction below 8 bits.
    ///
    /// Compression ratios vs 16-bit PCM source:
    ///   8-bit DPCM → 2:1   (same as μ-law, lower quality)
    ///   4-bit DPCM → 4:1   (noticeable quality loss on complex material)
    ///   2-bit DPCM → 8:1   (heavy distortion, voice-only use)
    /// </summary>
    public static class DpcmCodec
    {
        // ── Encode ────────────────────────────────────────────────────────────

        /// <summary>
        /// Encodes float samples [-1,1] to a bit-packed DPCM byte array.
        /// </summary>
        /// <param name="samples">Normalized float samples.</param>
        /// <param name="bits">Bits per residual value (1–8).</param>
        public static byte[] Encode(float[] samples, int bits, CancellationToken token = default)
        {
            ValidateBits(bits);
            int       maxVal   = (1 << bits) - 1;
            var       writer   = new BitWriter();
            float     prev     = 0f;

            for (int i = 0; i < samples.Length; i++)
            {
                if (token.IsCancellationRequested)
                    throw new OperationCanceledException(token);

                float diff = samples[i] - prev;

                // Map diff range [-2, +2] → [0, maxVal].
                // Actual diff range is [-1-(-1), 1-(-1)] = [-2, +2] in the worst case.
                int q = (int)Math.Round((diff + 2.0) / 4.0 * maxVal);
                q = Math.Clamp(q, 0, maxVal);
                writer.WriteBits(q, bits);

                // Reconstruct exactly as the decoder will, to keep encoder/decoder in sync.
                float dequant = (float)((double)q / maxVal * 4.0 - 2.0);
                prev = Math.Clamp(prev + dequant, -1f, 1f);
            }

            return writer.ToArray();
        }

        // ── Decode ────────────────────────────────────────────────────────────

        /// <summary>
        /// Decodes a bit-packed DPCM byte array back to float samples.
        /// </summary>
        /// <param name="encoded">Bit-packed payload (no header).</param>
        /// <param name="bits">Must match the value used during encoding.</param>
        /// <param name="sampleCount">Original sample count (stored in file header).</param>
        public static float[] Decode(byte[] encoded, int bits, int sampleCount)
        {
            ValidateBits(bits);
            int     maxVal  = (1 << bits) - 1;
            var     reader  = new BitReader(encoded);
            float[] samples = new float[sampleCount];
            float   prev    = 0f;

            for (int i = 0; i < sampleCount; i++)
            {
                int   q       = reader.ReadBits(bits);
                float dequant = (float)((double)q / maxVal * 4.0 - 2.0);
                float sample  = Math.Clamp(prev + dequant, -1f, 1f);
                samples[i]    = sample;
                prev          = sample;
            }

            return samples;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void ValidateBits(int bits)
        {
            if (bits < 1 || bits > 8)
                throw new ArgumentOutOfRangeException(nameof(bits), "bits must be 1–8.");
        }
    }
}
