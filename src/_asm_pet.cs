// ============================================================================
//  Assembly metadata for WorkBuddy PointPal (the pet application).
//
//  Without this file the compiler emits no version resource at all, so
//  Explorer's Details tab shows 0.0.0.0 for both File version and Product
//  version.  The compiler picks up the [assembly: ...] attributes from any
//  source file listed on the command line - this one carries nothing but
//  metadata, so it never affects the program.
//
//  KEEP IN SYNC with PointPalSetup.Version in setup.cs (the installer shows
//  it in Apps & features); see _tools/_sync_version.py which stamps both.
//
//  Pure ASCII on purpose: it is compiled with the pet (/codepage default),
//  and Chinese text here would need an explicit codepage.
// ============================================================================
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("WorkBuddy PointPal")]
[assembly: AssemblyDescription("WorkBuddy account points desktop pet")]
[assembly: AssemblyProduct("WorkBuddy PointPal")]
[assembly: AssemblyCompany("Realfie")]
[assembly: AssemblyCopyright("Copyright (C) Realfie")]
[assembly: AssemblyVersion("1.2.13.0")]
[assembly: AssemblyFileVersion("1.2.13.0")]
[assembly: ComVisible(false)]
