using System;
using System.Threading;

namespace AudioCompressor.Helpers
{
    /// <summary>
    /// Delta Modulation (DM) codec — 1 bit per sample.
    ///
    /// The most aggressive codec here.  Each sample is encoded as a single bit:
    ///   1 = signal went up by stepSize
    ///   0 = signal went down by stepSize
    ///
    /// The decoder maintains the same integrator and reconstructs the waveform.
    /// Heavy quantization noise is expected; best for voice at low bit rates.
    ///
    /// Compression ratio vs 16-bit PCM: 16:1.
    ///
    /// Padding: the last byte may have up to 7 unused bits (set to 0).
    /// The exact sample count is stored in the file header so the decoder
    /// never misinterprets padding bits as real samples.
    /// </summary>
    public static class DeltaModulationCodec
    {
        // ── Encode ────────────────────────────────────────────────────────────

        /// <summary>
        /// Encodes float samples [-1,1] to a 1-bit-per-sample packed byte array.
        /// </summary>
        /// <param name="samples">Normalized float samples.</param>
        /// <param name="stepSize">
        ///   Fixed step size for the integrator (default 0.05).
        ///   Larger = tracks fast changes better but more idle noise.
        /// </param>
        public static byte[] Encode(
            float[] samples, float stepSize = 0.05f,
            int channels = 1, CancellationToken token = default)
        {
            ValidateArgs(stepSize, channels);
            byte[] encoded = new byte[(samples.Length + 7) / 8];
            float[] prev   = new float[channels];

            for (int i = 0; i < samples.Length; i++)
            {
                if (token.IsCancellationRequested)
                    throw new OperationCanceledException(token);

                int channel = i % channels;
                int bit = samples[i] > prev[channel] ? 1 : 0;
                if (bit == 1) encoded[i / 8] |= (byte)(1 << (i % 8));

                prev[channel] += bit == 1 ? stepSize : -stepSize;
                prev[channel]  = Math.Clamp(prev[channel], -1f, 1f);
            }

            return encoded;
        }

        // ── Decode ────────────────────────────────────────────────────────────

        /// <summary>
        /// Decodes a 1-bit-per-sample packed byte array back to float samples.
        /// </summary>
        /// <param name="encoded">Bit-packed payload (no header).</param>
        /// <param name="sampleCount">
        ///   Exact original sample count from the file header.
        ///   This prevents padding bits from being decoded as samples.
        /// </param>
        /// <param name="stepSize">Must match the value used during encoding.</param>
        public static float[] Decode(
            byte[] encoded, int sampleCount, float stepSize = 0.05f, int channels = 1)
        {
            ValidateArgs(stepSize, channels);
            float[] samples = new float[sampleCount];
            float[] prev    = new float[channels];

            for (int i = 0; i < sampleCount; i++)
            {
                int channel = i % channels;
                int bit = (encoded[i / 8] >> (i % 8)) & 1;

                prev[channel] += bit == 1 ? stepSize : -stepSize;
                prev[channel]  = Math.Clamp(prev[channel], -1f, 1f);
                samples[i]     = prev[channel];
            }

            return samples;
        }

        private static void ValidateArgs(float stepSize, int channels)
        {
            if (stepSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(stepSize), "stepSize must be > 0.");
            if (channels < 1)
                throw new ArgumentOutOfRangeException(nameof(channels), "channels must be >= 1.");
        }
    }
}
