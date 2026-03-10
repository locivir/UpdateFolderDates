using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UpdateFolderDates
{
    public class Filter
    {
        #region Properties
        private readonly string _pattern;
        public string Pattern
        {
            get
            {
                string s = HasWildcard || DirectoryIsWildcard ? "'" : "";
                string d = IsDirectory ? @"\" : "";
                return $"{s}{_pattern}{d}{s}";
            }
        }
        private bool HasWildcard { get; set; }
        private bool IsDirectory { get; set; }
        private bool DirectoryIsWildcard { get; set; }
        private string Extension { get; set; }
        private string Filename { get; set; }
        private string RegexPattern { get; set; }
        #endregion

        public Filter(string pattern)
        {
            _pattern = pattern.ToLowerInvariant();
            HasWildcard = _pattern.Contains("*");
            IsDirectory = _pattern.EndsWith("\\");
            if (IsDirectory)
                _pattern = _pattern.TrimEnd('\\');

            string[] split = _pattern.Split(new char[] { '.' });
            Extension = IsDirectory ? string.Empty : split.Length == 1 ? string.Empty : split[split.Length - 1];
            Filename = IsDirectory ? string.Empty : split.Length == 1 ?
                _pattern : _pattern.Substring(0, _pattern.Length - split[split.Length - 1].Length - 1);
            DirectoryIsWildcard = HasWildcard && IsDirectory;
            RegexPattern = string.Concat("^", Regex.Escape(_pattern).Replace("\\*", ".*"), "$");
        }
        public static IEnumerable<Filter> GetFilters(IEnumerable<string> patterns)
        {
            foreach (string pattern in patterns)
                yield return new Filter(pattern);
        }

        public bool Matches(string path)
        {
            // always use lower case
            string lower = path.ToLowerInvariant();

            // full match with absolute file name
            if (_pattern.Equals(lower)) return true;

            string file = Path.GetFileName(lower);

            // full match with file or directory name only
            if (_pattern.Equals(file)) return true;

            // without exact match or wildcard
            if (!HasWildcard) return false;

            // Directory match
            if (DirectoryIsWildcard && System.IO.Directory.Exists(path))
            {
                if (_pattern.StartsWith("*") && _pattern.EndsWith("*") &&
                    file.Contains(_pattern.Substring(1, _pattern.Length - 2))) return true;
                if (_pattern.StartsWith("*") && file.EndsWith(_pattern.Substring(1))) return true;
                if (_pattern.EndsWith("*") && file.StartsWith(_pattern.Substring(0, _pattern.Length - 2))) return true;
                return Regex.IsMatch(file, RegexPattern);
            }
            // file match
            else if (!IsDirectory && System.IO.File.Exists(path))
            {
                if (_pattern.StartsWith("*") && _pattern.EndsWith("*") &&
                  file.Contains(_pattern.Substring(1, _pattern.Length - 2))) return true;
                if (_pattern.StartsWith("*") && file.EndsWith(_pattern.Substring(1))) return true;
                if (_pattern.EndsWith("*") && file.StartsWith(_pattern.Substring(0, _pattern.Length - 2))) return true;
                return Regex.IsMatch(file, RegexPattern);
            }
            return false;
        }
    }

    static class Program
    {
        private static bool verbose = false;
        private static bool quiet = false;
        private static bool updateCreate = false;
        private static bool updateModify = true;
        private static bool usedefaults = false;
        private static bool quit = false;
        private static readonly List<string> paths = new List<string>();
        private static readonly List<Filter> filters = new List<Filter>();

        static void Main(string[] args)
        {
            GetIni();
            if (args.Length == 0)
            {
                PrintUsage();
                return;
            }

            List<Filter> added = new List<Filter>();
            for (int i = 0; i < args.Length; i++)
                if (args[i].StartsWith("/"))
                {
                    string[] split = args[i].Substring(1).Split('=');
                    switch (split[0])
                    {
                        case "ini":
                            ProcessIni(split[1]);
                            break;
                        case "created":
                            bool.TryParse(split.Length <= 1 ? "true" : split[1].Trim(), out updateCreate);
                            break;
                        case "modified":
                            bool.TryParse(split.Length <= 1 ? "true" : split[1].Trim(), out updateModify);
                            break;
                        case "verbose":
                            bool.TryParse(split.Length <= 1 ? "true" : split[1].Trim(), out verbose);
                            break;
                        case "quiet":
                            bool.TryParse(split.Length <= 1 ? "true" : split[1].Trim(), out quiet);
                            break;
                        case "list":
                            if (added.Count == 0 && !usedefaults)
                                Console.WriteLine("No filters loaded.");
                            else if (!usedefaults)
                                Console.WriteLine("Loaded filters:\n" + string.Join("\n", added.Select(x => x.Pattern)));
                            else if (added.Count == 0)
                                Console.WriteLine("Loaded filters:\n" + string.Join("\n", filters.Select(x => x.Pattern)));
                            else
                                Console.WriteLine("Loaded filters:\n" + string.Join("\n", filters.Select(x => x.Pattern).ToList().
                                    Union(added.Select(x => x.Pattern)).Distinct()));
                            quit = true;
                            break;
                        case "filter":
                            if (split.Length <= 1)                            {
                                if (!quiet)
                                    Console.WriteLine("Filter value missing!");
                                Environment.Exit(1);
                            }
                            string match = split[1].Trim(new char[] { ' ', '\'' });
                            if (!IsValidFilename(match)) 
                            {
                                if (!quiet)
                                    Console.WriteLine("Invalid filter: " + match);
                                Environment.Exit(1);
                            }
                            filters.Add(new Filter(match));
                            break;
                        case "defaults":
                            usedefaults = true;
                            break;
                        case "save":
                            if (split.Length > 1)
                                WriteIni(split[1].Trim(new char[] { ' ', '\'' }));
                            else
                                WriteIni("");
                            quit = true;
                            break;
                    }
                }
                else paths.Add(args[i]);

            if (!updateCreate && !updateModify)
            {
                if (!quiet)
                    Console.WriteLine("Nothing to do, neiter updating creation time or modified time is selected.");
                quit = true;
            }

            if (!quit)
                foreach (string path in paths)
                {
                    try
                    {
                        string tmp = Path.GetFullPath(path);
                        if (!Directory.Exists(tmp))
                        {
                            if (!quiet)
                                Console.WriteLine("Directory does not exist: " + path);
                            continue;
                        }
                    }
                    catch
                    {
                        if (!quiet)
                            Console.WriteLine("Invalid directory: " + path);
                        continue;
                    }

                    if (!quiet)
                    {
                        Console.WriteLine("");
                        if (updateCreate && updateModify)
                            Console.WriteLine($"Updating created and modified timestamps for '{path}' and the directories therein.");
                        else if (updateCreate)
                            Console.WriteLine($"Updating created timestamps for '{path}' and the directories therein.");
                        else
                            Console.WriteLine($"Updating created and modified timestamps for '{path}' and the directories therein.");
                    }
                    DateTime creation = DateTime.MaxValue;
                    DateTime modification = DateTime.MinValue;
                    ParseDirectory(path, ref creation, ref modification);
                }

            if (!quiet)
            {
                Console.WriteLine("");
                Console.WriteLine("Press any key to continue...");
            }
            Console.ReadKey();
            Environment.Exit(0);
        }

        static void PrintUsage()
        {
            Console.WriteLine($"Usage: {AppDomain.CurrentDomain.FriendlyName} <path>");

            Console.WriteLine("Options:");
            Console.WriteLine(@"/ini=<filename>         Configuration file (default in the folder of the program or %localappdata%\Locivir\).");
            Console.WriteLine("/created=true|false     Update created timestamp (default=false).No value is 'true'.");
            Console.WriteLine("/modified=true|false    Update modified timestamp (default=true). No value is 'true'.");
            Console.WriteLine("/verbose=true|false     Verbose output (default=false). No value is 'true'.");
            Console.WriteLine("/quiet=true|false       Suppress output (default=false). No value is 'true'.");
            Console.WriteLine("/filter=<pattern>       Apply a filter for files to ignore when checking for dates, can be used multiple times.");
            Console.WriteLine("                        * If /defaults is not supplied this will replace all entries from the configuration file! *");
            Console.WriteLine("/defaults               Include the defaults loaded from the configuration file.");
            Console.WriteLine("/list                   List the current loaded list of filters and exit.");
            Console.WriteLine("/save=<filename>        Save the current loaded list of filters to the configuration file and exit.");
            Console.WriteLine("                        (If no filename is given the default locations will be tried.)");
        }

        static void WriteIni(string file)
        {
            List<string> lines = new List<string>();
            lines.Add("# Configuration file for UpdateFolderDates");
            lines.Add("#-----------------------------------------------------------------------");
            lines.Add("# Lines starting with # are ignored.");
            lines.Add("# Command line arguments override any settings from here.");
            lines.Add("# Command line filters with spaces need to be in single quotes: 'using spaces'");
            lines.Add("# Command line filters that use the '*' wildcard require single quotes: '*filter*'");
            lines.Add("#-----------------------------------------------------------------------");
            lines.Add("# Command line arguments:");
            lines.Add("#-----------------------------------------------------------------------");
            lines.Add("# Automatically include defaults from the configuration file if adding extra filters.");
            if (usedefaults) lines.Add("/defaults"); else lines.Add("#/defaults");
            lines.Add("# Update created timestamp: true|false. Default is 'false', no value is 'true'.");
            lines.Add($"/created={updateCreate.ToString().ToLower()}");
            lines.Add("# Update modified timestamp: true|false. Default is 'true', no value is 'true'.");
            lines.Add($"/modified={updateCreate.ToString().ToLower()}");
            lines.Add("# Verbose output: true|false. Default is 'false', no value is 'true'.");
            lines.Add($"/verbose={verbose.ToString().ToLower()}");
            lines.Add("# Suppress output: true|false. Default is 'false', no value is 'true'.");
            lines.Add($"/quiet={quiet.ToString().ToLower()}");
            lines.Add("#-----------------------------------------------------------------------");
            lines.Add("# Place filters below here (single quotes are optional, not required):");
            lines.Add("#-----------------------------------------------------------------------");

            if (filters.Count == 0)
            {
                lines.Add("# Example filters:");
                lines.Add("#deskop.ini");
                lines.Add("#Thumbs.db");
                lines.Add("#'*.bak'");
                lines.Add("#*.tmp");
                lines.Add("#*IMPORTANT*");
                lines.Add("#'*(keep).jpg'");
                lines.Add("#-----------------------------------------------------------------------");
                lines.Add("deskop.ini");
                lines.Add("Thumbs.db");
            }
            lines.AddRange(filters.Select(f => f.Pattern));

            if (!string.IsNullOrEmpty(file))
            {
                try { File.WriteAllLines(file, lines); return; }
                catch { Console.WriteLine("Can not write INI-file."); return; }
            }

            string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            string ini = Path.GetFileNameWithoutExtension(exe) + ".ini";
            string dir = Path.GetDirectoryName(ini);
            file = Path.Combine(dir, ini);

            try { File.WriteAllLines(file, lines); return; } 
            catch { } 
            
            dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            file = Path.Combine(dir, "Locivir", ini);
            
            try { File.WriteAllLines(file, lines); return; }
            catch { Console.WriteLine("Can not write INI-file."); return; }
        }

        static void GetIni()
        {
            string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            string ini = Path.GetFileNameWithoutExtension(exe) + ".ini";
            string dir = Path.GetDirectoryName(ini);
            string file = Path.Combine(dir, ini);

            if (!File.Exists(file))
            {
                dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                file = Path.Combine(dir, "Locivir", ini);
            }

            if (!File.Exists(file)) return;
            ProcessIni(file);
        }

        static void ProcessIni(string file)
        {
            filters.Clear();
            string[] lines;
            try { lines = File.ReadAllLines(file); }             
            catch { Console.WriteLine("Can not read INI-file."); return; }
            // Split all text by new lines and commas, but ignore commas in quotes
            //string pattern = ",(?=(?:[^\"]*\"[^\"]*\")*[^\"]*$)|\n";
            //string[] lines = Regex.Split(config, pattern);
            bool error = false;
            foreach (string item in lines)
            {
                if (item.StartsWith("#")) continue;
                if (item.StartsWith("/"))
                {
                    string[] split = item.Substring(1).Split('=');
                    switch (split[0])
                    {
                        case "defaults":
                            usedefaults = true;
                            break;
                        case "created":
                            bool.TryParse(split.Length <= 1 ? "true" : split[1].Trim(), out updateCreate);
                            break;
                        case "modified":
                            bool.TryParse(split.Length <= 1 ? "true" : split[1].Trim(), out updateModify);
                            break;
                        case "verbose":
                            bool.TryParse(split.Length <= 1 ? "true" : split[1].Trim(), out verbose);
                            break;
                        case "quiet":
                            bool.TryParse(split.Length <= 1 ? "true" : split[1].Trim(), out quiet);
                            break;
                        default:
                            error = true;
                            break;
                    }
                    continue;
                }

                string match = item.Trim(new char[] { ' ', '\'' });
                if (!IsValidFilename(match)) error = true;
                else filters.Add(new Filter(match));
            }

            if (error && !quiet) Console.WriteLine("Ignoring invalid configuration entries in {0}", file);
        }

        static bool IsValidFilename(string filename)
        {
            // Define a regex pattern for valid filenames with wildcard characters
            string pattern = @"^[\w\-. ]*(\*|\?)*[\w\-. \\]*$";

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