using CaptureRack.DeckLink;

namespace CaptureRack.App;

public sealed partial class MainWindow
{
    private TestSignalScenario SelectedTestScenario => testSignalInspector.SelectedScenario;

    private string TestSignalScenarioKey => SelectedTestScenario switch
    {
        TestSignalScenario.TimingFaults => "TestSignalFaults",
        TestSignalScenario.MultiChannel => "TestSignalMultiChannel",
        _ => "TestSignalNormal"
    };

    private uint ActiveDeckLinkAudioChannels => IsTestSignalActive
        ? (SelectedTestScenario == TestSignalScenario.MultiChannel ? 8u : CaptureAudio.Channels)
        : deckLinkInspector.SelectedAudioChannels;

    private string GetRecordingLabel()
    {
        var label = RecordingFileNaming.SanitizeLabel(RecordingLabelBox.Text);
        return !IsTestSignalActive ? label : label.Length == 0 ? "TestSignal" : $"TestSignal_{label}";
    }

    private void ApplyTestSignalLanguage() =>
        testSignalInspector.ApplyLanguage(L);

    private Func<CaptureStats> CreatePreviewRunner(PreviewFeed feed, PreviewRecorder recorder,
        LocalAudioMonitor audioMonitor, Action<DeckLinkProgress> progress, CancellationToken cancellation)
    {
        if (IsTestSignalActive)
        {
            var scenario = SelectedTestScenario;
            return () => TestSignal.Run(scenario, feed, recorder, audioMonitor, progress, cancellation);
        }

        var request = new DeckLinkCaptureRequest(deckLinkInspector.SelectedDevice!.Index,
            deckLinkInspector.SelectedMode!.Id, Duration: null)
        {
            Routing = new CaptureRouting(deckLinkInspector.SelectedVideoSource!.Id, deckLinkInspector.SelectedAudioSource!.Id)
            {
                VancInput = deckLinkInspector.CanMapVancInput ? deckLinkInspector.VancInput : null
            },
            Preview = feed,
            PixelFormat = RecordingVideoFormat,
            AudioFormat = RecordingAudioFormat,
            AudioChannels = deckLinkInspector.SelectedAudioChannels,
            PreviewRecorder = recorder,
            AnalogOptions = deckLinkInspector.BuildAnalogInputOptions(),
            LocalAudioMonitor = audioMonitor
        };
        return () => DeckLink.Run(request, progress, cancellation);
    }
}
