using CaptureRack.DirectShow.Windows.Capture;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
        CameraControlsHeading.Text = L("DsCameraControls").ToUpperInvariant();
        CameraControlPickerLabel.Text = L("DsCameraProperty");
        AutomationProperties.SetName(CameraControlPicker, L("DsCameraProperty"));
        CameraControlChoicesLabel.Text = L("DsCameraChoice");
        AutomationProperties.SetName(CameraControlChoices, L("DsCameraChoice"));
        CameraControlAuto.Content = L("DsCameraAutomatic");
        ApplyCameraControlButton.Content = L("DsCameraApply");
        ResetCameraControlButton.Content = L("DsCameraResetDefault");
        CameraPendingBadgeText.Text = L("DsCameraPendingChanges");
        ToolTipService.SetToolTip(RefreshCameraControlsButton, L("DsRefresh"));
        AutomationProperties.SetName(RefreshCameraControlsButton, L("DsRefresh"));
        CameraControlsStatus.Text = L("DsCameraNoControls");
        if (CameraControlPicker.ItemsSource is CameraControlOption[] options)
            SetCameraControls(options.Select(option => option.Control), CameraControl?.Id);
        else
            UpdateCameraValueLabel();
    }

    private void ResetCameraControls()
    {
        cameraControlsLoaded = false;
        cameraControlsExpanded = false;
        CameraControlsBody.Visibility = Visibility.Collapsed;
        CameraControlsChevron.Glyph = "\uE70D";
        CameraControlPicker.ItemsSource = null;
        CameraPendingBadge.Visibility = Visibility.Collapsed;
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
            CameraControlAuto.IsChecked = IsControlCurrentlyAuto(control);
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
                CameraControlValue.StepFrequency = Math.Max(1, control.Step);
                CameraControlValue.Value = Math.Clamp(control.Value, control.Minimum, control.Maximum);
            }
            updatingCameraControls = false;
            UpdateCameraValueLabel();
        }
        UpdateCameraControlState();
    }

    private static bool IsControlCurrentlyAuto(DirectShowControl control) =>
        (control.SupportedModes & DirectShowControlMode.Manual) == 0 ||
        ((control.CurrentMode & DirectShowControlMode.Auto) != 0 && (control.SupportedModes & DirectShowControlMode.Auto) != 0);

    private int ReadEditedCameraValue(DirectShowControl control) =>
        CameraControlChoicesGroup.Visibility == Visibility.Visible
            ? (CameraControlChoices.SelectedItem as CameraValueOption)?.Value ?? control.Value
            : (int)Math.Round(CameraControlValue.Value);

    private void UpdateCameraControlState()
    {
        var control = CameraControl;
        var supported = control is not null;
        var auto = control?.SupportedModes.HasFlag(DirectShowControlMode.Auto) == true;
        var manual = control?.SupportedModes.HasFlag(DirectShowControlMode.Manual) == true;
        var canAccess = CanAccessCameraControls;
        var discrete = control is not null && (control.Choices is not null || IsBoolean(control));

        CameraControlsCard.Visibility = capabilities is not null ? Visibility.Visible : Visibility.Collapsed;
        CameraControlsStatus.Visibility = cameraControlsLoaded && !supported ? Visibility.Visible : Visibility.Collapsed;
        CameraControlPickerGroup.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        CameraControlAuto.Visibility = auto && manual ? Visibility.Visible : Visibility.Collapsed;
        CameraControlChoicesGroup.Visibility = manual && discrete ? Visibility.Visible : Visibility.Collapsed;
        CameraControlValueGroup.Visibility = manual && !discrete ? Visibility.Visible : Visibility.Collapsed;
        ApplyCameraControlButton.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;

        var editedIsAuto = CameraControlAuto.IsChecked == true;
        var editedValue = control is not null ? ReadEditedCameraValue(control) : 0;
        var hasPending = control is not null && CameraControlEditHelper.HasPendingChanges(
            IsControlCurrentlyAuto(control), control.Value, editedIsAuto, editedValue, auto && manual);
        var discreteDefaultAvailable = !discrete ||
            (CameraControlChoices.ItemsSource is CameraValueOption[] choiceList &&
             control?.DefaultValue is { } dv && choiceList.Any(option => option.Value == dv));
        var canReset = control is not null && discreteDefaultAvailable && CameraControlEditHelper.CanResetToDefault(
            control.DefaultValue, editedValue, editedIsAuto, manual);

        CameraPendingBadge.Visibility = hasPending ? Visibility.Visible : Visibility.Collapsed;
        ResetCameraControlButton.Visibility = supported && control?.DefaultValue is not null && discreteDefaultAvailable
            ? Visibility.Visible : Visibility.Collapsed;
        ResetCameraControlButton.IsEnabled = canAccess && canReset;

        CameraControlPicker.IsEnabled = canAccess;
        CameraControlAuto.IsEnabled = canAccess;
        CameraControlChoices.IsEnabled = canAccess && !editedIsAuto;
        CameraControlValue.IsEnabled = canAccess && !editedIsAuto && control?.Minimum != control?.Maximum;
        ApplyCameraControlButton.IsEnabled = canAccess;
        RefreshCameraControlsButton.IsEnabled = canAccess;

        ApplyCameraControlButton.Background = StudioBrushes.Resolve(this, hasPending ? "StudioAccentSoftBrush" : "StudioCardBrush");
        ApplyCameraControlButton.BorderBrush = StudioBrushes.Resolve(this, hasPending ? "StudioAccentBrush" : "StudioHairlineBrush");
        ApplyCameraControlButton.Foreground = StudioBrushes.Resolve(this, hasPending ? "StudioAccentBrush" : "StudioTextPrimaryBrush");
    }

    private static bool IsBoolean(DirectShowControl control) =>
        control.Id is "LowLightCompensation" or "BacklightCompensation" or "ColorEnable" &&
        control is { Minimum: 0, Maximum: 1, Step: 1 };

    private void UpdateCameraValueLabel()
    {
        if (CameraControl is not { } control)
            return;

        CameraControlValueLabel.Text = control.DefaultValue is { } defaultValue
            ? string.Format(L("DsCameraValue"), CameraControlValue.Value, defaultValue)
            : string.Format(L("DsCameraValueNoDefault"), CameraControlValue.Value);
        AutomationProperties.SetName(CameraControlValue, $"{L($"DsCamera{control.Id}")} · {CameraControlValueLabel.Text}");
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

    private async void CameraControlsToggle_Click(object sender, RoutedEventArgs e)
    {
        cameraControlsExpanded = !cameraControlsExpanded;
        CameraControlsBody.Visibility = cameraControlsExpanded ? Visibility.Visible : Visibility.Collapsed;
        CameraControlsChevron.Glyph = cameraControlsExpanded ? "\uE70E" : "\uE70D";
        if (cameraControlsExpanded)
        {
            Controls.StudioFeedback.RevealSubtle(CameraControlsBody, offsetY: 4f, durationMs: 120);
            if (!cameraControlsLoaded)
                await AccessCameraControlsAsync();
        }
    }

    private async void RefreshCameraControlsClicked(object sender, RoutedEventArgs e) => await AccessCameraControlsAsync();

    private async void ApplyCameraControlClicked(object sender, RoutedEventArgs e)
    {
        if (CameraControl is { } control)
        {
            var value = ReadEditedCameraValue(control);
            await AccessCameraControlsAsync(new(control.Id, value,
                CameraControlAuto.IsChecked == true ? DirectShowControlMode.Auto : DirectShowControlMode.Manual));
        }
    }

    private async void ResetCameraControlClicked(object sender, RoutedEventArgs e)
    {
        if (CameraControl is not { DefaultValue: int defaultValue } control)
            return;

        CameraValueOption? matchedChoice = null;
        int targetValue;
        if (CameraControlChoicesGroup.Visibility == Visibility.Visible &&
            CameraControlChoices.ItemsSource is CameraValueOption[] choices)
        {
            matchedChoice = choices.FirstOrDefault(option => option.Value == defaultValue);
            if (matchedChoice is null)
                return;
            targetValue = matchedChoice.Value;
        }
        else
        {
            targetValue = Math.Clamp(defaultValue, control.Minimum, control.Maximum);
        }

        updatingCameraControls = true;
        CameraControlAuto.IsChecked = false;
        if (matchedChoice is not null)
            CameraControlChoices.SelectedItem = matchedChoice;
        else
            CameraControlValue.Value = targetValue;
        updatingCameraControls = false;
        UpdateCameraValueLabel();
        UpdateCameraControlState();
        await AccessCameraControlsAsync(new(control.Id, targetValue, DirectShowControlMode.Manual));
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

    private void CameraControlChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingCameraControls)
            UpdateCameraControlState();
    }

    private void CameraControlValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!updatingCameraControls)
        {
            UpdateCameraValueLabel();
            UpdateCameraControlState();
        }
    }

    public sealed record CameraControlOption(DirectShowControl Control, string Label);
    public sealed record CameraValueOption(int Value, string Label);
}
