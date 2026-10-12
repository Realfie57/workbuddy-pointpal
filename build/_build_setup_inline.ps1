# Build Uninstall.exe + "WorkBuddy PointPal Setup.exe" (embeds the pet exe).
# Inlined from build_setup.ps1 because nested & invocation is blocked here.
# Diagnostics go to _build_setup_log.txt.
$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$log  = Join-Path $here '_build_setup_log.txt'
$buf  = New-Object System.Collections.Generic.List[string]

function Build-One {
    param([string]$src, [string]$out, [string[]]$res, [string]$asm)
    $ico = Join-Path $here 'DaFeiYu.ico'
    $cp = New-Object System.CodeDom.Compiler.CompilerParameters
    $cp.GenerateExecutable = $true
    $cp.GenerateInMemory = $false
    $cp.IncludeDebugInformation = $false
    $cp.OutputAssembly = $out
    $cp.CompilerOptions = "/optimize+ /target:winexe /codepage:65001 /win32icon:$ico"
    foreach ($a in @('System.dll','System.Core.dll','System.Drawing.dll',
                     'System.Windows.Forms.dll','Microsoft.CSharp.dll')) {
        [void]$cp.ReferencedAssemblies.Add($a)
    }
    foreach ($r in $res) { [void]$cp.EmbeddedResources.Add($r) }
    $prov = New-Object Microsoft.CSharp.CSharpCodeProvider
    # Compile the program together with its assembly metadata block, so the
    # exe carries a real File version / Product version (Explorer shows
    # 0.0.0.0 without one).
    $srcs = @($src)
    if ($asm) {
        if (-not (Test-Path $asm)) { throw "missing assembly info: $asm" }
        $srcs += $asm
    }
    # Explicit [string[]] cast: an unrolled PowerShell array is passed as
    # separate positional arguments and the call fails.
    [string[]]$srcsT = $srcs
    $r2 = $prov.CompileAssemblyFromFile($cp, $srcsT)
    if ($r2.Errors.Count -gt 0) {
        foreach ($e in $r2.Errors) { $buf.Add('ERR: ' + $e.ToString()) }
        return $false
    }
    $buf.Add('built: ' + $out)
    return $true
}

try {
    $ico    = Join-Path $here 'DaFeiYu.ico'
    $petExe = Join-Path $here 'WorkBuddy PointPal.exe'
    if (-not (Test-Path $petExe)) { throw 'pet exe missing' }

    # NOTE: no .vbs is embedded any more. The dual launcher used to ship as
    # _payload_bundle_launcher.vbs, which 360 quarantined twice
    # (virus.vbs.powershell.a / virus.vbs.dropper.d). The logic now lives in
    # wb_pet.cs behind --launch-wb and the shortcut targets the pet exe, so the
    # only extra payload left is the shortcut icon.
    $bundleIco = Join-Path $here 'WbLauncher.ico'
    if (-not (Test-Path $bundleIco)) { throw 'bundle launcher icon missing (WbLauncher.ico)' }
    $buf.Add('bundle icon: ' + ([System.IO.File]::ReadAllBytes($bundleIco).Length) + ' bytes')

    $ok1 = Build-One -src (Join-Path $here 'uninstall.cs') -out (Join-Path $here 'Uninstall.exe') -res @() `
                     -asm (Join-Path $here '_asm_uninstall.cs')
    $ok2 = Build-One -src (Join-Path $here 'setup.cs') `
                     -out (Join-Path $here 'WorkBuddy PointPal Setup.exe') `
                     -res @($petExe, (Join-Path $here 'Uninstall.exe'), $ico, $bundleIco) `
                     -asm (Join-Path $here '_asm_setup.cs')
    if ($ok1 -and $ok2) { $buf.Add('RESULT: OK') } else { $buf.Add('RESULT: COMPILE_FAILED') }
} catch {
    $buf.Add('EXCEPTION: ' + $_.Exception.Message)
    $buf.Add('RESULT: EXCEPTION')
}
[System.IO.File]::WriteAllLines($log, $buf, [System.Text.UTF8Encoding]::new($false))
