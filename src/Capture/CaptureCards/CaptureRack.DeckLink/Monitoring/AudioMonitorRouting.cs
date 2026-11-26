using System.Runtime.CompilerServices;

namespace CaptureRack.DeckLink;

public enum AudioMonitorMode
{
    Pair = 0,
    Downmix = 1
}

public readonly record struct AudioMonitorRoute(AudioMonitorMode Mode, int PairIndex = 0)
{
    public const int MaxSupportedChannels = 16;
    public const int MaxSupportedPairs = MaxSupportedChannels / 2;

    public static AudioMonitorRoute Default => new(AudioMonitorMode.Pair, 0);
    public static AudioMonitorRoute Pair(int pairIndex) => new(AudioMonitorMode.Pair, Math.Clamp(pairIndex, 0, MaxSupportedPairs - 1));
    public static AudioMonitorRoute Downmix => new(AudioMonitorMode.Downmix, 0);

    public AudioMonitorRoute ClampForChannels(int channels)
    {
        var safeChannels = Math.Clamp(channels, 1, MaxSupportedChannels);
        if (safeChannels <= 2)
            return Default;

        if (Mode == AudioMonitorMode.Downmix)
            return Downmix;

        var maxPair = Math.Max(0, (safeChannels + 1) / 2 - 1);
        return Pair(Math.Clamp(PairIndex, 0, maxPair));
    }

    internal int Pack() => Mode == AudioMonitorMode.Downmix ? -1 : Math.Clamp(PairIndex, 0, MaxSupportedPairs - 1);

    internal static AudioMonitorRoute Unpack(int packed) =>
        packed < 0 ? Downmix : Pair(packed);
}

public enum AudioChannelLayout
{
    Unknown = 0,
    Mono = 1,
    Stereo = 2,
    Surround51 = 3,
    Surround71 = 4
}

public static class AudioChannelLayoutHelper
{
    public static AudioChannelLayout Detect(string? layoutName, int channels)
    {
        if (!string.IsNullOrWhiteSpace(layoutName))
        {
            var trimmed = layoutName.Trim();
            if (trimmed.StartsWith("7.1", StringComparison.OrdinalIgnoreCase))
                return channels >= 8 ? AudioChannelLayout.Surround71 : DetectFromCount(channels);
            if (trimmed.StartsWith("5.1", StringComparison.OrdinalIgnoreCase))
                return channels >= 6 ? AudioChannelLayout.Surround51 : DetectFromCount(channels);
            if (trimmed.Equals("stereo", StringComparison.OrdinalIgnoreCase))
                return channels == 2 ? AudioChannelLayout.Stereo : DetectFromCount(channels);
            if (trimmed.Equals("mono", StringComparison.OrdinalIgnoreCase))
                return channels == 1 ? AudioChannelLayout.Mono : DetectFromCount(channels);
        }

        return DetectFromCount(channels);
    }

    private static AudioChannelLayout DetectFromCount(int channels) => channels switch
    {
        1 => AudioChannelLayout.Mono,
        2 => AudioChannelLayout.Stereo,
        _ => AudioChannelLayout.Unknown
    };

