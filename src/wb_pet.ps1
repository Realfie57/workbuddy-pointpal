# ============================================================================
#  WorkBuddy credits pet  (v1)
#
#  A draggable desktop overlay: the character holds a tablet whose screen shows
#  the live WorkBuddy credits balance. Every time the balance drops by the
#  configured step (1 credit by default) the character flashes red and shakes
#  (Minecraft hurt style) and a red "-1" floats up above their head.
#
#  Based on DSH balance pet v2 (VKmich16/VK-1, Windows original). Same artwork,
#  same animation / sound / accounting architecture; the data source is now
#  the WorkBuddy billing endpoint:
#    POST https://www.workbuddy.cn/billing/meter/get-user-resource-summary
#    headers: Authorization: Bearer <token>, X-User-Id: <uid>, Accept-Language
#    balance = sum of data.packages[].cycleRemain
#
#  Credentials: WorkBuddy has no public API key. The token is pasted once by
#  the user (browser F12 guide in the docs) and stored in token.txt next to
#  this script (line 1 = token, optional line 2 = uid). A best-effort local
#  auto-detect also runs at startup; see the docs for why manual paste is the
#  reliable path.
#
#  Windows PowerShell 5.1 + WinForms. No external packages, no admin rights.
#  ASCII-only: PS 5.1 reads .ps1 without a BOM as ANSI, so all UI text lives
#  in C# \uXXXX escapes inside the here-string below.
# ============================================================================

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

if (-not ('WbPet' -as [type])) {
$csPath = Join-Path $PSScriptRoot 'wb_pet.cs'
Add-Type -ReferencedAssemblies @('System.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Net.Http.dll') -TypeDefinition (Get-Content $csPath -Raw -Encoding ASCII)
}

# ------------------------------------------------------------------ startup --

$here = $PSScriptRoot
if (-not $here) { $here = (Get-Location).Path }

# Token: environment first, then the local token.txt (line 1 = token,
# optional line 2 = uid).
$tokenFile = Join-Path $here 'token.txt'
if (-not $env:WBPET_TOKEN -and (Test-Path $tokenFile)) {
    $lines = @(Get-Content $tokenFile | ForEach-Object { $_.Trim() })
    if ($lines.Count -ge 1 -and $lines[0]) { $env:WBPET_TOKEN = $lines[0] }
    if ($lines.Count -ge 2 -and $lines[1] -and -not $env:WBPET_UID) { $env:WBPET_UID = $lines[1] }
    if ($lines.Count -ge 3 -and $lines[2] -and -not $env:WBPET_UA) { $env:WBPET_UA = $lines[2] }
}
# NOTE: WBPET_SPRITE is deliberately NOT auto-set here. The C# side resolves
# each character's artwork from its own path (default = sprite.png next to
# this script); the env var remains an explicit override for the default
# character only. Auto-setting it would make character switching load the
# default art for everyone.

try {
    [WbPet]::Run($here, $args)
} catch {
    $_ | Out-String | Set-Content (Join-Path $here 'error.log') -Encoding UTF8
    throw
}
