#if REALTIME_RF
using CaptureRack.App;
using CaptureRack.DeckLink;
using CaptureRack.SignalCapture;
using VHSDecode.Preview;
using Xunit;
using Xunit.Abstractions;

namespace CaptureRack.UT;

public sealed class RfPackagedPreviewTests(ITestOutputHelper output)
{
    [Fact]
    public async Task HostOpensEveryDistinctPackagedMode()
    {
        var tested = new HashSet<RealtimePreviewCombination>();
        foreach (var combo in RealtimePreviewCapabilities.SupportedCombinations)
        {
            // SECAM also accepts the legacy PAL spelling; standard+HiFi is an alias for HiFi.
            if ((combo.SourceFormat is PreviewSourceFormat.SecamVhs or PreviewSourceFormat.MesecamVhs &&
                 combo.Standard == PreviewVideoStandard.Pal) ||
                (combo.SourceFormat == PreviewSourceFormat.Betamax && combo.BetamaxProfile == BetamaxVideoProfile.Standard &&
                 combo.AudioTrack == PreviewAudioTrack.BetamaxHiFi)) continue;

            var type = combo.SourceFormat switch
            {
                PreviewSourceFormat.Vhs => combo.HasVideo ? RfPreviewType.Vhs : RfPreviewType.VhsHifi,
                PreviewSourceFormat.Cvbs => RfPreviewType.VideoOnly,
                PreviewSourceFormat.LaserDisc => RfPreviewType.LaserDisc,
                PreviewSourceFormat.Betamax => combo.AudioTrack == PreviewAudioTrack.None ? RfPreviewType.Betamax : RfPreviewType.BetamaxHifi,
                PreviewSourceFormat.SVhs => RfPreviewType.SVhs,
                PreviewSourceFormat.SecamVhs => RfPreviewType.SecamVhs,
                PreviewSourceFormat.MesecamVhs => RfPreviewType.MesecamVhs,
                _ => throw new NotSupportedException(combo.ToString())
            };
            int track = combo.AudioTrack switch
            {
                PreviewAudioTrack.LaserDiscAnalog => 0, PreviewAudioTrack.LaserDiscDigitalPcm => 1,
                PreviewAudioTrack.LaserDiscDts => 2, PreviewAudioTrack.LaserDiscAc3Rf => 3, _ => 4
            };
            var options = RfDecoderOptions.Create(type, combo.InputSampleRateHz, combo.Standard == PreviewVideoStandard.Pal,
                true, (int)combo.BetamaxProfile, track);
            Assert.Equal(combo, new RealtimePreviewCombination(options.SourceFormat, options.Standard,
                options.InputSampleRateHz, options.BetamaxProfile, options.AudioTrack));
            Assert.Equal(combo.HasVideo, options.IncludeSignalScopes);
            Assert.Equal(combo.SupportsCvbs50Ohms, options.Cvbs50Ohms);
            Assert.True(RealtimePreviewCapabilities.IsSupported(options));
            var audio = new RfPreviewAudio();
            var decoder = new RfPreviewDecoder(options, audio) { FullRaster = true, MeasureSwitch = false, FieldMode = 0 };
            decoder.Offer(new byte[8192], 8192, 0);
            await decoder.StopAsync(drain: true);
            Assert.Null(decoder.Error);
            Assert.False(audio.Read().Locked);
            tested.Add(combo);
        }
        output.WriteLine($"Opened {tested.Count} distinct source/standard/rate/profile/track combinations.");
    }

