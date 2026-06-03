using System;
using System.Collections.Generic;
using System.Threading;

namespace AudioCompressor.Helpers
{
    public static class AudioCompressor
    {
        // ========== 1. Nonlinear Quantization (μ-law) ==========
        public static byte[] MuLawEncode(float[] samples, CancellationToken token = default)
        {
            byte[] compressed = new byte[samples.Length];
            for (int i = 0; i < samples.Length; i++)
            {
                if (token.IsCancellationRequested) throw new OperationCanceledException();
                float sample = samples[i];
                double sign = sample >= 0 ? 1 : -1;
                double absSample = Math.Min(Math.Abs(sample), 1.0);
                double compressedVal = sign * (Math.Log(1 + 255 * absSample) / Math.Log(1 + 255));
                compressed[i] = (byte)((compressedVal + 1) / 2 * 255);
            }
            return compressed;
        }
        public static float[] MuLawDecode(byte[] compressed)
        {
            float[] samples = new float[compressed.Length];
            for (int i = 0; i < compressed.Length; i++)
            {
                double val = compressed[i] / 255.0 * 2 - 1;
                double sign = val >= 0 ? 1 : -1;
                double absVal = Math.Abs(val);
                double sample = sign * (1.0 / 255) * (Math.Pow(1 + 255, absVal) - 1);
                samples[i] = (float)sample;
            }
            return samples;
        }

        // ========== 2. Differential PCM (DPCM) ==========
        public static byte[] DpcmEncode(float[] samples, int bits, CancellationToken token = default)
        {
            int maxDiff = (int)Math.Pow(2, bits) - 1;
            List<byte> encoded = new List<byte>();
            float prev = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                if (token.IsCancellationRequested) throw new OperationCanceledException();
                float diff = samples[i] - prev;
                int quantizedDiff = (int)((diff + 1) / 2 * maxDiff);
                quantizedDiff = Math.Clamp(quantizedDiff, 0, maxDiff);
                encoded.Add((byte)quantizedDiff);
                float dequantDiff = (float)quantizedDiff / maxDiff * 2 - 1;
                prev = prev + dequantDiff;
                prev = Math.Clamp(prev, -1, 1);
            }
            return encoded.ToArray();
        }
        public static float[] DpcmDecode(byte[] encoded, int bits)
        {
            int maxDiff = (int)Math.Pow(2, bits) - 1;
            float[] samples = new float[encoded.Length];
            float prev = 0;
            for (int i = 0; i < encoded.Length; i++)
            {
                float dequantDiff = (float)encoded[i] / maxDiff * 2 - 1;
                float sample = prev + dequantDiff;
                sample = Math.Clamp(sample, -1, 1);
                samples[i] = sample;
                prev = sample;
            }
            return samples;
        }

        // ========== 3. Predictive Differential Coding (FIR predictor) ==========
        public static byte[] PredictiveEncode(float[] samples, int bits, int order = 2, CancellationToken token = default)
        {
            int maxDiff = (int)Math.Pow(2, bits) - 1;
            List<byte> encoded = new List<byte>();
            float[] prevSamples = new float[order];
            for (int i = 0; i < samples.Length; i++)
            {
                if (token.IsCancellationRequested) throw new OperationCanceledException();
                // simple predictor: average of last 'order' samples (or linear prediction)
                float predicted = 0;
                for (int j = 0; j < order && j <= i; j++) predicted += prevSamples[order - 1 - j];
                predicted = (i >= order) ? predicted / order : 0;
                float diff = samples[i] - predicted;
                int quantizedDiff = (int)((diff + 1) / 2 * maxDiff);
                quantizedDiff = Math.Clamp(quantizedDiff, 0, maxDiff);
                encoded.Add((byte)quantizedDiff);
                float dequantDiff = (float)quantizedDiff / maxDiff * 2 - 1;
                float reconstructed = predicted + dequantDiff;
                // shift buffer
                for (int j = 0; j < order - 1; j++) prevSamples[j] = prevSamples[j + 1];
                if (order > 0) prevSamples[order - 1] = reconstructed;
            }
            return encoded.ToArray();
        }
        public static float[] PredictiveDecode(byte[] encoded, int bits, int order = 2)
        {
            int maxDiff = (int)Math.Pow(2, bits) - 1;
            float[] samples = new float[encoded.Length];
            float[] prevSamples = new float[order];
            for (int i = 0; i < encoded.Length; i++)
            {
                float predicted = 0;
                for (int j = 0; j < order && j <= i; j++) predicted += prevSamples[order - 1 - j];
                predicted = (i >= order) ? predicted / order : 0;
                float dequantDiff = (float)encoded[i] / maxDiff * 2 - 1;
                float sample = predicted + dequantDiff;
                sample = Math.Clamp(sample, -1, 1);
                samples[i] = sample;
                for (int j = 0; j < order - 1; j++) prevSamples[j] = prevSamples[j + 1];
                if (order > 0) prevSamples[order - 1] = sample;
            }
            return samples;
        }

