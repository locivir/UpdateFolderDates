using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("UpdateFolderDates")]
[assembly: AssemblyDescription("Sets each directory's creation time to the earliest, and its modified time to the latest, timestamp derived from the files within its subtree (a pre-1980 creation time falls back to the file's last-write time, which also counts toward the earliest-creation calculation).")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("Locivir")]
[assembly: AssemblyProduct("UpdateFolderDates")]
[assembly: AssemblyCopyright("Copyright © 2018")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Setting ComVisible to false makes the types in this assembly not visible 
// to COM components.  If you need to access a type in this assembly from 
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("b1138f9c-9e1b-46f2-92c4-cf3493569378")]

// Version information for an assembly consists of the following four values:
//
//      Major Version
//      Minor Version 
//      Build Number
//      Revision
//
// You can specify all the values or you can default the Build and Revision Numbers 
// by using the '*' as shown below:
// [assembly: AssemblyVersion("1.0.*")]
[assembly: AssemblyVersion("1.1.2.0")]
[assembly: AssemblyFileVersion("1.1.2.0")]
