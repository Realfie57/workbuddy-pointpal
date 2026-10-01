# ============================================================================
#  Build the standalone single-file exe from wb_pet.cs.
#
#  The exe embeds every resource (artwork, characters, hit sound, icon) and
#  unpacks them into %LOCALAPPDATA%\<app folder> on first run, so it works on
#  any Windows 10/11 machine as-is: no PowerShell window, no admin rights, no
#  .NET to install (the in-box .NET Framework 4.x is both compiler and
#  runtime). Token / state / logs live in that data folder.
#
#  Compilation goes through CodeDom (System.CodeDom.Compiler) - the very same
#  in-box compiler Add-Type uses when the pet itself starts, so no extra SDK
#  is needed. ASCII-only: PS 5.1 reads .ps1 without a BOM as ANSI; the Chinese
#  exe name is assembled from char codes.
# ============================================================================
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
if (-not $here) { $here = (Get-Location).Path }

# exe name: pure ASCII since the rename to PointPal (no more char codes)
$exeName = 'WorkBuddy PointPal.exe'
$out = Join-Path $here $exeName
$ico = Join-Path $here 'DaFeiYu.ico'
$cs  = Join-Path $here 'wb_pet.cs'

# keep in sync with ExeEntry.ResFiles in wb_pet.cs. NOTE: the compiler names
# each embedded resource by its BARE FILE NAME, so every file must have a
# unique name across all folders (this is why characters\sprite.png was
# renamed sprite-dsh.png).
$res = @(
  'sprite.png',
  'characters\sprite-dsh.png',
  'characters\sprite-claude.png',
  'characters\sprite-claude_detailed.png',
  'characters\sprite-gemini.png',
  'characters\sprite-gemini_detailed.png',
  'characters\sprite-gpt.png',
  'characters\sprite-gpt_detailed.png',
  'hit.mp3',
  'DaFeiYu.ico'
)
foreach ($r in $res) {
  if (-not (Test-Path (Join-Path $here $r))) { throw "missing resource: $r" }
}

$cp = New-Object System.CodeDom.Compiler.CompilerParameters
$cp.GenerateExecutable = $true
$cp.GenerateInMemory = $false
$cp.IncludeDebugInformation = $false
$cp.OutputAssembly = $out
$cp.CompilerOptions = "/optimize+ /target:winexe /win32icon:$ico"
foreach ($a in @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Net.Http.dll')) {
  [void]$cp.ReferencedAssemblies.Add($a)
}
# Absolute paths: the compiler resolves relative EmbeddedResources against the
# PROCESS CWD (Environment.CurrentDirectory), which Push-Location does NOT
# change - it only moves the PowerShell runspace location. Bare-filename
# resource naming is unaffected by passing full paths.
foreach ($r in $res) { [void]$cp.EmbeddedResources.Add((Join-Path $here $r)) }
$prov = New-Object Microsoft.CSharp.CSharpCodeProvider
$r = $prov.CompileAssemblyFromFile($cp, $cs)

if ($r.Errors.Count -gt 0) {
  foreach ($e in $r.Errors) { Write-Output $e.ToString() }
  throw 'compile failed'
}
Write-Output "built: $out"
