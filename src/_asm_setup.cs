// ============================================================================
//  Assembly metadata for "WorkBuddy PointPal Setup.exe".
//
//  The installer is a separate program from the pet, so it needs its own
//  metadata block (a file can only carry one).  Without it Explorer shows
//  0.0.0.0 in both version fields.
//
//  KEEP IN SYNC with PointPalSetup.Version in setup.cs; _tools/_sync_version.py
//  rewrites both whenever the version is bumped.
//
//  UTF-8: only ASCII characters are used here, but this file is compiled
//  together with setup.cs using /codepage:65001, so UTF-8 is safe.
// ============================================================================
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("WorkBuddy PointPal Setup")]
[assembly: AssemblyDescription("Installer for the WorkBuddy PointPal desktop pet")]
[assembly: AssemblyProduct("WorkBuddy PointPal")]
[assembly: AssemblyCompany("Realfie")]
[assembly: AssemblyCopyright("Copyright (C) Realfie")]
[assembly: AssemblyVersion("1.2.16.1")]
[assembly: AssemblyFileVersion("1.2.16.1")]
[assembly: ComVisible(false)]
