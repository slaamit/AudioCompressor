using System;
using System.IO;

namespace AudioCompressor.Helpers
{
    /// <summary>
    /// Header prepended to every .bin compressed file so the file is
    /// completely self-describing — no external metadata needed to decompress.
    ///
    /// Layout (all little-endian):
    ///   [0..3]   magic       "ACMP"
    ///   [4]      version     header version byte
    ///   [5]      algorithm   AlgoId enum (byte)
    ///   [6]      bits        bits per encoded sample
    ///   [7]      channels    byte
    ///   [8..11]  sampleCount original float sample count (int32)
    ///   [12..15] sampleRate  Hz (int32)
    ///   [16]     order       predictor order (0 for non-predictive codecs)
    ///   [17..19] reserved    zero
    /// </summary>
    public static class CompressedFileHeader
    {
        public const int Size = 20;

        private static readonly byte[] Magic =
            { (byte)'A', (byte)'C', (byte)'M', (byte)'P' };

        private const byte Version = 0x20;

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
            Validate(algo, bits, sampleCount, sampleRate, channels, order);

            using var ms = new MemoryStream(Size);
            using var w  = new BinaryWriter(ms);
            w.Write(Magic);
            w.Write(Version);
            w.Write((byte)algo);
            w.Write((byte)bits);
            w.Write((byte)channels);
            w.Write(sampleCount);
            w.Write(sampleRate);
            w.Write((byte)order);
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((byte)0);
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
            if (data[4] != Version) return false;

            algo        = (AlgoId)data[5];
            bits        = data[6];
            channels    = data[7];
            sampleCount = BitConverter.ToInt32(data, 8);
            sampleRate  = BitConverter.ToInt32(data, 12);
            order       = data[16];
            return IsValid(algo, bits, sampleCount, sampleRate, channels, order);
        }

        public static bool HasValidPayloadLength(
            int payloadLength, AlgoId algo, int bits, int sampleCount)
        {
            if (payloadLength < 0 || sampleCount <= 0) return false;

            long expected = algo switch
            {
                AlgoId.MuLaw => sampleCount,
                AlgoId.Dpcm or AlgoId.Predictive => ((long)sampleCount * bits + 7) / 8,
                AlgoId.Delta or AlgoId.AdaptiveDelta => ((long)sampleCount + 7) / 8,
                _ => -1
            };

            return expected >= 0 && payloadLength == expected;
        }

        private static bool IsValid(
            AlgoId algo, int bits, int sampleCount,
            int sampleRate, int channels, int order)
        {
            if (!Enum.IsDefined(typeof(AlgoId), algo)) return false;
            if (sampleCount <= 0 || sampleRate <= 0) return false;
            if (channels < 1 || channels > 8) return false;

            return algo switch
            {
                AlgoId.MuLaw => bits == 8 && order == 0,
                AlgoId.Dpcm => bits is >= 1 and <= 8 && order == 0,
                AlgoId.Predictive => bits is >= 1 and <= 8 && order is >= 1 and <= 8,
                AlgoId.Delta or AlgoId.AdaptiveDelta => bits == 1 && order == 0,
                _ => false
            };
        }

        private static void Validate(
            AlgoId algo, int bits, int sampleCount,
            int sampleRate, int channels, int order)
        {
            if (!IsValid(algo, bits, sampleCount, sampleRate, channels, order))
                throw new InvalidDataException("Compressed file header contains invalid metadata.");
        }
    }
}
