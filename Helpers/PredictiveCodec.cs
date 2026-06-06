using System;
using System.Threading;

namespace AudioCompressor.Helpers
{
    /// <summary>
    /// Predictive Differential Coding codec (FIR-predictor DPCM) with bit-packing.
    ///
    /// Improves on plain DPCM by predicting each sample from recent history before
    /// computing the residual.  A good prediction means smaller residuals, which
    /// means fewer bits needed for the same quality.
    ///
    /// Predictor: weighted average of the last `order` reconstructed samples,
    /// with more recent samples receiving linearly higher weights.
    ///
    /// Warm-up: the first `order` samples use however many history samples are
    /// available rather than defaulting to 0, avoiding a quality dip at the start.
    ///
    /// Bit-packing: same as <see cref="DpcmCodec"/> — real size reduction.
    /// </summary>
    public static class PredictiveCodec
    {
        // ── Encode ────────────────────────────────────────────────────────────

        /// <summary>
        /// Encodes float samples [-1,1] to a bit-packed predictive-DPCM byte array.
        /// </summary>
        /// <param name="samples">Normalized float samples.</param>
        /// <param name="bits">Bits per residual (1–8).</param>
        /// <param name="order">Predictor order — how many past samples to use (default 2).</param>
        public static byte[] Encode(
            float[] samples, int bits, int order = 2,
            CancellationToken token = default)
        {
            ValidateArgs(bits, order);
            int     maxVal  = (1 << bits) - 1;
            var     writer  = new BitWriter();
            float[] history = new float[order];   // ring of reconstructed samples

            for (int i = 0; i < samples.Length; i++)
            {
                if (token.IsCancellationRequested)
                    throw new OperationCanceledException(token);

                float predicted = Predict(history, Math.Min(i, order));
                float diff      = samples[i] - predicted;

                int q = (int)Math.Round((diff + 2.0) / 4.0 * maxVal);
                q = Math.Clamp(q, 0, maxVal);
                writer.WriteBits(q, bits);

                // Reconstruct exactly as the decoder will.
                float dequant      = (float)((double)q / maxVal * 4.0 - 2.0);
                float reconstructed = Math.Clamp(predicted + dequant, -1f, 1f);

                ShiftHistory(history, reconstructed, order);
            }

            return writer.ToArray();
        }

        // ── Decode ────────────────────────────────────────────────────────────

        /// <summary>
        /// Decodes a bit-packed predictive-DPCM byte array back to float samples.
        /// </summary>
        /// <param name="encoded">Bit-packed payload (no header).</param>
        /// <param name="bits">Must match the value used during encoding.</param>
        /// <param name="sampleCount">Original sample count (stored in file header).</param>
        /// <param name="order">Must match the value used during encoding.</param>
        public static float[] Decode(byte[] encoded, int bits, int sampleCount, int order = 2)
        {
            ValidateArgs(bits, order);
            int     maxVal  = (1 << bits) - 1;
            var     reader  = new BitReader(encoded);
            float[] samples = new float[sampleCount];
            float[] history = new float[order];

            for (int i = 0; i < sampleCount; i++)
            {
                float predicted = Predict(history, Math.Min(i, order));
                int   q         = reader.ReadBits(bits);
                float dequant   = (float)((double)q / maxVal * 4.0 - 2.0);
                float sample    = Math.Clamp(predicted + dequant, -1f, 1f);
                samples[i]      = sample;
                ShiftHistory(history, sample, order);
            }

            return samples;
        }

        // ── Predictor ─────────────────────────────────────────────────────────

        /// <summary>
        /// Weighted linear predictor.  More recent samples get higher weight:
        ///   weight[j] = j + 1  (oldest = 1, newest = available)
        /// Falls back to 0 if no history is available yet (start of stream).
        /// </summary>
        private static float Predict(float[] history, int available)
        {
            if (available == 0) return 0f;

            double sum = 0, weightSum = 0;
            int    offset = history.Length - available;

            for (int j = 0; j < available; j++)
            {
                double w  = j + 1;          // linearly increasing weight
                sum       += history[offset + j] * w;
                weightSum += w;
            }

            return (float)(sum / weightSum);
        }

        /// <summary>Appends a new sample to the history ring (oldest is discarded).</summary>
        private static void ShiftHistory(float[] history, float newSample, int order)
        {
            if (order == 0) return;
            Array.Copy(history, 1, history, 0, order - 1);
            history[order - 1] = newSample;
        }

        // ── Validation ────────────────────────────────────────────────────────

        private static void ValidateArgs(int bits, int order)
        {
            if (bits < 1 || bits > 8)
                throw new ArgumentOutOfRangeException(nameof(bits), "bits must be 1–8.");
            if (order < 1)
                throw new ArgumentOutOfRangeException(nameof(order), "order must be >= 1.");
        }
    }
}
