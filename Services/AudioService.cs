using NAudio.Wave;
using System;
using System.IO;

namespace AudioCompressor.Services
{
    public class AudioService
    {
        private WaveOutEvent? _waveOut;
        private AudioFileReader? _audioReader;

        public (WaveFormat format, float[] samples) LoadAudio(string filePath)
        {
            using var reader = new AudioFileReader(filePath);
            var format = reader.WaveFormat;
            int sampleCount = (int)(reader.Length / 4);
            float[] samples = new float[sampleCount];
            reader.Read(samples, 0, sampleCount);
            return (format, samples);
        }

        public void Play(string filePath)
        {
            Stop();
            _audioReader = new AudioFileReader(filePath);
            _waveOut = new WaveOutEvent();
            _waveOut.Init(_audioReader);
            _waveOut.Play();
        }

        public void Stop()
        {
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _audioReader?.Dispose();
            _waveOut = null;
            _audioReader = null;
        }

        public (TimeSpan duration, int sampleRate, int channels, int bitRate, string codec) GetProperties(string filePath)
        {
            using var reader = new AudioFileReader(filePath);
            return (reader.TotalTime, reader.WaveFormat.SampleRate, reader.WaveFormat.Channels,
                    reader.WaveFormat.AverageBytesPerSecond * 8, reader.WaveFormat.Encoding.ToString());
        }

        // تشغيل مباشر من الذاكرة
        public void PlayFromMemory(float[] samples, int sampleRate, int channels)
        {
            Stop();
            byte[] pcmData = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                short shortSample = (short)(samples[i] * short.MaxValue);
                byte[] bytes = BitConverter.GetBytes(shortSample);
                pcmData[i * 2] = bytes[0];
                pcmData[i * 2 + 1] = bytes[1];
            }
            var memoryStream = new MemoryStream(pcmData);
            var waveFormat = new WaveFormat(sampleRate, channels);
            var waveProvider = new RawSourceWaveStream(memoryStream, waveFormat);
            _waveOut = new WaveOutEvent();
            _waveOut.Init(waveProvider);
            _waveOut.Play();
        }
    }
}