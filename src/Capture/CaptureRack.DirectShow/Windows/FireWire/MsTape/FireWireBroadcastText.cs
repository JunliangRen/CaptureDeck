using System.Text;

namespace CaptureRack.DirectShow.Windows.FireWire.MsTape;

internal static class FireWireBroadcastText
{
    private static readonly Encoding Japanese = CodePagesEncodingProvider.Instance.GetEncoding(932,
        EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!;

    // ARIB's default G0/G1/G2/G3 sets and basic shifts cover ordinary Japanese names.
    // Keep raw descriptor bytes visible when a broadcaster uses unsupported glyph sets.
    internal static string? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return null;
        Span<int> sets = [0x42, 0x4a, 0x30, 0x31];
        var gl = 0;
        var gr = 2;
        var single = -1;
        var text = new StringBuilder();
        Span<byte> jis = stackalloc byte[2];
        try
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                var value = bytes[i];
                switch (value)
                {
                    case 0x0e: gl = 1; continue;
                    case 0x0f: gl = 0; continue;
                    case 0x19: single = 2; continue;
                    case 0x1d: single = 3; continue;
                    case 0x20 or 0xa0: text.Append(' '); continue;
                    case 0x1b:
                        if (++i >= bytes.Length) return Raw(bytes);
                        switch (bytes[i])
                        {
                            case 0x6e: gl = 2; break;
                            case 0x6f: gl = 3; break;
                            case 0x7e: gr = 1; break;
                            case 0x7d: gr = 2; break;
                            case 0x7c: gr = 3; break;
                            case 0x24 or 0x28 or 0x29 or 0x2a or 0x2b:
                                var slot = bytes[i] == 0x24 ? 0 : bytes[i] - 0x28;
                                var twoBytes = bytes[i] == 0x24;
                                if (++i >= bytes.Length) return Raw(bytes);
                                if (twoBytes && bytes[i] is >= 0x28 and <= 0x2b)
                                {
                                    slot = bytes[i] - 0x28;
                                    if (++i >= bytes.Length) return Raw(bytes);
                                }
                                if (bytes[i] is not (0x42 or 0x4a or 0x30 or 0x31 or 0x36 or 0x37 or 0x38))
                                    return Raw(bytes);
                                sets[slot] = bytes[i];
                                break;
                            default: return Raw(bytes);
                        }
                        continue;
                }
                var ch = value & 0x7f;
                if (ch is < 0x21 or > 0x7e) return Raw(bytes);
                var set = sets[single >= 0 ? single : value >= 0xa1 ? gr : gl];
                single = -1;
                if (set is 0x4a or 0x36)
                {
                    text.Append((char)ch);
                    continue;
                }
                if (set is 0x30 or 0x31 or 0x37 or 0x38 && ch is >= 0x79 and <= 0x7e)
                {
                    text.Append("ー。「」、・"[ch - 0x79]);
                    continue;
                }
                var row = set switch { 0x30 or 0x37 => 0x24, 0x31 or 0x38 => 0x25, _ => ch };
                if (set == 0x42)
                {
                    if (++i >= bytes.Length) return Raw(bytes);
                    ch = bytes[i] & 0x7f;
                    if (ch is < 0x21 or > 0x7e) return Raw(bytes);
                }
                var lead = (row - 0x21) / 2 + 0x81;
                if (lead > 0x9f) lead += 0x40;
                var trail = (row & 1) == 0 ? ch + 0x7e : ch + (ch < 0x60 ? 0x1f : 0x20);
                jis[0] = (byte)lead;
                jis[1] = (byte)trail;
                text.Append(Japanese.GetString(jis));
            }
            return text.ToString();
        }
        catch (DecoderFallbackException) { return Raw(bytes); }
    }

    private static string Raw(ReadOnlySpan<byte> bytes) => $"HEX {Convert.ToHexString(bytes)}";
}