        // ========== 4. Delta Modulation (1-bit) ==========
        public static byte[] DeltaEncode(float[] samples, float stepSize = 0.05f, CancellationToken token = default)
        {
            byte[] encoded = new byte[(samples.Length + 7) / 8];
            float prev = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                if (token.IsCancellationRequested) throw new OperationCanceledException();
                int bit = (samples[i] > prev) ? 1 : 0;
                int byteIdx = i / 8;
                int bitIdx = i % 8;
                if (bit == 1) encoded[byteIdx] |= (byte)(1 << bitIdx);
                prev += (bit == 1 ? stepSize : -stepSize);
                prev = Math.Clamp(prev, -1, 1);
            }
            return encoded;
        }
        public static float[] DeltaDecode(byte[] encoded, int sampleCount, float stepSize = 0.05f)
        {
            float[] samples = new float[sampleCount];
            float prev = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                int byteIdx = i / 8;
                int bitIdx = i % 8;
                int bit = (encoded[byteIdx] >> bitIdx) & 1;
                prev += (bit == 1 ? stepSize : -stepSize);
                prev = Math.Clamp(prev, -1, 1);
                samples[i] = prev;
            }
            return samples;
        }

        // ========== 5. Adaptive Delta Modulation ==========
        public static byte[] AdaptiveDeltaEncode(float[] samples, float minStep = 0.02f, float maxStep = 0.2f, float alpha = 1.5f, CancellationToken token = default)
        {
            byte[] encoded = new byte[(samples.Length + 7) / 8];
            float prev = 0;
            float step = minStep;
            for (int i = 0; i < samples.Length; i++)
            {
                if (token.IsCancellationRequested) throw new OperationCanceledException();
                int bit = (samples[i] > prev) ? 1 : 0;
                int byteIdx = i / 8;
                int bitIdx = i % 8;
                if (bit == 1) encoded[byteIdx] |= (byte)(1 << bitIdx);
                prev += (bit == 1 ? step : -step);
                prev = Math.Clamp(prev, -1, 1);
                // adapt step size: if three consecutive bits same, increase step; else decrease
                if (i >= 2)
                {
                    int prevBit1 = (encoded[(i - 1) / 8] >> ((i - 1) % 8)) & 1;
                    int prevBit2 = (encoded[(i - 2) / 8] >> ((i - 2) % 8)) & 1;
                    if (bit == prevBit1 && prevBit1 == prevBit2)
                        step = Math.Min(maxStep, step * alpha);
                    else
                        step = Math.Max(minStep, step / alpha);
                }
            }
            return encoded;
        }
        public static float[] AdaptiveDeltaDecode(byte[] encoded, int sampleCount, float minStep = 0.02f, float maxStep = 0.2f, float alpha = 1.5f)
        {
            float[] samples = new float[sampleCount];
            float prev = 0;
            float step = minStep;
            for (int i = 0; i < sampleCount; i++)
            {
                int byteIdx = i / 8;
                int bitIdx = i % 8;
                int bit = (encoded[byteIdx] >> bitIdx) & 1;
                prev += (bit == 1 ? step : -step);
                prev = Math.Clamp(prev, -1, 1);
                samples[i] = prev;
                // adapt step size same as encoder
                if (i >= 2)
                {
                    int prevBit1 = (encoded[(i - 1) / 8] >> ((i - 1) % 8)) & 1;
                    int prevBit2 = (encoded[(i - 2) / 8] >> ((i - 2) % 8)) & 1;
                    if (bit == prevBit1 && prevBit1 == prevBit2)
                        step = Math.Min(maxStep, step * alpha);
                    else
                        step = Math.Max(minStep, step / alpha);
                }
            }
            return samples;
        }
    }
}