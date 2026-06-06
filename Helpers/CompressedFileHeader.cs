using System;
using System.IO;

namespace AudioCompressor.Helpers
{
    /// <summary>
    /// 16-byte header prepended to every .bin compressed file so the file is
    /// completely self-describing — no external metadata needed to decompress.
    ///
    /// Layout (all little-endian):
    ///   [0..3]   magic       "ACMP"
    ///   [4]      algorithm   AlgoId enum (byte)
    ///   [5]      bits        quantization bits (0 for 1-bit delta methods)
    ///   [6..9]   sampleCount original float sample count (int32)
    ///   [10..13] sampleRate  Hz (int32)
    ///   [14]     channels    byte
    ///   [15]     order       predictor order (0 for non-predictive codecs)
    /// </summary>
    public static class CompressedFileHeader
    {
        public const int Size = 16;

        private static readonly byte[] Magic =
            { (byte)'A', (byte)'C', (byte)'M', (byte)'P' };

        public enum AlgoId : byte
        {
            MuLaw        = 0,
            Dpcm         = 1,
            Predictive   = 2,
            Delta        = 3,
            AdaptiveDelta = 4
        }

        public static byte[] Build(
            AlgoId algo, int bits, int sampleCount,
            int sampleRate, int channels, int order = 0)
        {
            using var ms = new MemoryStream(Size);
            using var w  = new BinaryWriter(ms);
            w.Write(Magic);
            w.Write((byte)algo);
            w.Write((byte)bits);
            w.Write(sampleCount);
            w.Write(sampleRate);
            w.Write((byte)channels);
            w.Write((byte)order);
            return ms.ToArray();
        }

        public static bool TryParse(
            byte[] data,
            out AlgoId algo, out int bits, out int sampleCount,
            out int sampleRate, out int channels, out int order)
        {
            algo = 0; bits = 0; sampleCount = 0;
            sampleRate = 0; channels = 1; order = 0;

            if (data.Length < Size) return false;
            if (data[0] != Magic[0] || data[1] != Magic[1] ||
                data[2] != Magic[2] || data[3] != Magic[3]) return false;

            algo        = (AlgoId)data[4];
            bits        = data[5];
            sampleCount = BitConverter.ToInt32(data, 6);
            sampleRate  = BitConverter.ToInt32(data, 10);
            channels    = data[14];
            order       = data[15];
            return true;
        }
    }
}
