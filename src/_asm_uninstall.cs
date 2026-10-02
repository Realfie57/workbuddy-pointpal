// ============================================================================
//  Assembly metadata for Uninstall.exe.
//
//  Same reason as _asm_setup.cs: the uninstaller is its own program and needs
//  its own metadata block.  Without it Explorer shows 0.0.0.0.
//
//  KEEP IN SYNC with PointPalSetup.Version; _tools/_sync_version.py stamps all
//  three assembly files plus setup.cs in one go.
//
//  Compiled together with uninstall.cs using /codepage:65001.
// ============================================================================
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("WorkBuddy PointPal Uninstall")]
[assembly: AssemblyDescription("Uninstaller for the WorkBuddy PointPal desktop pet")]
[assembly: AssemblyProduct("WorkBuddy PointPal")]
[assembly: AssemblyCompany("Realfie")]
[assembly: AssemblyCopyright("Copyright (C) Realfie")]
[assembly: AssemblyVersion("1.2.14.0")]
[assembly: AssemblyFileVersion("1.2.14.0")]
[assembly: ComVisible(false)]
