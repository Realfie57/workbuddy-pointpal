# ============================================================================
#  Build the wizard installer "WorkBuddy PointPal Setup.exe".
#
#  Chain:  build_exe.ps1 (pet)  ->  this script
#    1. compile uninstall.cs  -> Uninstall.exe
#    2. compile setup.cs      -> WorkBuddy PointPal Setup.exe
#       (embeds the pet exe, the fresh Uninstall.exe and the icon)
#
#  Same CodeDom pipeline as build_exe.ps1. The installer sources are UTF-8
#  (real Chinese text), so /codepage:65001 is passed to the compiler.
#  This .ps1 itself stays ASCII-only (PS 5.1 reads BOM-less files as ANSI).
# ============================================================================
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
if (-not $here) { $here = (Get-Location).Path }

$ico    = Join-Path $here 'DaFeiYu.ico'
$petExe = Join-Path $here 'WorkBuddy PointPal.exe'
if (-not (Test-Path $petExe)) { throw 'pet exe missing - run build_exe.ps1 first' }

function Build-Exe {
    param([string]$src, [string]$out, [string[]]$res)
    $cp = New-Object System.CodeDom.Compiler.CompilerParameters
    $cp.GenerateExecutable = $true
    $cp.GenerateInMemory = $false
    $cp.IncludeDebugInformation = $false
    $cp.OutputAssembly = $out
    $cp.CompilerOptions = "/optimize+ /target:winexe /codepage:65001 /win32icon:$ico"
    foreach ($a in @('System.dll', 'System.Core.dll', 'System.Drawing.dll',
                     'System.Windows.Forms.dll', 'Microsoft.CSharp.dll')) {
        [void]$cp.ReferencedAssemblies.Add($a)
    }
    foreach ($r in $res) { [void]$cp.EmbeddedResources.Add($r) }  # absolute paths
    $prov = New-Object Microsoft.CSharp.CSharpCodeProvider
    $r = $prov.CompileAssemblyFromFile($cp, $src)
    if ($r.Errors.Count -gt 0) {
        foreach ($e in $r.Errors) { Write-Output $e.ToString() }
        throw "compile failed: $src"
    }
    Write-Output "built: $out"
}

# 1. uninstaller (no embedded resources)
Build-Exe -src (Join-Path $here 'uninstall.cs') -out (Join-Path $here 'Uninstall.exe') -res @()

# 2. setup wizard (embeds pet + uninstaller + icon; bare-file-name matching)
Build-Exe -src (Join-Path $here 'setup.cs') -out (Join-Path $here 'WorkBuddy PointPal Setup.exe') `
          -res @($petExe, (Join-Path $here 'Uninstall.exe'), $ico)
