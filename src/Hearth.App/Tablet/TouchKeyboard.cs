using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Hearth.Core.Diagnostics;

namespace Hearth.App.Tablet;

/// <summary>
/// Shows or hides the Windows touch keyboard, for the navigation bar's
/// keyboard button. The shell's own toggle (ITipInvocation) is used when the
/// keyboard host is running; otherwise starting TabTip.exe brings it up.
/// </summary>
internal static class TouchKeyboard
{
    public static void Toggle()
    {
        try
        {
            var tip = (ITipInvocation)Activator.CreateInstance(Type.GetTypeFromCLSID(TipInvocationClsid, throwOnError: true)!)!;
            try
            {
                tip.Toggle(GetDesktopWindow());
                Log.Write("tablet: touch keyboard toggled");
                return;
            }
            finally
            {
                Marshal.ReleaseComObject(tip);
            }
        }
        catch (COMException ex)
        {
            Log.Write($"tablet: touch keyboard toggle failed ({ex.HResult:X8}); starting TabTip");
        }

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            "microsoft shared", "ink", "TabTip.exe");
        if (!File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Write($"tablet: TabTip did not start: {ex.Message}");
        }
    }

    private static readonly Guid TipInvocationClsid = new("4ce576fa-83dc-4f88-951c-9d0782b4e376");

    [ComImport]
    [Guid("37c994e7-432b-4834-a2f7-dce1f13b834b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITipInvocation
    {
        void Toggle(IntPtr hwnd);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();
}
