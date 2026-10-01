# Compile the pet exe from wb_pet.cs using the in-box CodeDom C# compiler,
# driven entirely from this single .ps1 (no nested script invocation, which is
# blocked in this sandbox). Diagnostic output goes to _build_pet_log.txt.
$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$log  = Join-Path $here '_build_pet_log.txt'
$buf  = New-Object System.Collections.Generic.List[string]

try {
    $exeName = 'WorkBuddy PointPal.exe'
    $out = Join-Path $here $exeName
    $ico = Join-Path $here 'DaFeiYu.ico'
    $cs  = Join-Path $here 'wb_pet.cs'
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
    $buf.Add('dir: ' + $here)
    foreach ($r in $res) {
        if (-not (Test-Path (Join-Path $here $r))) { throw "missing resource: $r" }
    }
    $cp = New-Object System.CodeDom.Compiler.CompilerParameters
    $cp.GenerateExecutable = $true
    $cp.GenerateInMemory = $false
    $cp.IncludeDebugInformation = $false
    $cp.OutputAssembly = $out
    $cp.CompilerOptions = "/optimize+ /target:winexe /win32icon:$ico"
    foreach ($a in @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Net.Http.dll')) {
        [void]$cp.ReferencedAssemblies.Add($a)
    }
    foreach ($r in $res) { [void]$cp.EmbeddedResources.Add((Join-Path $here $r)) }
    $prov = New-Object Microsoft.CSharp.CSharpCodeProvider
    # Two source files: the program itself, plus the assembly metadata block
    # that gives the exe its File version / Product version (without it
    # Explorer reports 0.0.0.0).
    $asm  = Join-Path $here '_asm_pet.cs'
    if (-not (Test-Path $asm)) { throw "missing assembly info: $asm" }
    # Cast to [string[]] explicitly: passing a PowerShell array straight
    # through gets unrolled into separate positional arguments and the
    # call fails with "不支持给定路径的格式".
    [string[]]$srcs = @($cs, $asm)
    $r2 = $prov.CompileAssemblyFromFile($cp, $srcs)
    if ($r2.Errors.Count -gt 0) {
        foreach ($e in $r2.Errors) { $buf.Add('ERR: ' + $e.ToString()) }
        $buf.Add('RESULT: COMPILE_FAILED')
    } else {
        $buf.Add('built: ' + $out)
        $buf.Add('RESULT: OK')
    }
} catch {
    $buf.Add('EXCEPTION: ' + $_.Exception.Message)
    $buf.Add('RESULT: EXCEPTION')
}
[System.IO.File]::WriteAllLines($log, $buf, [System.Text.UTF8Encoding]::new($false))
