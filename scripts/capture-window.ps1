# Captures the on-screen MULExplorer window (including OS backdrops such as Mica, which
# in-app render-to-bitmap screenshots cannot see) to a PNG.
#   scripts/capture-window.ps1 -Out C:\temp\shot.png [-ProcessName MULExplorer]
# Refuses to capture unless the window is verifiably in the foreground, so it can never
# grab whatever other application happens to be on top.
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$ProcessName = 'MULExplorer'
)

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win32Capture {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
}
'@

$proc = Get-Process -Name $ProcessName -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { throw "No visible $ProcessName window." }
$hwnd = $proc.MainWindowHandle

# Windows only lets a process take focus after input; a synthetic Alt press satisfies that rule.
for ($i = 0; $i -lt 5 -and [Win32Capture]::GetForegroundWindow() -ne $hwnd; $i++) {
    [Win32Capture]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
    [Win32Capture]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    [Win32Capture]::ShowWindow($hwnd, 9) | Out-Null   # SW_RESTORE
    [Win32Capture]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 400
}

if ([Win32Capture]::GetForegroundWindow() -ne $hwnd) {
    throw "Could not bring the $ProcessName window to the foreground; not capturing."
}

Start-Sleep -Milliseconds 400

# DWMWA_EXTENDED_FRAME_BOUNDS (9): the visible frame without the invisible resize borders.
$rect = New-Object Win32Capture+RECT
[Win32Capture]::DwmGetWindowAttribute($hwnd, 9, [ref]$rect, [Runtime.InteropServices.Marshal]::SizeOf($rect)) | Out-Null
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top

$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
$bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bitmap.Dispose()
"$Out ($width x $height)"
