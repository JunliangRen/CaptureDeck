namespace CaptureRack.SignalCapture;

public enum RtlSdrTuner { Unknown, E4000, Fc0012, Fc0013, Fc2580, R820T, R828D }

public sealed record RtlSdrDevice(uint Index, string Name, string? Manufacturer, string? Product, string? Serial);

public sealed record RtlSdrCapabilities(RtlSdrDevice Device, RtlSdrTuner Tuner,
    IReadOnlyList<int> GainsTenthsDb, bool IsBlogV4, bool SupportsManualVga)
{
    internal static RtlSdrCapabilities FromDevice(RtlSdrDevice device, RtlSdrTuner tuner, int[] gains, bool nativeVgaSupport)
    {
        var blogV4 = device is { Manufacturer: "RTLSDRBlog", Product: "Blog V4" } && tuner == RtlSdrTuner.R828D;
        return new(device, tuner, gains, blogV4, blogV4 && nativeVgaSupport);
    }
}

public sealed record RtlSdrSettings(uint FrequencyHz = 100_000_000, uint SampleRateHz = 2_400_000,
    int? GainTenthsDb = null, bool DigitalAgc = false, int? VgaIndex = null, int? Ppm = null)
{
    internal int? Validate(RtlSdrCapabilities capabilities)
    {
        if (FrequencyHz == 0 || SampleRateHz == 0)
            throw new ArgumentException("Frequency and sample rate must be greater than zero.");
        if (GainTenthsDb is int gain && !capabilities.GainsTenthsDb.Contains(gain))
            throw new ArgumentException($"The tuner does not advertise gain {gain} (tenths of a dB). Run probe for its supported values.");
        if (VgaIndex is < 0 or > 8)
            throw new ArgumentException("Manual VGA index must be 0–8.");
        if (VgaIndex.HasValue && !GainTenthsDb.HasValue)
            throw new ArgumentException("Manual VGA requires manual tuner gain.");
        if (VgaIndex.HasValue && !capabilities.SupportsManualVga)
            throw new ArgumentException("Manual VGA requires an R828D Blog V4 and patched librtlsdr.");

        return GainTenthsDb.HasValue && capabilities.SupportsManualVga ? VgaIndex ?? 8 : null;
    }
}

public sealed record RtlSdrConfigured(RtlSdrCapabilities Capabilities, RtlSdrSettings Requested,
    uint ActualFrequencyHz, uint ActualSampleRateHz, int ActualPpm, int? GainTenthsDb, int? AppliedVgaIndex);
