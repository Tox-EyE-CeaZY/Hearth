using System.Runtime.InteropServices;
using Hearth.Core.Settings;

namespace Hearth.App.Tablet;

[Flags]
internal enum PeripheralKind
{
    None = 0,
    Keyboard = 1,
    Mouse = 2,
    Touchpad = 4,
}

/// <summary>
/// One physical input device (a keyboard, a mouse, a touchpad), however many
/// interfaces it exposes. A Razer mouse, for example, is one USB device with
/// a mouse interface and two keyboard interfaces for its macro keys.
/// </summary>
internal sealed record PeripheralDevice(
    string Key,
    string Name,
    PeripheralKind Kinds,
    bool Internal,
    string Detail)
{
    public bool IsKeyboard => Kinds.HasFlag(PeripheralKind.Keyboard);
    public bool IsPointer => (Kinds & (PeripheralKind.Mouse | PeripheralKind.Touchpad)) != 0;

    public string KindText => Kinds switch
    {
        PeripheralKind.Keyboard => "Keyboard",
        PeripheralKind.Mouse => "Mouse",
        PeripheralKind.Touchpad => "Touchpad",
        PeripheralKind.Keyboard | PeripheralKind.Touchpad => "Keyboard with touchpad",
        PeripheralKind.Keyboard | PeripheralKind.Mouse => "Keyboard and mouse",
        _ => "Input device",
    };
}

/// <summary>What the machine says about itself, read once per evaluation.</summary>
internal sealed record MachineInfo(
    bool HasTouchscreen,
    int MaxTouches,
    bool SlateSensorSaysSlate,
    int ChassisType,
    int PlatformRole)
{
    /// <summary>SMBIOS 30 Tablet, 31 Convertible, 32 Detachable; power role 8 is Slate.</summary>
    public bool LooksLikeTablet => ChassisType is 30 or 31 or 32 || PlatformRole == 8;

    /// <summary>A 2-in-1 whose keyboard folds back rather than coming off.</summary>
    public bool IsConvertible => ChassisType == 31;

    public bool IsPureTablet => ChassisType == 30 || (PlatformRole == 8 && ChassisType is not (31 or 32));

    public string ChassisText => ChassisType switch
    {
        3 or 4 or 6 or 7 => "desktop",
        8 or 9 or 10 or 14 => "laptop",
        13 => "all-in-one",
        30 => "tablet",
        31 => "convertible",
        32 => "detachable",
        0 => "unknown",
        _ => $"type {ChassisType}",
    };
}

/// <summary>
/// Lists the keyboards, mice and touchpads Windows currently has, grouped into
/// physical devices, and reads the machine facts Auto mode needs.
///
/// Everything here is SetupAPI and the configuration manager: no WMI, no
/// WinRT (whose first use costs seconds; see notes), no NuGet.
/// </summary>
internal static class Peripherals
{
    private static readonly Guid KeyboardInterface = new("884b96c3-56ef-11d1-bc8c-00a0c91405dd");
    private static readonly Guid MouseInterface = new("378de44c-56ef-11d1-bc8c-00a0c91405dd");

