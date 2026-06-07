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
            int channels = 1,
            CancellationToken token = default,
            Action<int>? progress = null)
        {
            ValidateArgs(minStep, maxStep, alpha, channels);
            byte[] encoded  = new byte[(samples.Length + 7) / 8];
            float[] prev     = new float[channels];
            float[] step     = new float[channels];
            int[]   prevBit1 = new int[channels];
            int[]   prevBit2 = new int[channels];
            int reportEvery = Math.Max(1, samples.Length / 100);

            for (int channel = 0; channel < channels; channel++)
                step[channel] = minStep;

            for (int i = 0; i < samples.Length; i++)
            {
                if (token.IsCancellationRequested)
                    throw new OperationCanceledException(token);

                int channel = i % channels;
                int sampleIndexForChannel = i / channels;
                int bit = samples[i] > prev[channel] ? 1 : 0;
                if (bit == 1) encoded[i / 8] |= (byte)(1 << (i % 8));

                prev[channel] += bit == 1 ? step[channel] : -step[channel];
                prev[channel]  = Math.Clamp(prev[channel], -1f, 1f);

                // Adapt step using local history (no byte re-reads).
                if (sampleIndexForChannel >= 2)
                    step[channel] = (bit == prevBit1[channel] && prevBit1[channel] == prevBit2[channel])
                        ? Math.Min(maxStep, step[channel] * alpha)
                        : Math.Max(minStep, step[channel] / alpha);

                prevBit2[channel] = prevBit1[channel];
                prevBit1[channel] = bit;

                if (progress != null && (i % reportEvery == 0 || i == samples.Length - 1))
                    progress((i + 1) * 100 / samples.Length);
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
            float minStep = 0.02f, float maxStep = 0.2f, float alpha = 1.5f,
            int channels = 1)
        {
            ValidateArgs(minStep, maxStep, alpha, channels);
            float[] samples  = new float[sampleCount];
            float[] prev     = new float[channels];
            float[] step     = new float[channels];
            int[]   prevBit1 = new int[channels];
            int[]   prevBit2 = new int[channels];

            for (int channel = 0; channel < channels; channel++)
                step[channel] = minStep;

            for (int i = 0; i < sampleCount; i++)
            {
                int channel = i % channels;
                int sampleIndexForChannel = i / channels;
                int bit = (encoded[i / 8] >> (i % 8)) & 1;

                prev[channel] += bit == 1 ? step[channel] : -step[channel];
                prev[channel]  = Math.Clamp(prev[channel], -1f, 1f);
                samples[i]     = prev[channel];

                if (sampleIndexForChannel >= 2)
                    step[channel] = (bit == prevBit1[channel] && prevBit1[channel] == prevBit2[channel])
                        ? Math.Min(maxStep, step[channel] * alpha)
                        : Math.Max(minStep, step[channel] / alpha);

                prevBit2[channel] = prevBit1[channel];
                prevBit1[channel] = bit;
            }

            return samples;
        }

        private static void ValidateArgs(float minStep, float maxStep, float alpha, int channels)
        {
            if (minStep <= 0)
                throw new ArgumentOutOfRangeException(nameof(minStep), "minStep must be > 0.");
            if (maxStep < minStep)
                throw new ArgumentOutOfRangeException(nameof(maxStep), "maxStep must be >= minStep.");
            if (alpha <= 1)
                throw new ArgumentOutOfRangeException(nameof(alpha), "alpha must be > 1.");
            if (channels < 1)
                throw new ArgumentOutOfRangeException(nameof(channels), "channels must be >= 1.");
        }
    }
}
