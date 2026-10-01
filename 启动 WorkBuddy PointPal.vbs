' ===========================================================================
'  WorkBuddy PointPal - silent launcher (no console window).
'  Double-click this file to start the pet.
'  Kept ASCII-only: cscript.exe reads .vbs as ANSI without a BOM.
' ===========================================================================
Option Explicit

Dim fso, shell, here, script, cmd
Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

here = fso.GetParentFolderName(WScript.ScriptFullName)
script = fso.BuildPath(here, "wb_pet.ps1")

If Not fso.FileExists(script) Then
    MsgBox "wb_pet.ps1 is missing next to this launcher.", 16, "WorkBuddy PointPal"
    WScript.Quit 1
End If

If Not fso.FileExists(fso.BuildPath(here, "sprite.png")) Then
    MsgBox "sprite.png is missing." & vbCrLf & vbCrLf & _
           "Run this once in PowerShell to build it from your artwork:" & vbCrLf & _
           "  .\make_sprite.ps1 -Source ""<path to your image>""", 48, "WorkBuddy PointPal"
    WScript.Quit 1
End If

shell.CurrentDirectory = here
cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & script & """"
shell.Run cmd, 0, False
