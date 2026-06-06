using System;
using System.Threading;

namespace AudioCompressor.Helpers
{
    /// <summary>
    /// Adaptive Delta Modulation (ADM) codec — 1 bit per sample, variable step.
    ///
    /// Improves on plain Delta Modulation by dynamically adjusting the step size:
    ///   • Three consecutive same-direction bits → signal is moving fast → increase step
    ///   • Otherwise → signal is stable or reversing → decrease step
    ///
    /// This reduces both slope overload (step too small for fast signals) and
    /// granular noise (step too large for slow signals).
    ///
    /// Compression ratio vs 16-bit PCM: 16:1 (same as DM, better quality).
    ///
    /// State tracking: encoder and decoder both use local prevBit variables
    /// rather than re-reading the encoded byte array, which is cleaner and
    /// guarantees perfect encoder/decoder state synchronisation.
    /// </summary>
    public static class AdaptiveDeltaModulationCodec
    {
        // ── Encode ────────────────────────────────────────────────────────────

        /// <summary>
        /// Encodes float samples [-1,1] to a 1-bit-per-sample packed byte array
        /// with adaptive step size.
        /// </summary>
        /// <param name="samples">Normalized float samples.</param>
        /// <param name="minStep">Minimum step size (default 0.02).</param>
        /// <param name="maxStep">Maximum step size (default 0.2).</param>
        /// <param name="alpha">Step size multiplier/divisor (default 1.5).</param>
        public static byte[] Encode(
            float[] samples,
            float minStep = 0.02f, float maxStep = 0.2f, float alpha = 1.5f,
            CancellationToken token = default)
        {
            byte[] encoded  = new byte[(samples.Length + 7) / 8];
            float  prev     = 0f;
            float  step     = minStep;
            int    prevBit1 = 0, prevBit2 = 0;

            for (int i = 0; i < samples.Length; i++)
            {
                if (token.IsCancellationRequested)
                    throw new OperationCanceledException(token);

                int bit = samples[i] > prev ? 1 : 0;
                if (bit == 1) encoded[i / 8] |= (byte)(1 << (i % 8));

                prev  += bit == 1 ? step : -step;
                prev   = Math.Clamp(prev, -1f, 1f);

                // Adapt step using local history (no byte re-reads).
                if (i >= 2)
                    step = (bit == prevBit1 && prevBit1 == prevBit2)
                        ? Math.Min(maxStep, step * alpha)
                        : Math.Max(minStep, step / alpha);

                prevBit2 = prevBit1;
                prevBit1 = bit;
            }

            return encoded;
        }

        // ── Decode ────────────────────────────────────────────────────────────

        /// <summary>
        /// Decodes a 1-bit-per-sample adaptive packed byte array back to float samples.
        /// All parameters must exactly match those used during encoding.
        /// </summary>
        /// <param name="encoded">Bit-packed payload (no header).</param>
        /// <param name="sampleCount">Exact original sample count from the file header.</param>
        public static float[] Decode(
            byte[] encoded, int sampleCount,
            float minStep = 0.02f, float maxStep = 0.2f, float alpha = 1.5f)
        {
            float[] samples  = new float[sampleCount];
            float   prev     = 0f;
            float   step     = minStep;
            int     prevBit1 = 0, prevBit2 = 0;

            for (int i = 0; i < sampleCount; i++)
            {
                int bit = (encoded[i / 8] >> (i % 8)) & 1;

                prev     += bit == 1 ? step : -step;
                prev      = Math.Clamp(prev, -1f, 1f);
                samples[i] = prev;

                if (i >= 2)
                    step = (bit == prevBit1 && prevBit1 == prevBit2)
                        ? Math.Min(maxStep, step * alpha)
                        : Math.Max(minStep, step / alpha);

                prevBit2 = prevBit1;
                prevBit1 = bit;
            }

            return samples;
        }
    }
}