    /// <summary>Every present device, sorted with external ones first.</summary>
    public static List<PeripheralDevice> Enumerate()
    {
        var groups = new Dictionary<string, Builder>(StringComparer.OrdinalIgnoreCase);
        Collect(KeyboardInterface, PeripheralKind.Keyboard, groups);
        Collect(MouseInterface, PeripheralKind.Mouse, groups);

        return groups.Values
            .Select(b => b.Build())
            .Where(d => d.Kinds != PeripheralKind.None)
            .OrderBy(d => d.Internal)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed class Builder(string key, string name, bool isInternal)
    {
        public readonly string Key = key;
        public readonly string Name = name;
        public readonly bool Internal = isInternal;
        public PeripheralKind Kinds;
        public bool FirstInterfaceIsBootMouse;
        public readonly List<string> Notes = [];

        public PeripheralDevice Build()
        {
            var kinds = Kinds;
            // A USB device whose first interface is a boot mouse is a mouse;
            // its "keyboards" are macro buttons. Gaming mice declare boot
            // keyboard interfaces too (the Razer here does), so only the
            // first interface tells: it is the keyboard on a keyboard.
            if (FirstInterfaceIsBootMouse && kinds.HasFlag(PeripheralKind.Keyboard))
            {
                kinds &= ~PeripheralKind.Keyboard;
                Notes.Add("keyboard interfaces treated as mouse buttons");
            }
            return new PeripheralDevice(Key, Name, kinds, Internal, string.Join("; ", Notes.Distinct()));
        }
    }

    private static void Collect(Guid interfaceClass, PeripheralKind kind, Dictionary<string, Builder> groups)
    {
        var set = SetupDiGetClassDevs(ref interfaceClass, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == INVALID_HANDLE_VALUE) return;
        try
        {
            var info = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
            {
                try { Add(info.DevInst, kind, groups); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    Hearth.Core.Diagnostics.Log.Write($"tablet: skipped an input device: {ex.Message}");
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static void Add(uint node, PeripheralKind kind, Dictionary<string, Builder> groups)
    {
        // Walk up to the first node that is not a HID collection or HID
        // transport: that is the hardware (a USB interface, an I2C device, a
        // Bluetooth link, ACPI for a PS/2 keyboard).
        var chain = new List<uint> { node };
        for (var current = node; CM_Get_Parent(out var parent, current, 0) == 0 && parent != 0; current = parent)
        {
            chain.Add(parent);
            if (chain.Count > 12) break;
        }
        var ids = chain.Select(DeviceId).ToList();
        var hardwareIndex = ids.FindIndex(i => !IsHidNode(i));
        if (hardwareIndex < 0) hardwareIndex = ids.Count - 1;

        // Only the device's own nodes: every tree passes through ROOT\ACPI_HAL.
        if (IsVirtual(ids.Take(hardwareIndex + 1))) return;

        var isInternal = InLocalMachineContainer(node);

        // Button arrays are always built in; an external keyboard is never one.
        if (kind == PeripheralKind.Keyboard && isInternal &&
            LooksLikeButtons(chain.Select(n => (Name(n), Service(n))).ToList()))
        {
            return;
        }

        if (kind == PeripheralKind.Mouse)
        {
            var siblings = SiblingUsages(node);
            if (siblings.Touchpad) kind = PeripheralKind.Touchpad;
            else if (siblings.TouchOrPen) return; // a touchscreen's or pen's mouse emulation
        }

        var hardwareNode = chain[hardwareIndex];

        string key;
        if (!isInternal && ContainerId(node) is { } container)
        {
            key = "container:" + container.ToString("D");
        }
        else
        {
            // Every built-in device shares one container, so the hardware
            // node's instance id tells them apart. For a composite USB device,
            // group by the parent device rather than each interface.
            var keyNode = ids[hardwareIndex].Contains("&MI_", StringComparison.OrdinalIgnoreCase) && hardwareIndex + 1 < chain.Count
                ? hardwareIndex + 1
                : hardwareIndex;
            key = "device:" + ids[keyNode];
        }

        if (!groups.TryGetValue(key, out var group))
        {
            group = new Builder(key, DisplayName(chain, hardwareIndex, isInternal, kind), isInternal);
            groups[key] = group;
        }
        group.Kinds |= kind;

        var hardwareId = ids[hardwareIndex];
        if (hardwareId.StartsWith("USB", StringComparison.OrdinalIgnoreCase) &&
            hardwareId.Contains("&MI_00", StringComparison.OrdinalIgnoreCase) &&
            StringList(hardwareNode, DEVPKEY_Device_CompatibleIds)
                .Any(c => c.Contains("Class_03&SubClass_01&Prot_02", StringComparison.OrdinalIgnoreCase)))
        {
            group.FirstInterfaceIsBootMouse = true;
        }
        group.Notes.Add(Bus(hardwareId));
    }

    private static string Bus(string instanceId)
    {
        var slash = instanceId.IndexOf('\\');
        var bus = slash > 0 ? instanceId[..slash] : instanceId;
        return bus.ToUpperInvariant() switch
        {
            "USB" => "USB",
            "ACPI" => "ACPI",
            "BTHENUM" or "BTHLEDEVICE" or "BTHHFENUM" => "Bluetooth",
            "HID" => "HID",
            _ => bus,
        };
    }

    private static bool IsHidNode(string instanceId) =>
        instanceId.StartsWith("HID", StringComparison.OrdinalIgnoreCase);

    /// <summary>Software devices: remote desktop, virtual machines, vendor tools' virtual keyboards.</summary>
    private static bool IsVirtual(IEnumerable<string> ids) => ids.Any(i =>
        i.StartsWith("ROOT", StringComparison.OrdinalIgnoreCase) ||
        i.StartsWith("SWD", StringComparison.OrdinalIgnoreCase) ||
        i.StartsWith("VHF", StringComparison.OrdinalIgnoreCase) ||
        i.StartsWith("TERMINPUT", StringComparison.OrdinalIgnoreCase) ||
        i.StartsWith("VMBUS", StringComparison.OrdinalIgnoreCase) ||
        i.Contains("HID_DEVICE_SYSTEM_VHF", StringComparison.OrdinalIgnoreCase) ||
        i.Contains("RDP_", StringComparison.OrdinalIgnoreCase));

    private static readonly string[] ButtonWords =
    [
        "button", "hotkey", "hot key", "airplane", "event filter",
        "slate", "convertible", "volume", "virtual",
    ];

    private static readonly string[] ButtonServices =
    [
        "hidinterrupt", "HidEventFilter", "SurfaceButton", "MSHWButtons", "buttonconverter",
        "WirelessButtonDriver", "iaLPSS2_GPIO", "HidIr",
    ];

    /// <summary>
    /// Tablets report their volume and power buttons, and laptops their
    /// airplane-mode key, as keyboards. Those never come and go, so they must
    /// not stop tablet mode.
    /// </summary>
    private static bool LooksLikeButtons(List<(string Name, string Service)> chain)
    {
        // The first node is the keyboard collection itself ("HID Keyboard
        // Device"); what matters is what it hangs off.
        foreach (var (name, service) in chain.Skip(1).Take(3))
        {
            if (ButtonServices.Any(s => service.Equals(s, StringComparison.OrdinalIgnoreCase))) return true;
            if (ButtonWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    /// <summary>What the other collections of the same HID device are.</summary>
    private static (bool Touchpad, bool TouchOrPen) SiblingUsages(uint node)
    {
        var touchpad = false;
        var touchOrPen = false;
        if (CM_Get_Parent(out var parent, node, 0) != 0) return (false, false);
        if (CM_Get_Child(out var child, parent, 0) != 0) return (false, false);
        for (var n = child; n != 0;)
        {
            foreach (var hw in StringList(n, DEVPKEY_Device_HardwareIds))
            {
                // HID usage page 0x0D is digitizers: 5 touch pad, 4 touch
                // screen, 2 pen, 1 digitizer (pen tablets).
                if (hw.Contains("UP:000D_U:0005", StringComparison.OrdinalIgnoreCase)) touchpad = true;
                if (hw.Contains("UP:000D_U:0004", StringComparison.OrdinalIgnoreCase) ||
                    hw.Contains("UP:000D_U:0002", StringComparison.OrdinalIgnoreCase) ||
                    hw.Contains("UP:000D_U:0001", StringComparison.OrdinalIgnoreCase)) touchOrPen = true;
            }
            if (CM_Get_Sibling(out var next, n, 0) != 0) break;
            n = next;
        }
        return (touchpad, touchOrPen);
    }

    private static string DisplayName(List<uint> chain, int hardwareIndex, bool isInternal, PeripheralKind kind)
    {
        // The product name the device reports about itself is usually best
        // ("Razer Basilisk V3 X HyperSpeed"); generic driver names are not.
        // Look no higher than the device itself, or the composite USB device
        // above an interface: above that are hubs and bridges ("PCI Device").
        var top = hardwareIndex;
        if (hardwareIndex + 1 < chain.Count && DeviceId(chain[hardwareIndex]).Contains("&MI_", StringComparison.OrdinalIgnoreCase))
            top = hardwareIndex + 1;
        for (var i = top; i >= 0; i--)
        {
            var reported = StringProperty(chain[i], DEVPKEY_Device_BusReportedDeviceDesc);
            if (IsUseful(reported)) return reported!;
        }
        for (var i = 0; i <= top; i++)
        {
            var name = Name(chain[i]);
            if (IsUseful(name)) return name;
        }
        var what = kind == PeripheralKind.Keyboard ? "keyboard" : kind == PeripheralKind.Touchpad ? "touchpad" : "mouse";
        return isInternal ? $"Built-in {what}" : $"External {what}";

        static bool IsUseful(string? name) =>
            !string.IsNullOrWhiteSpace(name) &&
            !name.StartsWith("HID", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("USB Input Device", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("USB Composite Device", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("Filter Driver", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("I2C HID", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("Standard", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("Microsoft Input Configuration", StringComparison.OrdinalIgnoreCase);
    }

    // ---- Machine facts --------------------------------------------------------

    public static MachineInfo ReadMachine(int chassis, int role)
    {
        var digitizer = GetSystemMetrics(SM_DIGITIZER);
        var touches = GetSystemMetrics(SM_MAXIMUMTOUCHES);
        var hasTouch = (digitizer & (NID_INTEGRATED_TOUCH | NID_EXTERNAL_TOUCH)) != 0 && touches > 0;
        var slate = GetSystemMetrics(SM_CONVERTIBLESLATEMODE) == 0;
        return new MachineInfo(hasTouch, touches, slate, chassis, role);
    }

    public static int ReadPlatformRole()
    {
        try { return PowerDeterminePlatformRoleEx(POWER_PLATFORM_ROLE_V2); }
        catch (EntryPointNotFoundException) { return 0; }
    }

    /// <summary>The SMBIOS chassis type (0 when unknown).</summary>
    public static int ReadChassisType()
    {
        const uint RSMB = 0x52534D42;
        var size = GetSystemFirmwareTable(RSMB, 0, null, 0);
        if (size == 0) return 0;
        var buffer = new byte[size];
        if (GetSystemFirmwareTable(RSMB, 0, buffer, size) != size) return 0;

        // RawSMBIOSData: 8-byte header, then the structure table.
        var tableLength = BitConverter.ToInt32(buffer, 4);
        var offset = 8;
        var end = Math.Min(buffer.Length, 8 + tableLength);
        while (offset + 4 <= end)
        {
            var type = buffer[offset];
            var length = buffer[offset + 1];
            if (length < 4) break;
            if (type == 3 && length > 5) return buffer[offset + 5] & 0x7F;
            if (type == 127) break;

            // Skip the formatted area, then the string set, which ends with two zero bytes.
            var next = offset + length;
            while (next + 1 < end && !(buffer[next] == 0 && buffer[next + 1] == 0)) next++;
            offset = next + 2;
        }
        return 0;
    }

    // ---- Configuration manager helpers ----------------------------------------

    private static string DeviceId(uint node)
    {
        var buffer = new char[512];
        return CM_Get_Device_ID(node, buffer, buffer.Length, 0) == 0
            ? new string(buffer, 0, Array.IndexOf(buffer, (char)0) is var n and >= 0 ? n : buffer.Length)
            : string.Empty;
    }

    private static string Name(uint node) =>
        StringProperty(node, DEVPKEY_Device_FriendlyName) ?? StringProperty(node, DEVPKEY_Device_DeviceDesc) ?? string.Empty;

    private static string Service(uint node) => StringProperty(node, DEVPKEY_Device_Service) ?? string.Empty;

    private static bool InLocalMachineContainer(uint node)
    {
        var bytes = RawProperty(node, DEVPKEY_Device_InLocalMachineContainer, out _);
        if (bytes is { Length: >= 1 }) return bytes[0] != 0;
        return ContainerId(node) is not { } id || id == LocalMachineContainer;
    }

    private static readonly Guid LocalMachineContainer = new("00000000-0000-0000-ffff-ffffffffffff");

    private static Guid? ContainerId(uint node)
    {
        var bytes = RawProperty(node, DEVPKEY_Device_ContainerId, out _);
        return bytes is { Length: 16 } ? new Guid(bytes) : null;
    }

    private static string? StringProperty(uint node, DEVPROPKEY key)
    {
        var bytes = RawProperty(node, key, out var type);
        if (bytes is null || type != DEVPROP_TYPE_STRING) return null;
        var text = System.Text.Encoding.Unicode.GetString(bytes).TrimEnd((char)0);
        return text.Length > 0 ? text : null;
    }

    private static IEnumerable<string> StringList(uint node, DEVPROPKEY key)
    {
        var bytes = RawProperty(node, key, out var type);
        if (bytes is null || type != DEVPROP_TYPE_STRING_LIST) return [];
        return System.Text.Encoding.Unicode.GetString(bytes)
            .Split((char)0, StringSplitOptions.RemoveEmptyEntries);
    }

    private static byte[]? RawProperty(uint node, DEVPROPKEY key, out uint type)
    {
        uint size = 0;
        var result = CM_Get_DevNode_Property(node, ref key, out type, null, ref size, 0);
        if (result != CR_BUFFER_SMALL || size == 0) return null;
        var buffer = new byte[size];
        result = CM_Get_DevNode_Property(node, ref key, out type, buffer, ref size, 0);
        return result == 0 ? buffer : null;
    }

    // ---- Native ---------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;

        public DEVPROPKEY(string guid, uint id)
        {
            fmtid = new Guid(guid);
            pid = id;
        }
    }

    private static readonly DEVPROPKEY DEVPKEY_Device_DeviceDesc = new("a45c254e-df1c-4efd-8020-67d146a850e0", 2);
    private static readonly DEVPROPKEY DEVPKEY_Device_HardwareIds = new("a45c254e-df1c-4efd-8020-67d146a850e0", 3);
    private static readonly DEVPROPKEY DEVPKEY_Device_CompatibleIds = new("a45c254e-df1c-4efd-8020-67d146a850e0", 4);
    private static readonly DEVPROPKEY DEVPKEY_Device_Service = new("a45c254e-df1c-4efd-8020-67d146a850e0", 6);
    private static readonly DEVPROPKEY DEVPKEY_Device_FriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    private static readonly DEVPROPKEY DEVPKEY_Device_ContainerId = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2);
    private static readonly DEVPROPKEY DEVPKEY_Device_InLocalMachineContainer = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 4);
    private static readonly DEVPROPKEY DEVPKEY_Device_BusReportedDeviceDesc = new("540b947e-8b40-45bc-a8a2-6a0b894cbda2", 4);

    private const uint DEVPROP_TYPE_STRING = 0x12;
    private const uint DEVPROP_TYPE_STRING_LIST = 0x2012;
    private const uint CR_BUFFER_SMALL = 0x1A;
    private const uint DIGCF_PRESENT = 0x2;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    private const int SM_DIGITIZER = 94;
    private const int SM_MAXIMUMTOUCHES = 95;
    private const int SM_CONVERTIBLESLATEMODE = 0x2003;
    private const int NID_INTEGRATED_TOUCH = 0x01;
    private const int NID_EXTERNAL_TOUCH = 0x02;
    private const uint POWER_PLATFORM_ROLE_V2 = 2;

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID(uint node, char[] buffer, int length, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Child(out uint child, uint node, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Sibling(out uint sibling, uint node, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static extern uint CM_Get_DevNode_Property(uint node, ref DEVPROPKEY key, out uint type,
        byte[]? buffer, ref uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("powrprof.dll")]
    private static extern int PowerDeterminePlatformRoleEx(uint version);

    [DllImport("kernel32.dll")]
    private static extern uint GetSystemFirmwareTable(uint provider, uint id, byte[]? buffer, uint size);
}
