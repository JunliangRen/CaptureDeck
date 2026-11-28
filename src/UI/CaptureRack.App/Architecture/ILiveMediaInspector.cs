namespace CaptureRack.App;

internal interface ILiveMediaInspector
{
    bool HasOpenDropdown { get; }
    string? BuildFilenamePreview(string label);
    void NotifyOutputFolderChanged();
}
