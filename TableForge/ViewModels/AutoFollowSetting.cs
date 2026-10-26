namespace TableForge.ViewModels;

/// <summary>
/// "Auto-follow linked tables": one setting for the whole app, shown (and changed) on every Roll screen and remembered between
/// sessions. Off by default; off is exactly the behavior Gidubba always had. Turning it off during a chain stops the chain after
/// the roll already in the air.
/// </summary>
/// <param name="save">Remembers a change. A failure to save never gets in the way: the setting still applies until Gidubba closes.</param>
public sealed class AutoFollowSetting(bool isOn = false, Action<bool>? save = null) : ObservableObject
{
    private bool _isOn = isOn;

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (!Set(ref _isOn, value)) return;
            try { save?.Invoke(value); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        }
    }
}