    private sealed class MediaTheoryAttribute : TheoryAttribute
    {
        public MediaTheoryAttribute(string variable)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
                Skip = $"Set {variable} to the capture fixture directory.";
        }
    }

    [MediaTheory("CAPTURERACK_SECAM_FIXTURES")]
    [InlineData("sec.s16", false)]
    [InlineData("mes.s16", true)]
    public async Task SecamCassetteFilesProduceColourAndFullRasterFields(string name, bool mesecam)
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("CAPTURERACK_SECAM_FIXTURES")!, name);
        var file = RfPlaybackFile.Open(path, null) with { Rate = 20_000_000 };
        var type = mesecam ? RfPreviewType.MesecamVhs : RfPreviewType.SecamVhs;
        var decoder = new RfPreviewDecoder(RfDecoderOptions.Create(type, file.Rate, false, false, 0, 0), null)
        {
            FullRaster = true, FieldMode = 2, MeasureSwitch = true
        };
        PreviewFrame? frame = null;
        int colours = 0, pairs = 0;
        var previous = TimeSpan.MinValue;
        try
        {
            await Task.Run(() => file.Play(0, null, _ => { }, (data, length, first) =>
            {
                decoder.Offer(data, length, first);
                if (!decoder.ScopeFeed.TryTakeLatest(ref frame)) return;
                var field = Assert.IsType<RfPreviewDecoder.FieldPair>(frame!.SourceTag).Top;
                Assert.True(field.Timestamp > previous);
                previous = field.Timestamp;
                Assert.Equal(50, field.FieldsPerSecond);
                Assert.Equal((field.Width, field.Height), (frame.Width, frame.Height));
                var yuv = field.SignalYuv.Span;
                Assert.Equal(field.Width * field.Height * 3, yuv.Length);
                for (int pixel = 0; pixel < field.Width * field.Height; pixel += 53)
                    if (Math.Abs((int)yuv[pixel * 3 + 1]) > 80 || Math.Abs((int)yuv[pixel * 3 + 2]) > 80)
                    {
                        colours++;
                        break;
                    }
                pairs++;
            }, CancellationToken.None));
        }
        finally { await decoder.StopAsync(drain: true); }
        Assert.Null(decoder.Error);
        output.WriteLine($"{type}: {pairs} advancing pairs; {colours} with chroma.");
        Assert.True(pairs > 3 && colours > 3);
    }

    [MediaTheory("CAPTURERACK_PAL_LD_FIXTURES")]
    [InlineData("roger.s16", 40000000)]
    [InlineData("roger20.s16", 20000000)]
    public async Task PalDigitalDiscReacquiresPictureSignalPlanesAndStereoPcm(string name, int rate)
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("CAPTURERACK_PAL_LD_FIXTURES")!, name);
        var file = RfPlaybackFile.Open(path, null) with { Rate = rate };
        var options = RfDecoderOptions.Create(RfPreviewType.LaserDisc, rate, true, false, 0, 1);
        var audio = new RfPreviewAudio();
        var decoder = new RfPreviewDecoder(options, audio, "LD PCM");
        decoder.ScopeFeed.DeinterlaceMode = PreviewDeinterlaceMode.Off;
        PreviewFrame? frame = null;
        long offset = 0;
        var previous = TimeSpan.MinValue;
        async Task Replay()
        {
            int displayed = 0;
            bool pcm = false, locked = false, signal = false;
            await Task.Run(() => file.Play(0, null, _ => { }, (bytes, length, index) =>
            {
                decoder.Offer(bytes, length, offset + index);
                if (decoder.Locks is { HorizontalLocked: true, VerticalLocked: true, ColorLocked: true, AudioLocked: true })
                    locked = true;
                if (audio.Read() is { Locked: true, Levels: { SampleRate: 44100, ChannelCount: 2, LeftPeak: > 0, RightPeak: > 0 } })
                    pcm = true;
                if (!decoder.ScopeFeed.TryTakeLatest(ref frame)) return;
                var pair = Assert.IsType<RfPreviewDecoder.FieldPair>(frame!.SourceTag);
                var field = pair.Second;
                Assert.True(field.Timestamp > previous);
                previous = field.Timestamp;
                Assert.Equal(field.Width * field.Height * 3, field.SignalYuv.Length);
                signal |= field.SignalYuv.Span.ContainsAnyExcept((short)0);
                displayed++;
            }, CancellationToken.None));
            output.WriteLine($"{rate} Hz: {displayed} advancing field pairs; signal={signal}, lock={locked}, stereo PCM={pcm}.");
            Assert.True(displayed >= 3 && signal && locked && pcm);
            offset += file.Samples;
        }

        try
        {
            await Replay();
            var silence = new byte[rate / 500]; // One millisecond of signed 16-bit RF.
            for (int i = 0; i < 250; i++)
            {
                decoder.Offer(silence, silence.Length, offset);
                offset += silence.Length / 2;
                await Task.Delay(1);
            }
            Assert.True(SpinWait.SpinUntil(() => decoder.Locks is { AudioLocked: false, VerticalLocked: false }
                && !audio.Read().Locked, TimeSpan.FromSeconds(5)));
            await Replay();
        }
        finally { await decoder.StopAsync(drain: true); }
        Assert.Null(decoder.Error);
    }
}
#endif
