#if REALTIME_RF
using VHSDecode.Preview;

namespace CaptureRack.App;

internal static class RfDecoderOptions
{
    internal static RealtimePreviewOptions Create(RfPreviewType type, int rate, bool pal, bool cvbsRestore,
        int betaProfile, int ldTrack)
    {
        var source = type switch
        {
            RfPreviewType.VideoOnly => PreviewSourceFormat.Cvbs,
            RfPreviewType.Betamax or RfPreviewType.BetamaxHifi => PreviewSourceFormat.Betamax,
            RfPreviewType.LaserDisc => PreviewSourceFormat.LaserDisc,
            RfPreviewType.SVhs => PreviewSourceFormat.SVhs,
            RfPreviewType.SecamVhs => PreviewSourceFormat.SecamVhs,
            RfPreviewType.MesecamVhs => PreviewSourceFormat.MesecamVhs,
            RfPreviewType.Vhs or RfPreviewType.VhsHifi => PreviewSourceFormat.Vhs,
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
        var standard = type is RfPreviewType.SecamVhs or RfPreviewType.MesecamVhs
            ? PreviewVideoStandard.Secam : pal ? PreviewVideoStandard.Pal : PreviewVideoStandard.Ntsc;
        return new(source, standard, rate)
        {
            Region = PreviewRegion.FullRaster,
            IncludeSignalScopes = type != RfPreviewType.VhsHifi,
            DecodeThreads = Math.Min(4, Environment.ProcessorCount),
            Cvbs50Ohms = source == PreviewSourceFormat.Cvbs && cvbsRestore,
            BetamaxProfile = source != PreviewSourceFormat.Betamax ? BetamaxVideoProfile.Standard : betaProfile switch
            {
                1 => BetamaxVideoProfile.HiFi, 2 => BetamaxVideoProfile.SuperBeta,
                3 => BetamaxVideoProfile.SuperBetaHiFi, 4 => BetamaxVideoProfile.EdBeta,
                _ => BetamaxVideoProfile.Standard
            },
            AudioTrack = type switch
            {
                RfPreviewType.VhsHifi => PreviewAudioTrack.VhsHiFi,
                RfPreviewType.BetamaxHifi => PreviewAudioTrack.BetamaxHiFi,
                RfPreviewType.LaserDisc => ldTrack switch
                {
                    0 => PreviewAudioTrack.LaserDiscAnalog, 1 => PreviewAudioTrack.LaserDiscDigitalPcm,
                    2 => PreviewAudioTrack.LaserDiscDts, 3 => PreviewAudioTrack.LaserDiscAc3Rf,
                    _ => PreviewAudioTrack.None
                },
                _ => PreviewAudioTrack.None
            }
        };
    }
}
#endif
