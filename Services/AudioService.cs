using NAudio.Wave;
using System;
using System.IO;

namespace AudioCompressor.Services
{
    public class AudioService
    {
        private WaveOutEvent?    _waveOut;
        private AudioFileReader? _audioReader;

        // ── Load ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Loads an audio file and returns its WaveFormat and normalized float samples.
        /// Also returns the actual file size on disk in bytes (used for compression
        /// ratio calculation — NOT samples.Length * 4 which is the float array size).
        /// </summary>
        public (WaveFormat format, float[] samples, long fileSizeBytes) LoadAudio(string filePath)
        {
            long fileSizeBytes = new FileInfo(filePath).Length;

            using var reader     = new AudioFileReader(filePath);
            var       format     = reader.WaveFormat;
            int sampleCount = (int)(reader.Length / sizeof(float));
            float[] samples = new float[sampleCount];
            int samplesRead = reader.Read(samples, 0, sampleCount);
            if (samplesRead < samples.Length)
                Array.Resize(ref samples, samplesRead);

            return (format, samples, fileSizeBytes);
        }

        // ── Playback ──────────────────────────────────────────────────────────

        public void Play(string filePath)
        {
            Stop();
            _audioReader = new AudioFileReader(filePath);
            _waveOut     = new WaveOutEvent();
            _waveOut.Init(_audioReader);
            _waveOut.Play();
        }

        public void Stop()
        {
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _audioReader?.Dispose();
            _waveOut     = null;
            _audioReader = null;
        }

        /// <summary>Plays raw float samples directly from memory (no temp file needed).</summary>
        public void PlayFromMemory(float[] samples, int sampleRate, int channels)
        {
            Stop();

            // Convert float [-1,1] to 16-bit PCM
            byte[] pcmData = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                short s = (short)Math.Clamp((int)(samples[i] * short.MaxValue),
                                             short.MinValue, short.MaxValue);
                pcmData[i * 2]     = (byte)(s & 0xFF);
                pcmData[i * 2 + 1] = (byte)(s >> 8);
            }

            var ms           = new MemoryStream(pcmData);
            var waveFormat   = new WaveFormat(sampleRate, 16, channels);
            var waveProvider = new RawSourceWaveStream(ms, waveFormat);

            _waveOut = new WaveOutEvent();
            _waveOut.Init(waveProvider);
            _waveOut.Play();
        }

        // ── Properties ────────────────────────────────────────────────────────

        public (TimeSpan duration, int sampleRate, int channels, int bitRate, string codec)
            GetProperties(string filePath)
        {
            using var reader = new AudioFileReader(filePath);
            return (
                reader.TotalTime,
                reader.WaveFormat.SampleRate,
                reader.WaveFormat.Channels,
                reader.WaveFormat.AverageBytesPerSecond * 8,
                reader.WaveFormat.Encoding.ToString()
            );
        }
    }
}
