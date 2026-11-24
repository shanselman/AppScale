[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][string]$Path,
    # Optional window class filter (e.g. 'Chrome_WidgetWin_1'); default = largest visible top-level window of the process.
    [string]$ClassName
)
# Captures a process's main top-level window (including its non-client frame) with
# PrintWindow(PW_RENDERFULLCONTENT) and saves a PNG. Read-only: it does not move, resize or focus the window.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not ('AppScaleCapture.Native' -as [type])) {
Add-Type -Namespace AppScaleCapture -Name Native -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
public delegate bool EnumProc(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
[DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder s, int n);
'@
}
$N = [AppScaleCapture.Native]
# Physical (unvirtualized) coordinates: DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 for this thread.
[void]$N::SetThreadDpiAwarenessContext([IntPtr](-4))
$best = [IntPtr]::Zero; $bestArea = 0
$cb = [AppScaleCapture.Native+EnumProc]{ param($h, $l)
    $procId = [uint32]0; [void]$N::GetWindowThreadProcessId($h, [ref]$procId)
    if ($procId -ne $ProcessId -or -not $N::IsWindowVisible($h) -or $N::GetWindow($h, 4) -ne [IntPtr]::Zero) { return $true }
    if ($ClassName) { $sb = New-Object System.Text.StringBuilder 256; [void]$N::GetClassNameW($h, $sb, 256); if ($sb.ToString() -ne $ClassName) { return $true } }
    $r = New-Object AppScaleCapture.Native+RECT; [void]$N::GetWindowRect($h, [ref]$r)
    $area = ($r.Right - $r.Left) * ($r.Bottom - $r.Top)
    if ($area -gt $script:bestArea) { $script:bestArea = $area; $script:best = $h }
    return $true }
[void]$N::EnumWindows($cb, [IntPtr]::Zero)
if ($best -eq [IntPtr]::Zero) { throw "No visible top-level window for pid $ProcessId" }
$rect = New-Object AppScaleCapture.Native+RECT; [void]$N::GetWindowRect($best, [ref]$rect)
$w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
$bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$gfx = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $gfx.GetHdc()
$ok = $N::PrintWindow($best, $hdc, 2)  # PW_RENDERFULLCONTENT
$gfx.ReleaseHdc($hdc); $gfx.Dispose()
$dir = Split-Path -Parent ([IO.Path]::GetFullPath($Path)); if ($dir) { [void](New-Item -ItemType Directory -Force $dir) }
$bmp.Save([IO.Path]::GetFullPath($Path), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
[pscustomobject]@{ pid = $ProcessId; hwnd = ('0x{0:X}' -f $best.ToInt64()); printWindowOk = $ok; width = $w; height = $h
    externalGetDpiForWindow = $N::GetDpiForWindow($best); path = [IO.Path]::GetFullPath($Path) } | ConvertTo-Json -Compress
