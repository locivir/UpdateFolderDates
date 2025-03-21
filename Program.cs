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
        static bool updateCreate = false;
        static bool updateModify = true;
        static string path = string.Empty;
        static readonly List<string> ignore = new List<string>(); 

        static void Main(string[] args)
        {
            GetIni();
            if (args.Length == 0) return;
            if (args.Length == 1)
            {
                path = args[0];
            }

            if (args.Length == 2)
            {
                if (args[0] == "false") updateCreate = false;
                else if (args[0] == "true") updateCreate = true;
                else
                {
                    Console.WriteLine("Invalid argument");
                    return;
                }
                path = args[1];
            }
            if (args.Length == 3)
            {
                if (args[0] == "false") updateCreate = false;
                else if (args[0] == "true") updateCreate = true;
                else
                {
                    Console.WriteLine("Invalid argument");
                    return;
                }
                if (args[1] == "false") updateModify = false;
                else if (args[1] == "true") updateModify = true;
                else
                {
                    Console.WriteLine("Invalid argument");
                    return;
                }
                path = args[2];
            }

            if (Directory.Exists(path))
                path = Path.GetFullPath(path);
            
            if (!Directory.Exists(path))
            {
                Console.WriteLine("Invalid directory: " + path);
                return;
            }

            Console.WriteLine("Updating created and modified timestamps for \"" + path + "\" and the directories therein.");
            DateTime creation = DateTime.MaxValue;
            DateTime modification = DateTime.MinValue;
            ParseDirectory(path, ref creation, ref modification);

            Console.ReadKey();
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

            string config;
            try { config = File.ReadAllText(file); } catch { return; }
            bool error = false;
            string pattern = ",(?=(?:[^\"]*\"[^\"]*\")*[^\"]*$)|\n";
            string[] lines = Regex.Split(config, pattern);
                foreach (string item in lines)
                    if (item.StartsWith("#")) continue;
                    else if (!IsValidFilename(item)) error = true;
                    else ignore.Add(item);
            if (error) Console.WriteLine("Ignoring invalid configuration entries in {0}", file);
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

            Console.WriteLine("Parsing: " + cur);
            if (dir_creation != DateTime.MaxValue && Directory.GetCreationTime(cur) != dir_creation)
            {
                if (updateCreate)
                {
                    Console.WriteLine("Updating creation timestamp " + Directory.GetCreationTime(cur).ToString() + " => " + dir_creation.ToString());
                    UpdateDir(cur, dir_creation, false);
                }
                else
                    Console.WriteLine("Different creation timestamp " + Directory.GetCreationTime(cur).ToString() + " => " + dir_creation.ToString());
            }
            if (dir_modification != DateTime.MinValue && Directory.GetLastWriteTime(cur) != dir_modification)
            {
                if (updateModify)
                {
                    Console.WriteLine("Updating modfication timestamp " + Directory.GetLastWriteTime(cur).ToString() + " => " + dir_modification.ToString());
                    UpdateDir(cur, dir_modification, true);
                }
                else
                    Console.WriteLine("Different modfication timestamp " + Directory.GetLastWriteTime(cur).ToString() + " => " + dir_modification.ToString());
            }

            Console.WriteLine();

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

        static void UpdateDir(string dir, DateTime date, bool modified)
        {
            try
            {
                Directory.SetCurrentDirectory(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

                if (modified)
                    Directory.SetLastWriteTime(dir, date);
                else
                    Directory.SetCreationTime(dir, date);
            }
            catch (IOException)
            {
                retry++;
                if (retry < 10)
                {
                    System.Threading.Thread.Sleep(1000);
                    UpdateDir(dir, date, modified);
                    Console.WriteLine("Fail");
                    retry = 0;
                }
            }
        }

        static void UpdateDir(string dir, DateTime creation, DateTime modification)
        {
            try
            {
                Directory.SetCurrentDirectory(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

                Directory.SetLastWriteTime(dir, modification);
                Directory.SetCreationTime(dir, creation);
            }
            catch (IOException)
            {
                retry++;
                if (retry < 10)
                {
                    System.Threading.Thread.Sleep(1000);
                    UpdateDir(dir, creation, modification);
                    Console.WriteLine("Fail");
                    retry = 0;
                }
            }
        }
    }
}