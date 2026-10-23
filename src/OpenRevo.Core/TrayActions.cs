namespace OpenRevo.Core;

// Integer-only desktop IPC; keep IDs in sync with Background/tray-menu.h.
public enum TrayAction : uint
{
    Overview = 1, Performance = 3, Settings = 4,
    Office = 10, Balanced = 11, Turbo = 12, Custom = 13,
    FanBoost = 14, FanAuto = 15, Restore = 16, Keep = 17,
    Refresh = 18, Diagnostics = 19, Updates = 20
}

public static class TrayActions
{
    public static bool TryParse(string[] arguments, out TrayAction action)
    {
        action = default;
        if (arguments.Length != 2 || arguments[0] != "--tray-action"
            || !uint.TryParse(arguments[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var value) || !Enum.IsDefined((TrayAction)value)) return false;
        action = (TrayAction)value;
        return true;
    }

    public static bool IsControl(TrayAction action) => action is >= TrayAction.Office and <= TrayAction.Keep;
    public static uint Bit(TrayAction action) => Enum.IsDefined(action) ? 1u << (int)action : 0;
    public static uint State(ModePilot? pilot, bool connected)
    {
        if (!connected || pilot is null) return 0;
        uint state = Bit(TrayAction.Refresh);
        for (var mode = 0; mode < 4; mode++)
            if (pilot.CanSwitchTo((PilotMode)mode)) state |= Bit((TrayAction)(10 + mode));
        if (pilot.CanControlFan)
            state |= Bit(pilot.FanBoostEnabled == true ? TrayAction.FanAuto : TrayAction.FanBoost);
        if (pilot.CanRestore) state |= Bit(TrayAction.Restore);
        if (pilot.CanKeep) state |= Bit(TrayAction.Keep);
        if (int.TryParse(pilot.CurrentMode, out var current) && current is >= 0 and < 4) state |= (uint)(current + 1) << 24;
        if (pilot.FanBoostEnabled is bool boost) state |= 1u << (boost ? 29 : 28);
        return state;
    }
}
