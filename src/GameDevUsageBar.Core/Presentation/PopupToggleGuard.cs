namespace GameDevUsageBar.Core.Presentation;

// Correlate an outside-dismiss and tray mouse-down with the OS input timestamp.
// No fixed timeout: a long hold and a distinct rapid second click remain distinct.
public sealed class PopupToggleGuard
{
    private uint? dismissToken;
    private bool? closeGesture;
    public void Deactivated(uint inputToken, bool pressedOnTray)
    {
        dismissToken = pressedOnTray ? inputToken : null;
    }
    public void PointerDown(uint inputToken, bool visible)
    {
        closeGesture = visible || dismissToken == inputToken;
        dismissToken = null;
    }
    public bool ShouldOpenOnClick(bool visible)
    {
        var close = closeGesture ?? visible;
        closeGesture = null; dismissToken = null;
        return !close;
    }
    public void Reset() { dismissToken = null; closeGesture = null; }
}
