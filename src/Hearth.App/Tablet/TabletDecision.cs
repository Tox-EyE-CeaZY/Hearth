using Hearth.Core.Settings;

namespace Hearth.App.Tablet;

/// <summary>How one attached device was counted, for the settings screen.</summary>
internal sealed record DeviceVerdict(PeripheralDevice Device, DeviceRule Rule, bool Counts, string Why);

/// <summary>What Auto mode (or the fixed setting) wants, and why, in words the user can check.</summary>
internal sealed record TabletDecision(
    bool Tablet,
    string Reason,
    IReadOnlyList<DeviceVerdict> Devices,
    MachineInfo Machine,
    bool SensorTrusted,
    string Fingerprint)
{
    /// <summary>
    /// The rules, kept free of Windows calls so they can be checked against
    /// made-up hardware (a detachable with its cover off, a convertible folded
    /// back) on a machine that is neither.
    /// </summary>
    public static TabletDecision Decide(
        TabletSettings settings,
        MachineInfo machine,
        IReadOnlyList<PeripheralDevice> devices,
        bool sensorSeenChanging)
    {
        var verdicts = devices.Select(d => Judge(settings, machine, d)).ToList();
        var counted = verdicts.Where(v => v.Counts).ToList();

        var sensorTrusted = settings.SlateSensor switch
        {
            SlateSensorUse.Always => true,
            SlateSensorUse.Never => false,
            _ => machine.LooksLikeTablet || sensorSeenChanging,
        };

        var fingerprint = string.Join("|",
            settings.Mode,
            machine.HasTouchscreen,
            sensorTrusted && machine.SlateSensorSaysSlate,
            string.Join(",", counted.Select(v => v.Device.Key).Order(StringComparer.Ordinal)));

        TabletDecision Result(bool tablet, string reason) =>
            new(tablet, reason, verdicts, machine, sensorTrusted, fingerprint);

        switch (settings.Mode)
        {
            case TabletModeSetting.Off:
                return Result(false, "Tablet mode is set to Off.");
            case TabletModeSetting.On:
                return Result(true, "Tablet mode is set to On.");
        }

        if (settings.RequireTouchscreen && !machine.HasTouchscreen)
            return Result(false, "No touchscreen on this machine, so Auto stays in desktop mode.");

        // Something plugged in means desk use, whatever the hinge says.
        if (counted.FirstOrDefault(v => v.Device.IsKeyboard && !v.Device.Internal) is { } external)
            return Result(false, $"{external.Device.Name} is attached.");
        if (counted.FirstOrDefault(v => v.Device.IsPointer && !v.Device.Internal) is { } externalPointer)
            return Result(false, $"{externalPointer.Device.Name} is attached.");

        if (sensorTrusted && machine.SlateSensorSaysSlate)
            return Result(true, "The slate sensor says the keyboard is folded back or detached.");

        if (counted.FirstOrDefault(v => v.Device.IsKeyboard) is { } keyboard)
            return Result(false, $"{keyboard.Device.Name} is attached.");

        if (counted.FirstOrDefault(v => v.Device.IsPointer) is { } pointer)
            return Result(false, $"{pointer.Device.Name} is attached.");

        // "Laptop" only means something on hardware with a hinge or a
        // removable keyboard; a plain tablet has no sensor and reads that way.
        var laptopReadingMeaningful = settings.SlateSensor == SlateSensorUse.Always ||
                                      (sensorTrusted && (!machine.IsPureTablet || sensorSeenChanging));
        if (laptopReadingMeaningful && !machine.SlateSensorSaysSlate)
            return Result(false, "The slate sensor says this is in laptop posture.");

        return Result(true, "No keyboard or mouse is attached.");
    }

    private static DeviceVerdict Judge(TabletSettings settings, MachineInfo machine, PeripheralDevice device)
    {
        var rule = settings.Devices.TryGetValue(device.Key, out var chosen) ? chosen : DeviceRule.Auto;
        switch (rule)
        {
            case DeviceRule.Counts:
                return new DeviceVerdict(device, rule, true, "You set this to count.");
            case DeviceRule.Ignore:
                return new DeviceVerdict(device, rule, false, "You set this to be ignored.");
        }

        if (device.Internal && machine.IsConvertible)
            return new DeviceVerdict(device, rule, false, "Built into a convertible, which keeps it when folded; the slate sensor decides instead.");

        if (device.Internal && machine.IsPureTablet)
            return new DeviceVerdict(device, rule, false, "Built into a tablet; most likely its hardware buttons.");

        if (device.IsKeyboard)
            return new DeviceVerdict(device, rule, true, device.Internal ? "A built-in keyboard; on a detachable it leaves with the keyboard." : "An external keyboard.");

        if (!settings.MouseMeansDesktop)
            return new DeviceVerdict(device, rule, false, "Mice and touchpads don't count (setting).");

        return new DeviceVerdict(device, rule, true, device.Internal ? "A built-in pointing device." : "An external pointing device.");
    }
}