    public static string ChannelCode(int channelIndex, int totalChannels, AudioChannelLayout layout)
    {
        if (totalChannels <= 1)
            return "M";
        if (totalChannels == 2)
            return channelIndex == 0 ? "L" : "R";

        if (layout == AudioChannelLayout.Surround51 && channelIndex is >= 0 and < 6)
        {
            return channelIndex switch
            {
                0 => "L",
                1 => "R",
                2 => "C",
                3 => "LFE",
                4 => "Ls",
                _ => "Rs"
            };
        }

        if (layout == AudioChannelLayout.Surround71 && channelIndex is >= 0 and < 8)
        {
            return channelIndex switch
            {
                0 => "L",
                1 => "R",
                2 => "C",
                3 => "LFE",
                4 => "Ls",
                5 => "Rs",
                6 => "Lrs",
                _ => "Rrs"
            };
        }

        return (channelIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string PairShortLabel(int pairIndex, int totalChannels, AudioChannelLayout layout)
    {
        var left = pairIndex * 2;
        var right = left + 1;
        if (right >= totalChannels)
            return ChannelCode(left, totalChannels, layout);

        var numeric = $"{left + 1}-{right + 1}";
        if (layout is AudioChannelLayout.Surround51 or AudioChannelLayout.Surround71)
        {
            var lCode = ChannelCode(left, totalChannels, layout);
            var rCode = ChannelCode(right, totalChannels, layout);
            return $"{numeric} ({lCode}/{rCode})";
        }

        return numeric;
    }

    public static string PairCompactLabel(int pairIndex, int totalChannels)
    {
        var left = pairIndex * 2;
        var right = left + 1;
        return right >= totalChannels ? $"{left + 1}" : $"{left + 1}-{right + 1}";
    }
}

public static class AudioMonitorMixer
{
    private const double SurroundCoefficient = 0.7071067811865475; // 1 / sqrt(2), ITU-R BS.775
    private const double SoftLimitKnee = 0.85;
    private const double SoftLimitHeadroom = 1.0 - SoftLimitKnee;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (double Left, double Right) RouteFrame(
        ReadOnlySpan<double> frame,
        AudioMonitorRoute route,
        AudioChannelLayout layout,
        int activePairMask)
    {
        var channels = frame.Length;
        if (channels == 0)
            return (0, 0);
        if (channels == 1)
            return (frame[0], frame[0]);
        if (channels == 2)
            return (frame[0], frame[1]);

        if (route.Mode == AudioMonitorMode.Pair)
        {
            var clamped = route.ClampForChannels(channels);
            var leftIndex = clamped.PairIndex * 2;
            var rightIndex = leftIndex + 1;
            var left = leftIndex < channels ? frame[leftIndex] : 0;
            var right = rightIndex < channels ? frame[rightIndex] : left;
            return (left, right);
        }

        // Stereo downmix for local speaker/headphone monitoring only.
        if (layout == AudioChannelLayout.Surround51 && channels >= 6)
        {
            var center = frame[2] * SurroundCoefficient;
            var l = frame[0] + center + frame[4] * SurroundCoefficient;
            var r = frame[1] + center + frame[5] * SurroundCoefficient;
            return (SoftLimit(l), SoftLimit(r));
        }

        if (layout == AudioChannelLayout.Surround71 && channels >= 8)
        {
            var center = frame[2] * SurroundCoefficient;
            var l = frame[0] + center + frame[4] * SurroundCoefficient + frame[6] * SurroundCoefficient;
            var r = frame[1] + center + frame[5] * SurroundCoefficient + frame[7] * SurroundCoefficient;
            return (SoftLimit(l), SoftLimit(r));
        }

        // Generic multi-pair PCM fold-down: sum odd->L, even->R across active pairs with equal-power scaling.
        var pairCount = (channels + 1) / 2;
        var effectiveMask = activePairMask & ((1 << Math.Min(pairCount, 30)) - 1);
        if (effectiveMask == 0)
            effectiveMask = 1; // Always include Ch 1-2 when all pairs are silent.

        var activePairs = 0;
        var sumLeft = 0.0;
        var sumRight = 0.0;
        for (var p = 0; p < pairCount; p++)
        {
            if ((effectiveMask & (1 << p)) == 0)
                continue;
            activePairs++;
            var leftIdx = p * 2;
            var rightIdx = leftIdx + 1;
            sumLeft += frame[leftIdx];
            sumRight += rightIdx < channels ? frame[rightIdx] : frame[leftIdx];
        }

        var scale = activePairs > 1 ? 1.0 / Math.Sqrt(activePairs) : 1.0;
        return (SoftLimit(sumLeft * scale), SoftLimit(sumRight * scale));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double SoftLimit(double sample)
    {
        var abs = Math.Abs(sample);
        if (abs <= SoftLimitKnee)
            return sample;

        var limited = SoftLimitKnee + SoftLimitHeadroom * Math.Tanh((abs - SoftLimitKnee) / SoftLimitHeadroom);
        return Math.CopySign(Math.Min(1.0, limited), sample);
    }
}
