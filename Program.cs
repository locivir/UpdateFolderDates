using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace UpdateFolderLastModifiedAndCreated
{
    static class Program
    {
        static int retry = 0;
        static bool verbose = false;
        static bool quiet = false;
        static bool updateCreate = false;
        static bool updateModify = true;
        static string path = string.Empty;
        static readonly List<string> ignore = new List<string>(); 

        static void Main(string[] args)
        {
            GetIni();
            if (args.Length == 0)
            {
                Console.WriteLine($"Usage: {AppDomain.CurrentDomain.FriendlyName} <path>");

                Console.WriteLine("Options:");
                Console.WriteLine("    /ini=<filename>\tConfiguration file (default in the folder of the program)");
                Console.WriteLine("    /created=true|false\tUpdate created timestamp (default=false)");
                Console.WriteLine("    /modified=true|false\tUpdate modified timestamp (default=true)");
                Console.WriteLine("    /verbose=true|false\tVerbose output (default=false)");
                Console.WriteLine("    /quiet=true|false\tSuppress output (default=false)");
                Console.WriteLine("    /ignore=<filter>\tApply a filter for files to ignore when checking for dates, can be used multiple times");
                Console.WriteLine("    \tUsing the /ignore option will replace all entries from the ini file!");
                return;
            }
            if (args.Length == 1)
            {
                path = args[0];
            }

            if (args.Length > 1)
            {
                for (int i = 0; i < args.Length; i++)
                    if (args[i].StartsWith("/"))
                    {
                        string[] split = args[i].Substring(1).Split('=');
                        if (split[0] == "ini") ProcessIni(split[1]);
                        if (split[0] == "created") updateCreate = split[1] == "true";
                        if (split[0] == "modified") updateModify = split[1] == "true";
                        if (split[0] == "verbose") verbose = true;
                        if (split[0] == "quiet") quiet = true;
                        if (split[0] == "ignore")
                        {
                            if (IsValidFilename(split[1])) ignore.Add(split[1]);
                            else if (!quiet) Console.WriteLine("Ignoring invalid ignore entry {0}", split[1]);
                        }
                    }
                    else path = args[i];
            }

            if (Directory.Exists(path))
                path = Path.GetFullPath(path);
            
            if (!Directory.Exists(path))
            {
                if (!quiet)
                    Console.WriteLine("Invalid directory: " + path);
                Environment.Exit(1);
                return;
            }

            if (!quiet)
                if(updateCreate && updateModify)
                    Console.WriteLine("Updating created and modified timestamps for \"" + path + "\" and the directories therein.");
                else if (updateCreate)
                    Console.WriteLine("Updating created timestamps for \"" + path + "\" and the directories therein.");
                else
                    Console.WriteLine("Updating created and modified timestamps for \"" + path + "\" and the directories therein.");

            DateTime creation = DateTime.MaxValue;
            DateTime modification = DateTime.MinValue;
            ParseDirectory(path, ref creation, ref modification);

            if (!quiet)
                Console.WriteLine("Press any key to continue...");
            Console.ReadKey();
            Environment.Exit(0);
        }

        static void GetIni()
        {
            string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            string ini = System.IO.Path.GetFileNameWithoutExtension(exe) + ".ini";

            string dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string file = Path.Combine(dir, ini);

            if (!File.Exists(file)) dir = System.IO.Path.GetDirectoryName(exe);
            file = Path.Combine(dir, ini);
            if (!File.Exists(file)) return;
            ProcessIni(file);
        }
        static void ProcessIni(string file)
        {
            ignore.Clear();
            string config;
            try { config = File.ReadAllText(file); } 
            catch { Console.WriteLine("Can not read INI-file."); return; }
            bool error = false;
            string pattern = ",(?=(?:[^\"]*\"[^\"]*\")*[^\"]*$)|\n";
            string[] lines = Regex.Split(config, pattern);
                foreach (string item in lines)
                    if (item.StartsWith("#")) continue;
                    else if (!IsValidFilename(item)) error = true;
                    else ignore.Add(item);
            if (error && !quiet) Console.WriteLine("Ignoring invalid configuration entries in {0}", file);
        }
        static bool IsValidFilename(string filename)
        {
            // Define a regex pattern for valid filenames with wildcard characters
            string pattern = @"^[\w\-. ]+(\*|\?)*[\w\-. ]*$";

            // Check if the filename matches the pattern
            return Regex.IsMatch(filename, pattern);
        }
        static void ParseDirectory(string cur, ref DateTime creation, ref DateTime modification)
        {
            DateTime dir_creation = DateTime.MaxValue;
            DateTime dir_modification = DateTime.MinValue;

            Directory.GetDirectories(cur).ToList().ForEach(s => ParseDirectory(s, ref dir_creation, ref dir_modification));
            Directory.GetFiles(cur).ToList().ForEach(s => ParseFile(s, ref dir_creation, ref dir_modification));

            if (verbose) Console.WriteLine("Parsing: " + cur);
            if (dir_creation != DateTime.MaxValue && Directory.GetCreationTime(cur) != dir_creation)
            {
                if (updateCreate)
                {
                    if (verbose) Console.WriteLine("Updating creation timestamp " + Directory.GetCreationTime(cur).ToString() + " => " + dir_creation.ToString());
                    else if (UpdateDir(cur, dir_creation, false) && !quiet) Console.WriteLine(cur + " created => " + dir_creation.ToString());
                }
                else
                    if (verbose) Console.WriteLine("Different creation timestamp " + Directory.GetCreationTime(cur).ToString() + " => " + dir_creation.ToString());
            }
            if (dir_modification != DateTime.MinValue && Directory.GetLastWriteTime(cur) != dir_modification)
            {
                if (updateModify)
                {
                    if (verbose) Console.WriteLine("Updating modfication timestamp " + Directory.GetLastWriteTime(cur).ToString() + " => " + dir_modification.ToString());
                    else if(UpdateDir(cur, dir_modification, true) && !quiet) Console.WriteLine(cur + " modified => " + dir_modification.ToString());
                }
                else
                    if (verbose) Console.WriteLine("Different modfication timestamp " + Directory.GetLastWriteTime(cur).ToString() + " => " + dir_modification.ToString());
            }

            if (creation > dir_creation)
                creation = dir_creation;
            if (modification < dir_modification)
                modification = dir_modification;

        }

        static void ParseFile(string file, ref DateTime creation, ref DateTime modification)
        {
            if (Path.GetFileName(file).ToLower() == "thumbs.db") 
                return;
            if (Path.GetFileName(file).ToLower() == "desktop.ini") 
                return;

            DateTime file_creation = File.GetCreationTime(file);
            DateTime file_modification = File.GetLastWriteTime(file);

            if (file_creation < DateTime.Parse("1980-01-01"))
                file_creation = file_modification;
            
            if (file_creation > DateTime.Parse("1980-01-01") && file_creation < creation) 
                creation = file_creation;
            if (file_modification < creation)
                creation = file_modification;

            if (file_modification != DateTime.MinValue && file_modification > modification) 
                modification = file_modification;
        }

        static bool UpdateDir(string dir, DateTime date, bool modified)
        {
            try
            {
                if (modified)
                    Directory.SetLastWriteTime(dir, date);
                else
                    Directory.SetCreationTime(dir, date);
                return true;
            }
            catch (IOException)
            {
                    Console.WriteLine(string.Format("{0}: Failed",dir));
            }
            catch (System.UnauthorizedAccessException)
            {
                Console.WriteLine(string.Format("{0}: Access denied", dir));
            }
            catch 
            {
                Console.WriteLine(string.Format("{0}: Unknown error", dir));
            }
            return false;
        }
    }
}