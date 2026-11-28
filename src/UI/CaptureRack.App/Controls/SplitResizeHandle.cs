using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace CaptureRack.App.Controls;

public sealed partial class SplitResizeHandle : Grid
{
    public SplitResizeHandle()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
    }
}
