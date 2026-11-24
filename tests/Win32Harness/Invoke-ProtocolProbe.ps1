[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [int[]]$Sequence = @(144, 192, 96, 0, 300)
)
# Drives cooperative protocol v1 (docs/adr/0002) against a process from outside, exactly like
# Core's CooperativeBackend: finds its top-level windows carrying the property, sends SetDpi then GetDpi.
$ErrorActionPreference = 'Stop'
if (-not ('AppScaleProbe.Native' -as [type])) {
Add-Type -Namespace AppScaleProbe -Name Native -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string s);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetPropW(IntPtr h, string s);
[DllImport("user32.dll")] public static extern IntPtr SendMessageTimeoutW(IntPtr h, uint m, UIntPtr w, IntPtr l, uint f, uint t, out UIntPtr r);
public delegate bool EnumProc(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
'@
}
$N = [AppScaleProbe.Native]
$set = $N::RegisterWindowMessageW('AppScale.SetDpi.v1')
$get = $N::RegisterWindowMessageW('AppScale.GetDpi.v1')
$found = New-Object System.Collections.Generic.List[IntPtr]
$cb = [AppScaleProbe.Native+EnumProc]{ param($h, $l)
    $procId = [uint32]0; [void]$N::GetWindowThreadProcessId($h, [ref]$procId)
    if ($procId -eq $ProcessId -and $N::GetPropW($h, 'AppScale.Cooperative.v1') -ne [IntPtr]::Zero) { $found.Add($h) }
    return $true }
[void]$N::EnumWindows($cb, [IntPtr]::Zero)
if ($found.Count -eq 0) { [pscustomobject]@{ pid = $ProcessId; cooperative = $false } | ConvertTo-Json -Compress; exit 3 }
$out = foreach ($h in $found) {
    foreach ($d in $Sequence) {
        $r = [UIntPtr]::Zero
        $okSet = $N::SendMessageTimeoutW($h, $set, [UIntPtr][uint32]$d, [IntPtr]::Zero, 0x0002 -bor 0x0001, 2000, [ref]$r) -ne [IntPtr]::Zero
        $setResult = $r.ToUInt64()
        $g = [UIntPtr]::Zero
        $okGet = $N::SendMessageTimeoutW($h, $get, [UIntPtr]::Zero, [IntPtr]::Zero, 0x0002 -bor 0x0001, 2000, [ref]$g) -ne [IntPtr]::Zero
        $v = $g.ToUInt64()
        [pscustomobject]@{ hwnd = ('0x{0:X}' -f $h.ToInt64()); setDpi = $d; sendOk = $okSet; setResult = $setResult
            getOk = $okGet; effective = ($v -band 0xFFFF); os = (($v -shr 16) -band 0xFFFF) }
    }
}
$out | ConvertTo-Json -Compress
