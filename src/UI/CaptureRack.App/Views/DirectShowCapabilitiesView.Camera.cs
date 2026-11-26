using CaptureRack.DirectShow.Windows.Capture;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace CaptureRack.App.Views;

public sealed partial class DirectShowCapabilitiesView
{
    private bool cameraControlsLoaded;
    private bool updatingCameraControls;

    private DirectShowControl? CameraControl => (CameraControlPicker.SelectedItem as CameraControlOption)?.Control;
    private bool CanAccessCameraControls => capabilities is not null && operation is null && !IsStopping && !IsRecording;

    private void BindCameraControls()
    {
        CameraControlsSection.Header = L("DsCameraControls");
        CameraControlPicker.Header = L("DsCameraProperty");
        CameraControlChoices.Header = L("DsCameraChoice");
        CameraControlAuto.Content = L("DsCameraAutomatic");
        ApplyCameraControlButton.Content = L("DsCameraApply");
        RefreshCameraControlsButton.Content = L("DsRefresh");
        CameraControlsStatus.Text = L("DsCameraNoControls");
        if (CameraControlPicker.ItemsSource is CameraControlOption[] options)
            SetCameraControls(options.Select(option => option.Control), CameraControl?.Id);
    }

    private void ResetCameraControls()
    {
        cameraControlsLoaded = false;
        CameraControlsSection.IsExpanded = false;
        CameraControlPicker.ItemsSource = null;
    }

    private void SetCameraControls(IEnumerable<DirectShowControl> controls, string? selected)
    {
        var options = controls.Select(control => new CameraControlOption(control, L($"DsCamera{control.Id}"))).ToArray();
        updatingCameraControls = true;
        CameraControlPicker.ItemsSource = options;
        CameraControlPicker.SelectedItem = options.FirstOrDefault(option => option.Control.Id == selected) ?? options.FirstOrDefault();
        updatingCameraControls = false;
        LoadCameraControl();
    }

    private void LoadCameraControl()
    {
        if (CameraControl is { } control)
        {
            updatingCameraControls = true;
            CameraControlAuto.IsChecked = (control.SupportedModes & DirectShowControlMode.Manual) == 0 ||
                (control.CurrentMode & DirectShowControlMode.Auto) != 0 && (control.SupportedModes & DirectShowControlMode.Auto) != 0;
            var choices = control.Choices?.Select(value => new CameraValueOption(value, $"{value}°")).ToArray();
            if (choices is null && IsBoolean(control))
                choices = [new(0, L("DsCameraOff")), new(1, L("DsCameraOn"))];
            CameraControlChoices.ItemsSource = choices;
            CameraControlChoices.SelectedItem = choices?.FirstOrDefault(option => option.Value == control.Value);
            if (choices is null)
            {
                CameraControlValue.Minimum = Math.Min(CameraControlValue.Minimum, control.Minimum);
                CameraControlValue.Maximum = control.Maximum;
                CameraControlValue.Minimum = control.Minimum;
                CameraControlValue.StepFrequency = control.Step;
                CameraControlValue.Value = Math.Clamp(control.Value, control.Minimum, control.Maximum);
            }
            updatingCameraControls = false;
            UpdateCameraValueLabel();
        }
        UpdateCameraControlState();
    }

    private void UpdateCameraControlState()
    {
        var control = CameraControl;
        var supported = control is not null;
        var auto = control?.SupportedModes.HasFlag(DirectShowControlMode.Auto) == true;
        var manual = control?.SupportedModes.HasFlag(DirectShowControlMode.Manual) == true;
        var canAccess = CanAccessCameraControls;
        var discrete = control is not null && (control.Choices is not null || IsBoolean(control));
        CameraControlsSection.Visibility = capabilities is not null ? Visibility.Visible : Visibility.Collapsed;
        CameraControlsStatus.Visibility = cameraControlsLoaded && !supported ? Visibility.Visible : Visibility.Collapsed;
        CameraControlPicker.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        CameraControlAuto.Visibility = auto && manual ? Visibility.Visible : Visibility.Collapsed;
        CameraControlChoices.Visibility = manual && discrete ? Visibility.Visible : Visibility.Collapsed;
        CameraControlValue.Visibility = manual && !discrete ? Visibility.Visible : Visibility.Collapsed;
        ApplyCameraControlButton.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        CameraControlPicker.IsEnabled = canAccess;
        CameraControlAuto.IsEnabled = canAccess;
        CameraControlChoices.IsEnabled = canAccess && CameraControlAuto.IsChecked != true;
        CameraControlValue.IsEnabled = canAccess && CameraControlAuto.IsChecked != true && control?.Minimum != control?.Maximum;
        ApplyCameraControlButton.IsEnabled = canAccess;
        RefreshCameraControlsButton.IsEnabled = canAccess;
    }

    private static bool IsBoolean(DirectShowControl control) =>
        control.Id is "LowLightCompensation" or "BacklightCompensation" or "ColorEnable" &&
        control is { Minimum: 0, Maximum: 1, Step: 1 };

    private void UpdateCameraValueLabel()
    {
        if (CameraControl?.DefaultValue is { } value)
            CameraControlValue.Header = string.Format(L("DsCameraValue"), CameraControlValue.Value, value);
    }

    private async Task AccessCameraControlsAsync(DirectShowControlChange? change = null)
    {
        if (!CanAccessCameraControls || capabilities?.Device is not { } device)
            return;
        var selected = CameraControl?.Id;
        await RunAsync(async token =>
        {
            var controls = preview is { } live
                ? await live.AccessCameraControlsAsync(change, token)
                : await DirectShowInspectionClient.RunAsync<DirectShowControl[]>(
                    new(DirectShowInspectionOperation.Controls, device.Id, ControlChange: change), token);
            token.ThrowIfCancellationRequested();
            cameraControlsLoaded = true;
            SetCameraControls(controls, selected);
        }, allowPreview: true);
    }

    private async void CameraControlsExpanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        if (!cameraControlsLoaded)
            await AccessCameraControlsAsync();
    }

    private async void RefreshCameraControlsClicked(object sender, RoutedEventArgs e) => await AccessCameraControlsAsync();

    private async void ApplyCameraControlClicked(object sender, RoutedEventArgs e)
    {
        if (CameraControl is { } control)
        {
            var value = CameraControlChoices.Visibility == Visibility.Visible
                ? (CameraControlChoices.SelectedItem as CameraValueOption)?.Value ?? control.Value
                : (int)CameraControlValue.Value;
            await AccessCameraControlsAsync(new(control.Id, value,
                CameraControlAuto.IsChecked == true ? DirectShowControlMode.Auto : DirectShowControlMode.Manual));
        }
    }

    private void CameraControlChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingCameraControls)
            LoadCameraControl();
    }

    private void CameraControlAutoChanged(object sender, RoutedEventArgs e)
    {
        if (!updatingCameraControls)
            UpdateCameraControlState();
    }

    private void CameraControlValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!updatingCameraControls)
            UpdateCameraValueLabel();
    }

    public sealed record CameraControlOption(DirectShowControl Control, string Label);
    public sealed record CameraValueOption(int Value, string Label);
}
