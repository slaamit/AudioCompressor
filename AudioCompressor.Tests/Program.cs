using System.IO;
using AudioCompressor.Helpers;

var tests = new (string Name, Action Run)[]
{
    ("mu-law round trip", TestMuLawRoundTrip),
    ("DPCM stereo round trip", TestDpcmStereoRoundTrip),
    ("predictive stereo round trip", TestPredictiveStereoRoundTrip),
    ("delta stereo round trip", TestDeltaStereoRoundTrip),
    ("adaptive delta stereo round trip", TestAdaptiveDeltaStereoRoundTrip),
    ("header validation", TestHeaderValidation)
};

foreach (var test in tests)
{
    test.Run();
    Console.WriteLine($"PASS {test.Name}");
}

static float[] CreateStereoSamples(int frames)
{
    var samples = new float[frames * 2];

    for (int i = 0; i < frames; i++)
    {
        samples[i * 2] = (float)(Math.Sin(i * 0.05) * 0.75);
        samples[i * 2 + 1] = (float)(Math.Cos(i * 0.08) * 0.45);
    }

    return samples;
}

static void TestMuLawRoundTrip()
{
    float[] samples = CreateStereoSamples(256);
    byte[] encoded = MuLawCodec.Encode(samples);
    float[] decoded = MuLawCodec.Decode(encoded);

    Assert(encoded.Length == samples.Length, "mu-law should use one byte per sample.");
    Assert(decoded.Length == samples.Length, "mu-law decoded length mismatch.");
    AssertFinite(decoded);
}

static void TestDpcmStereoRoundTrip()
{
    float[] samples = CreateStereoSamples(256);
    byte[] encoded = DpcmCodec.Encode(samples, bits: 4, channels: 2);
    float[] decoded = DpcmCodec.Decode(encoded, bits: 4, sampleCount: samples.Length, channels: 2);

    Assert(encoded.Length == ExpectedPackedBytes(samples.Length, 4), "DPCM payload size mismatch.");
    Assert(decoded.Length == samples.Length, "DPCM decoded length mismatch.");
    AssertFinite(decoded);
}

static void TestPredictiveStereoRoundTrip()
{
    float[] samples = CreateStereoSamples(256);
    byte[] encoded = PredictiveCodec.Encode(samples, bits: 3, order: 3, channels: 2);
    float[] decoded = PredictiveCodec.Decode(
        encoded, bits: 3, sampleCount: samples.Length, order: 3, channels: 2);

    Assert(encoded.Length == ExpectedPackedBytes(samples.Length, 3), "Predictive payload size mismatch.");
    Assert(decoded.Length == samples.Length, "Predictive decoded length mismatch.");
    AssertFinite(decoded);
}

static void TestDeltaStereoRoundTrip()
{
    float[] samples = CreateStereoSamples(256);
    byte[] encoded = DeltaModulationCodec.Encode(samples, stepSize: 0.05f, channels: 2);
    float[] decoded = DeltaModulationCodec.Decode(
        encoded, sampleCount: samples.Length, stepSize: 0.05f, channels: 2);

    Assert(encoded.Length == ExpectedPackedBytes(samples.Length, 1), "Delta payload size mismatch.");
    Assert(decoded.Length == samples.Length, "Delta decoded length mismatch.");
    AssertFinite(decoded);
}

static void TestAdaptiveDeltaStereoRoundTrip()
{
    float[] samples = CreateStereoSamples(256);
    byte[] encoded = AdaptiveDeltaModulationCodec.Encode(samples, channels: 2);
    float[] decoded = AdaptiveDeltaModulationCodec.Decode(
        encoded, sampleCount: samples.Length, channels: 2);

    Assert(encoded.Length == ExpectedPackedBytes(samples.Length, 1), "Adaptive delta payload size mismatch.");
    Assert(decoded.Length == samples.Length, "Adaptive delta decoded length mismatch.");
    AssertFinite(decoded);
}

static void TestHeaderValidation()
{
    byte[] header = CompressedFileHeader.Build(
        CompressedFileHeader.AlgoId.Predictive,
        bits: 3,
        sampleCount: 512,
        sampleRate: 44100,
        channels: 2,
        order: 3);

    Assert(CompressedFileHeader.TryParse(
        header, out var algo, out int bits, out int sampleCount,
        out int sampleRate, out int channels, out int order), "Header should parse.");
    Assert(algo == CompressedFileHeader.AlgoId.Predictive, "Parsed algorithm mismatch.");
    Assert(bits == 3 && sampleCount == 512 && sampleRate == 44100, "Parsed numeric metadata mismatch.");
    Assert(channels == 2 && order == 3, "Parsed channel/order metadata mismatch.");

    int payloadLength = ExpectedPackedBytes(sampleCount, bits);
    Assert(CompressedFileHeader.HasValidPayloadLength(payloadLength, algo, bits, sampleCount),
        "Valid predictive payload length was rejected.");
    Assert(!CompressedFileHeader.HasValidPayloadLength(payloadLength - 1, algo, bits, sampleCount),
        "Truncated payload length was accepted.");

    var badHeader = (byte[])header.Clone();
    badHeader[6] = 9;
    Assert(!CompressedFileHeader.TryParse(
        badHeader, out _, out _, out _, out _, out _, out _), "Invalid bit depth was accepted.");

    AssertThrows<InvalidDataException>(() =>
        CompressedFileHeader.Build(
            CompressedFileHeader.AlgoId.Dpcm,
            bits: 0,
            sampleCount: 512,
            sampleRate: 44100,
            channels: 2),
        "Invalid DPCM header did not throw.");
}

static int ExpectedPackedBytes(int sampleCount, int bits) => (sampleCount * bits + 7) / 8;

static void AssertFinite(float[] samples)
{
    for (int i = 0; i < samples.Length; i++)
        Assert(float.IsFinite(samples[i]), $"Sample {i} is not finite.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}
