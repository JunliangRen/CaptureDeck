using CaptureRack.DeckLink;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CaptureRack.App.Views;

public sealed partial class TestSignalInspectorView : UserControl
{
    private bool syncing;

    public TestSignalScenario SelectedScenario => ScenarioPicker.SelectedIndex == 1
        ? TestSignalScenario.TimingFaults : TestSignalScenario.Normal;

    public string SelectedScenarioTag => SelectedScenario == TestSignalScenario.TimingFaults
        ? "TimingFaults" : "Normal";

    public bool HasOpenDropdown => ScenarioPicker.IsDropDownOpen;

    public event EventHandler? ScenarioChanged;

    public TestSignalInspectorView() => InitializeComponent();

    public void RestoreScenario(string? scenarioTag)
    {
        syncing = true;
        try
        {
            ScenarioPicker.SelectedIndex = string.Equals(scenarioTag, "TimingFaults", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }
        finally
        {
            syncing = false;
        }
    }

    public void SetEnabled(bool enabled) => ScenarioPicker.IsEnabled = enabled;

    public void ApplyLanguage(Func<string, string> localize)
    {
        var currentIndex = ScenarioPicker.SelectedIndex;
        syncing = true;
        try
        {
            SectionHeading.Text = localize("TestSignalSectionHeading");
            ScenarioLabel.Text = localize("TestSignalScenario");
            AutomationProperties.SetName(ScenarioPicker, ScenarioLabel.Text);
            NormalItem.Content = localize("TestSignalNormal");
            FaultsItem.Content = localize("TestSignalFaults");
            ScenarioHint.Text = localize("TestSignalHint");
            ScenarioPicker.SelectedIndex = -1;
            ScenarioPicker.SelectedIndex = currentIndex < 0 ? 0 : currentIndex;
        }
        finally
        {
            syncing = false;
        }
    }

    private void ScenarioPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!syncing)
            ScenarioChanged?.Invoke(this, EventArgs.Empty);
    }
}
