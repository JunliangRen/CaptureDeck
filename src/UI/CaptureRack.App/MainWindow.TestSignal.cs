using CaptureRack.DeckLink;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CaptureRack.App;

public sealed partial class MainWindow
{
    private TestSignalScenario SelectedTestScenario => TestSignalScenarioPicker.SelectedIndex == 1
        ? TestSignalScenario.TimingFaults : TestSignalScenario.Normal;

    private string TestSignalScenarioKey => SelectedTestScenario == TestSignalScenario.TimingFaults
        ? "TestSignalFaults" : "TestSignalNormal";

    private string GetRecordingLabel()
    {
        var label = RecordingFileNaming.SanitizeLabel(RecordingLabelBox.Text);
        return !IsTestSignalActive ? label : label.Length == 0 ? "TestSignal" : $"TestSignal_{label}";
    }

    private void ApplyTestSignalLanguage()
    {
        TestSignalHeading.Text = L("TestSignalScenario");
        AutomationProperties.SetName(TestSignalScenarioPicker, TestSignalHeading.Text);
        TestSignalNormalItem.Content = L("TestSignalNormal");
        TestSignalFaultsItem.Content = L("TestSignalFaults");
        TestSignalHint.Text = L("TestSignalHint");
    }

    private void TestSignalScenario_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (settingsReady && IsTestSignalActive)
            UpdateConfigurationStatus();
    }

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
            Routing = new CaptureRouting(deckLinkInspector.SelectedVideoSource!.Id, deckLinkInspector.SelectedAudioSource!.Id),
            Preview = feed,
            PixelFormat = RecordingVideoFormat,
            AudioFormat = RecordingAudioFormat,
            PreviewRecorder = recorder,
            AnalogOptions = deckLinkInspector.BuildAnalogInputOptions(),
            LocalAudioMonitor = audioMonitor
        };
        return () => DeckLink.Run(request, progress, cancellation);
    }
}
