using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace UpdateFolderDates
{
    /// <summary>Deterministic process exit codes. The contract is documented in the usage text.</summary>
    internal enum ExitCode
    {
        Success = 0,    // requested action completed with no failures (incl. dry-run/list/save)
        UsageError = 1, // command-line / path / configuration / safety-validation error; no writes
        Partial = 2,    // completed only partially: one or more entries failed or were incomplete
        Declined = 3,   // user declined confirmation; no writes
        FatalError = 4  // unexpected fatal error
    }

    /// <summary>
    /// A single exclusion filter.
    ///
    /// Grammar: '*' matches zero or more characters, '?' matches exactly one character. Matching is
    /// anchored to the entire applicable name (or the entire normalized path when the pattern itself
    /// contains a separator) and is case-insensitive (Windows semantics). A trailing '\' or '/' marks
    /// a directory-only filter; otherwise the filter is file-only. A file filter never matches a
    /// directory and a directory filter never matches a file. Entry type is supplied by the caller,
    /// so matching never touches the filesystem (no File.Exists / Directory.Exists dependency).
    /// </summary>
    internal sealed class Filter
    {
        private readonly string _core;        // pattern without any trailing separator (for round-tripping)
        private readonly bool _directoryOnly; // had a trailing '\' or '/'
        private readonly bool _pathScoped;    // contains a separator -> match against the full path
        private readonly Regex _regex;        // anchored, case-insensitive

        public Filter(string pattern)
        {
            string trimmed = (pattern ?? string.Empty).Trim();
            _directoryOnly = trimmed.EndsWith("\\", StringComparison.Ordinal)
                          || trimmed.EndsWith("/", StringComparison.Ordinal);
            _core = _directoryOnly ? trimmed.TrimEnd('\\', '/') : trimmed;
            string normalized = _core.Replace('/', '\\');
            _pathScoped = normalized.IndexOf('\\') >= 0;
            _regex = new Regex("^" + GlobToRegex(normalized) + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <summary>True when <paramref name="name"/> (or full path, for path-scoped patterns) is excluded.</summary>
        public bool Matches(string fullPath, string name, bool isDirectory)
        {
            if (_directoryOnly && !isDirectory) return false; // directory filter must not match a file
            if (!_directoryOnly && isDirectory) return false; // file filter must not match a directory
            string target = _pathScoped ? (fullPath ?? string.Empty).Replace('/', '\\') : (name ?? string.Empty);
            return _regex.IsMatch(target);
        }

        /// <summary>Serialize back to an INI line, quoting when spaces or wildcards are present.</summary>
        public string ToIniLine()
        {
            string s = _core + (_directoryOnly ? "\\" : string.Empty);
            bool needsQuote = s.IndexOf(' ') >= 0 || s.IndexOf('*') >= 0 || s.IndexOf('?') >= 0;
            return needsQuote ? "'" + s + "'" : s;
        }

        /// <summary>Canonical key for case-insensitive de-duplication (keeps file vs. directory distinct).</summary>
        public string DedupKey()
        {
            return (_core.Replace('/', '\\') + (_directoryOnly ? "\\" : string.Empty)).ToLowerInvariant();
        }

        private static string GlobToRegex(string glob)
        {
            StringBuilder sb = new StringBuilder(glob.Length * 2);
            foreach (char c in glob)
            {
                if (c == '*') sb.Append(".*");
                else if (c == '?') sb.Append('.');
                else sb.Append(Regex.Escape(c.ToString()));
            }
            return sb.ToString();
        }

        /// <summary>Reject empty patterns and characters that are never valid in a Windows name.</summary>
        public static bool IsValidPattern(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return false;
            foreach (char c in pattern)
            {
                if (c < 0x20) return false;                                   // control characters
                if (c == '<' || c == '>' || c == '"' || c == '|') return false;
            }
            return true;
        }
    }

    /// <summary>Raw command-line options. Nullable booleans distinguish "unset" from an explicit value.</summary>
    internal sealed class CliOptions
    {
        public bool? Created, Modified, Verbose, Quiet, DryRun, Yes, AllowRoot, Defaults;
        public string IniPath;
        public bool IniSpecified;
        public readonly List<string> Filters = new List<string>();
        public bool ListRequested;
        public bool SaveRequested;
        public bool SaveHasValue;
        public string SavePath;
        public readonly List<string> Paths = new List<string>();
        public readonly List<string> Errors = new List<string>();
    }

    /// <summary>Options and filters loaded from an INI file (all optional).</summary>
    internal sealed class IniConfig
    {
        public bool? Created, Modified, Verbose, Quiet, DryRun, AllowRoot, Defaults;
        public readonly List<string> Filters = new List<string>();
    }

    /// <summary>Fully resolved options after applying defaults, INI, and command-line overrides.</summary>
    internal sealed class EffectiveOptions
    {
        public bool Created, Modified, Verbose, Quiet, DryRun, Yes, AllowRoot, Defaults;
        public List<Filter> Filters = new List<Filter>();
    }

    /// <summary>Result of scanning one directory subtree.</summary>
    internal sealed class ScanResult
    {
        public DateTime CreationMin = DateTime.MaxValue;     // earliest creation candidate
        public DateTime ModificationMax = DateTime.MinValue; // latest last-write time
        public bool HasData;                                 // at least one eligible file contributed
        public bool Complete = true;                         // subtree fully enumerated and readable
    }

    /// <summary>A precomputed timestamp change for one directory.</summary>
    internal sealed class DirUpdate
    {
        public string Path;
        public int Depth;
        public DateTime? NewCreation;
        public DateTime? NewModification;
        public DateTime? CurrentCreation;
        public DateTime? CurrentModification;
    }

    /// <summary>
    /// Output routing. Informational and verbose output go to stdout and are suppressed by /quiet.
    /// Actionable errors always go to stderr, even under /quiet, so automation can observe failures.
    /// </summary>
    internal sealed class Reporter
    {
        private readonly bool _quiet;
        private readonly bool _verbose;
        public Reporter(bool quiet, bool verbose) { _quiet = quiet; _verbose = verbose; }
        public void Info(string message) { if (!_quiet) Console.Out.WriteLine(message); }
        public void Verbose(string message) { if (!_quiet && _verbose) Console.Out.WriteLine(message); }
        public void Error(string message) { Console.Error.WriteLine(message); }
    }

    /// <summary>Shared state threaded through the recursive scan: options, plan, and counters.</summary>
    internal sealed class ScanContext
    {
        public EffectiveOptions Opt;
        public Reporter Report;
        public readonly List<DirUpdate> Plan = new List<DirUpdate>();
        public int ExcludedByFilter;
        public int SkippedReparse;
        public int IncompleteDirs;
        public int ErrorCount;

        public void RecordError(string path, string action, Exception ex)
        {
            ErrorCount++;
            Report.Error(string.Format("ERROR: failed to {0} for '{1}': {2}: {3}",
                action, path, ex.GetType().Name, ex.Message));
        }
    }

    internal static class Program
    {
        // Creation times before this sentinel are treated as invalid (legacy/FAT) and replaced by the
        // file's last-write time. Built numerically so it never depends on the current culture.
        private static readonly DateTime InvalidCreationCutoff = new DateTime(1980, 1, 1);

        private static readonly char[] Separators =
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        private static readonly char[] TrimChars = new[] { ' ', '\'' };

        private static int Main(string[] args)
        {
            try
            {
                return (int)Run(args);
            }
            catch (Exception ex)
            {
                // Never mask an unexpected failure as success.
                Console.Error.WriteLine("FATAL: unexpected error: " + ex);
                return (int)ExitCode.FatalError;
            }
        }

        private static ExitCode Run(string[] args)
        {
            // 1. Parse command-line syntax (no options applied yet).
            CliOptions cli = ParseArgs(args);
            if (cli.Errors.Count > 0)
            {
                foreach (string e in cli.Errors) Console.Error.WriteLine("ERROR: " + e);
                Console.Error.WriteLine("Run without arguments for usage.");
                return ExitCode.UsageError;
            }

            if (args.Length == 0)
            {
                PrintUsage();
                return ExitCode.Success;
            }

            // 2. Resolve and load the selected/default INI.
            IniConfig ini = new IniConfig();
            if (cli.IniSpecified)
            {
                if (!LoadIni(cli.IniPath, ini, true))
                    return ExitCode.UsageError; // explicit configuration error already reported
            }
            else
            {
                string defaultIni = FindDefaultIni();
                if (defaultIni != null) LoadIni(defaultIni, ini, false);
            }

            // 3./4. Apply command-line overrides (order-independent) and resolve the effective filter set.
            EffectiveOptions opt = Resolve(cli, ini);
            Reporter report = new Reporter(opt.Quiet, opt.Verbose);

            // Action modes never traverse or modify directories, and never block for input.
            if (cli.ListRequested || cli.SaveRequested)
            {
                ExitCode rc = ExitCode.Success;
                if (cli.ListRequested) DoList(opt, report);
                if (cli.SaveRequested && !DoSave(cli, opt, report)) rc = ExitCode.UsageError;
                return rc;
            }

            if (cli.Paths.Count == 0)
            {
                Console.Error.WriteLine("ERROR: no target directory specified.");
                Console.Error.WriteLine("Run without arguments for usage.");
                return ExitCode.UsageError;
            }

            if (!opt.Created && !opt.Modified)
            {
                report.Info("Nothing to do: neither /created nor /modified is enabled.");
                return ExitCode.Success;
            }

            // 5. Validate every root up-front. Any validation failure aborts with no writes.
            List<string> roots = new List<string>();
            List<string> validationErrors = new List<string>();
            foreach (string p in cli.Paths)
            {
                string full;
                string err;
                if (ValidateRoot(p, opt.AllowRoot, out full, out err)) roots.Add(full);
                else validationErrors.Add(err);
            }
            if (validationErrors.Count > 0)
            {
                foreach (string e in validationErrors) Console.Error.WriteLine("ERROR: " + e);
                return ExitCode.UsageError;
            }

            // 6. Scan the full in-scope tree and build the update plan (no timestamps written yet).
            ScanContext ctx = new ScanContext { Opt = opt, Report = report };
            foreach (string root in roots) Scan(root, 0, ctx);

            // 7. Show the plan summary.
            PrintPlanSummary(ctx, roots);
            bool anyProblem = ctx.ErrorCount > 0 || ctx.IncompleteDirs > 0;

            if (ctx.Plan.Count == 0)
            {
                report.Info("No directories require updating.");
                return anyProblem ? ExitCode.Partial : ExitCode.Success;
            }

            // Dry-run: report the plan, change nothing, require no confirmation.
            if (opt.DryRun)
            {
                foreach (DirUpdate u in ctx.Plan.OrderByDescending(x => x.Depth))
                    report.Info("Would update: " + DescribePlanned(u));
                return anyProblem ? ExitCode.Partial : ExitCode.Success;
            }

            // Confirmation gate for a real, timestamp-changing run. Default answer is no.
            if (!opt.Yes)
            {
                if (opt.Quiet || Console.IsInputRedirected)
                {
                    Console.Error.WriteLine("ERROR: refusing to modify timestamps without confirmation. " +
                        "Pass /yes for non-interactive execution, or run interactively.");
                    return ExitCode.UsageError;
                }
                Console.Out.Write(string.Format("Proceed with updating {0} director{1}? [y/N]: ",
                    ctx.Plan.Count, ctx.Plan.Count == 1 ? "y" : "ies"));
                string response = Console.ReadLine();
                string r = response == null ? string.Empty : response.Trim();
                if (!r.Equals("y", StringComparison.OrdinalIgnoreCase) &&
                    !r.Equals("yes", StringComparison.OrdinalIgnoreCase))
                {
                    report.Info("Aborted. No timestamps were changed.");
                    return ExitCode.Declined;
                }
            }

            // 8. Apply the precomputed plan deepest-first, creation before modification.
            int failed = ApplyPlan(ctx);

            // 9. Deterministic exit code.
            return (failed > 0 || anyProblem) ? ExitCode.Partial : ExitCode.Success;
        }

        private static CliOptions ParseArgs(string[] args)
        {
            CliOptions o = new CliOptions();
            foreach (string raw in args)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                if (raw[0] != '/') { o.Paths.Add(raw); continue; }

                string body = raw.Substring(1);
                int eq = body.IndexOf('=');                       // split on the FIRST '=' only
                string key = (eq < 0 ? body : body.Substring(0, eq)).Trim().ToLowerInvariant();
                bool hasValue = eq >= 0;
                string value = eq < 0 ? null : body.Substring(eq + 1);

                switch (key)
                {
                    case "created": ParseBool(o, "created", hasValue, value, v => o.Created = v); break;
                    case "modified": ParseBool(o, "modified", hasValue, value, v => o.Modified = v); break;
                    case "verbose": ParseBool(o, "verbose", hasValue, value, v => o.Verbose = v); break;
                    case "quiet": ParseBool(o, "quiet", hasValue, value, v => o.Quiet = v); break;
                    case "dryrun": ParseBool(o, "dryrun", hasValue, value, v => o.DryRun = v); break;
                    case "yes": ParseBool(o, "yes", hasValue, value, v => o.Yes = v); break;
                    case "allowroot": ParseBool(o, "allowroot", hasValue, value, v => o.AllowRoot = v); break;
                    case "defaults": ParseBool(o, "defaults", hasValue, value, v => o.Defaults = v); break;
                    case "ini":
                        if (!hasValue || string.IsNullOrWhiteSpace(value))
                            o.Errors.Add("/ini requires a non-empty value: /ini=<path>");
                        else { o.IniPath = value.Trim(TrimChars); o.IniSpecified = true; }
                        break;
                    case "filter":
                        if (!hasValue || string.IsNullOrWhiteSpace(value))
                        {
                            o.Errors.Add("/filter requires a non-empty value: /filter=<pattern>");
                        }
                        else
                        {
                            string pat = value.Trim(TrimChars);
                            if (!Filter.IsValidPattern(pat)) o.Errors.Add("invalid filter pattern: " + pat);
                            else o.Filters.Add(pat);
                        }
                        break;
                    case "list":
                        if (hasValue) o.Errors.Add("/list does not take a value.");
                        o.ListRequested = true;
                        break;
                    case "save":
                        o.SaveRequested = true;
                        if (hasValue)
                        {
                            o.SaveHasValue = true;
                            o.SavePath = value.Trim(TrimChars);
                            if (string.IsNullOrWhiteSpace(o.SavePath))
                                o.Errors.Add("/save was given an empty path.");
                        }
                        break;
                    default:
                        o.Errors.Add("unknown option: /" + key);
                        break;
                }
            }
            return o;
        }

        // Boolean options accept only true/false; a missing value means true. Anything else is an error.
        private static void ParseBool(CliOptions o, string name, bool hasValue, string value, Action<bool> set)
        {
            if (!hasValue) { set(true); return; }
            string t = value.Trim();
            if (t.Equals("true", StringComparison.OrdinalIgnoreCase)) set(true);
            else if (t.Equals("false", StringComparison.OrdinalIgnoreCase)) set(false);
            else o.Errors.Add(string.Format("invalid value for /{0}: '{1}' (use true or false)", name, value));
        }

        private static EffectiveOptions Resolve(CliOptions cli, IniConfig ini)
        {
            EffectiveOptions o = new EffectiveOptions();
            o.Created = cli.Created ?? ini.Created ?? false;
            o.Modified = cli.Modified ?? ini.Modified ?? true;
            o.Verbose = cli.Verbose ?? ini.Verbose ?? false;
            o.Quiet = cli.Quiet ?? ini.Quiet ?? false;
            o.DryRun = cli.DryRun ?? ini.DryRun ?? false;
            o.AllowRoot = cli.AllowRoot ?? ini.AllowRoot ?? false;
            o.Defaults = cli.Defaults ?? ini.Defaults ?? false;
            o.Yes = cli.Yes ?? false; // confirmation override: command-line only, never persisted

            // Filter resolution honoring the /defaults contract:
            //   no CLI filters            -> keep INI filters
            //   CLI filters, defaults=false-> CLI filters REPLACE INI filters
            //   CLI filters, defaults=true -> CLI filters MERGE with INI filters
            List<string> patterns;
            if (cli.Filters.Count == 0)
            {
                patterns = new List<string>(ini.Filters);
            }
            else if (o.Defaults)
            {
                patterns = new List<string>(ini.Filters);
                patterns.AddRange(cli.Filters);
            }
            else
            {
                patterns = new List<string>(cli.Filters);
            }

            o.Filters = DedupFilters(patterns);
            return o;
        }

        // Case-insensitive de-duplication that preserves first-seen order.
        private static List<Filter> DedupFilters(IEnumerable<string> patterns)
        {
            List<Filter> result = new List<Filter>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string p in patterns)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                Filter f = new Filter(p);
                if (seen.Add(f.DedupKey())) result.Add(f);
            }
            return result;
        }

        // Full-path resolution + safety checks for a requested root. Returns false with a message on failure.
        private static bool ValidateRoot(string path, bool allowRoot, out string full, out string error)
        {
            full = null;
            error = null;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex) when (IsExpected(ex))
            {
                error = string.Format("invalid path '{0}': {1}", path, ex.Message);
                return false;
            }

            FileAttributes attr;
            try { attr = File.GetAttributes(full); }
            catch (FileNotFoundException) { error = "directory does not exist: " + full; return false; }
            catch (DirectoryNotFoundException) { error = "directory does not exist: " + full; return false; }
            catch (Exception ex) when (IsExpected(ex))
            {
                error = string.Format("cannot access '{0}': {1}", full, ex.Message);
                return false;
            }

            if ((attr & FileAttributes.Directory) == 0)
            {
                error = "not a directory: " + full;
                return false;
            }
            if ((attr & FileAttributes.ReparsePoint) != 0)
            {
                error = "root path is a reparse point (junction/symlink); refusing to follow: " + full;
                return false;
            }
            if (IsFilesystemRoot(full) && !allowRoot)
            {
                error = string.Format(
                    "refusing to operate on filesystem root '{0}'. Pass /allowroot=true to override.", full);
                return false;
            }
            return true;
        }

        private static bool IsFilesystemRoot(string fullPath)
        {
            string root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)) return false;
            string a = fullPath.TrimEnd(Separators);
            string b = root.TrimEnd(Separators);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Recursively scans <paramref name="dir"/>, accumulating the earliest creation candidate and the
        /// latest last-write time over eligible descendant files, and appending a plan entry when the
        /// directory has eligible data and its subtree scanned completely. Extrema propagate upward; an
        /// incomplete subtree marks all dependent ancestors incomplete so they are never updated from
        /// partial data. Reparse points and filtered entries are intentional exclusions, not failures.
        /// No timestamps are written here (scan strictly precedes any write).
        /// </summary>
        private static ScanResult Scan(string dir, int depth, ScanContext ctx)
        {
            ScanResult result = new ScanResult();

            string[] subdirs;
            string[] files;
            try { subdirs = Directory.GetDirectories(dir); }
            catch (Exception ex) when (IsExpected(ex))
            {
                ctx.RecordError(dir, "enumerate subdirectories", ex);
                result.Complete = false;
                return result;
            }
            try { files = Directory.GetFiles(dir); }
            catch (Exception ex) when (IsExpected(ex))
            {
                // Files are unreadable, but any subdirectories already enumerated can still be scanned
                // and updated. Mark this directory incomplete (so it is not updated) and keep going.
                ctx.RecordError(dir, "enumerate files", ex);
                result.Complete = false;
                files = new string[0];
            }

            foreach (string sub in subdirs)
            {
                string name = LeafName(sub);

                if (IsExcludedByFilter(ctx, sub, name, true))
                {
                    ctx.ExcludedByFilter++;
                    ctx.Report.Verbose("Excluded directory (filter): " + sub);
                    continue; // whole subtree intentionally excluded
                }

                FileAttributes attr;
                try { attr = File.GetAttributes(sub); }
                catch (Exception ex) when (IsExpected(ex))
                {
                    ctx.RecordError(sub, "read directory attributes", ex);
                    result.Complete = false; // this subtree's data is missing
                    continue;
                }

                // Reparse-point policy: never follow junctions/symlinks/mount points. This keeps the scan
                // inside the requested tree and prevents cycles from causing infinite recursion.
                if ((attr & FileAttributes.ReparsePoint) != 0)
                {
                    ctx.SkippedReparse++;
                    ctx.Report.Verbose("Skipped reparse point (not followed): " + sub);
                    continue; // intentional exclusion, not an incomplete scan
                }

                ScanResult child = Scan(sub, depth + 1, ctx);
                if (child.HasData)
                {
                    result.HasData = true;
                    if (child.CreationMin < result.CreationMin) result.CreationMin = child.CreationMin;
                    if (child.ModificationMax > result.ModificationMax) result.ModificationMax = child.ModificationMax;
                }
                if (!child.Complete) result.Complete = false;
            }

            foreach (string file in files)
            {
                string name = LeafName(file);

                if (IsBuiltInExcluded(name))
                {
                    ctx.Report.Verbose("Excluded file (built-in): " + file);
                    continue;
                }
                if (IsExcludedByFilter(ctx, file, name, false))
                {
                    ctx.ExcludedByFilter++;
                    ctx.Report.Verbose("Excluded file (filter): " + file);
                    continue;
                }

                FileAttributes fattr;
                try { fattr = File.GetAttributes(file); }
                catch (Exception ex) when (IsExpected(ex))
                {
                    ctx.RecordError(file, "read file attributes", ex);
                    result.Complete = false;
                    continue;
                }

                // Skip file reparse points too: reading through them could pull in an out-of-scope target.
                if ((fattr & FileAttributes.ReparsePoint) != 0)
                {
                    ctx.SkippedReparse++;
                    ctx.Report.Verbose("Skipped file reparse point (target out of scope): " + file);
                    continue;
                }

                DateTime created, modified;
                try
                {
                    created = File.GetCreationTime(file);
                    modified = File.GetLastWriteTime(file);
                }
                catch (Exception ex) when (IsExpected(ex))
                {
                    ctx.RecordError(file, "read file timestamps", ex);
                    result.Complete = false; // a missing contribution would corrupt the extrema
                    continue;
                }

                // Creation-time calculation:
                //  - a creation time before the 1980 cutoff is invalid; use the last-write time instead;
                //  - both the (validated) creation time AND the last-write time are creation candidates, so
                //    an older last-write time can pull the directory's creation earlier than the file's own
                //    creation time (intentional);
                //  - the modification target is simply the latest last-write time.
                DateTime effectiveCreation = created < InvalidCreationCutoff ? modified : created;
                if (effectiveCreation < result.CreationMin) result.CreationMin = effectiveCreation;
                if (modified < result.CreationMin) result.CreationMin = modified;
                if (modified > result.ModificationMax) result.ModificationMax = modified;
                result.HasData = true;
            }

            BuildPlanEntry(dir, depth, ctx, result);
            return result;
        }

        private static void BuildPlanEntry(string dir, int depth, ScanContext ctx, ScanResult result)
        {
            if (!result.HasData)
                return; // empty or fully-excluded directory: leave unchanged

            // A filesystem/volume root's own timestamps cannot be set (.NET throws "Path must not be a
            // drive"). Only reachable with /allowroot; process its contents but never attempt the root
            // itself, so a permitted root run is not reported as a spurious partial failure.
            if (IsFilesystemRoot(dir))
            {
                ctx.Report.Verbose("Filesystem root: its own timestamps cannot be set; contents updated only: " + dir);
                return;
            }

            if (!result.Complete)
            {
                // Do not compute a parent timestamp from a partial subtree.
                ctx.IncompleteDirs++;
                ctx.Report.Error("SKIPPED (incomplete subtree; not updated from partial data): " + dir);
                return;
            }

            DateTime? newCreation = null, newModification = null;
            DateTime? curCreation = null, curModification = null;

            if (ctx.Opt.Created)
            {
                try
                {
                    DateTime cur = Directory.GetCreationTime(dir);
                    curCreation = cur;
                    if (cur != result.CreationMin) newCreation = result.CreationMin;
                }
                catch (Exception ex) when (IsExpected(ex))
                {
                    // Cannot plan this directory safely; its subtree extrema remain valid for ancestors.
                    ctx.RecordError(dir, "read directory creation time", ex);
                    return;
                }
            }
            if (ctx.Opt.Modified)
            {
                try
                {
                    DateTime cur = Directory.GetLastWriteTime(dir);
                    curModification = cur;
                    if (cur != result.ModificationMax) newModification = result.ModificationMax;
                }
                catch (Exception ex) when (IsExpected(ex))
                {
                    ctx.RecordError(dir, "read directory modified time", ex);
                    return;
                }
            }

            if (newCreation.HasValue || newModification.HasValue)
            {
                ctx.Plan.Add(new DirUpdate
                {
                    Path = dir,
                    Depth = depth,
                    NewCreation = newCreation,
                    NewModification = newModification,
                    CurrentCreation = curCreation,
                    CurrentModification = curModification
                });
            }
        }

        // Applies the plan deepest-first; sets creation before modification. Returns the failure count.
        private static int ApplyPlan(ScanContext ctx)
        {
            int failed = 0;
            foreach (DirUpdate u in ctx.Plan.OrderByDescending(x => x.Depth))
            {
                bool ok = true;
                if (u.NewCreation.HasValue)
                {
                    try { Directory.SetCreationTime(u.Path, u.NewCreation.Value); }
                    catch (Exception ex) when (IsExpected(ex)) { ctx.RecordError(u.Path, "set creation time", ex); ok = false; }
                }
                if (u.NewModification.HasValue)
                {
                    try { Directory.SetLastWriteTime(u.Path, u.NewModification.Value); }
                    catch (Exception ex) when (IsExpected(ex)) { ctx.RecordError(u.Path, "set modified time", ex); ok = false; }
                }
                if (ok) ctx.Report.Info("Updated: " + DescribePlanned(u));
                else failed++;
            }
            return failed;
        }

        private static string DescribePlanned(DirUpdate u)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(u.Path);
            if (u.NewCreation.HasValue)
                sb.Append(string.Format("  created {0} => {1}", Fmt(u.CurrentCreation), Fmt(u.NewCreation)));
            if (u.NewModification.HasValue)
                sb.Append(string.Format("  modified {0} => {1}", Fmt(u.CurrentModification), Fmt(u.NewModification)));
            return sb.ToString();
        }

        private static string Fmt(DateTime? d)
        {
            return d.HasValue ? d.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "?";
        }

        // Extracts the final path segment without ever throwing, so one malformed entry name cannot
        // abort the scan; on the rare failure the raw path is used for matching instead.
        private static string LeafName(string path)
        {
            try { return Path.GetFileName(path.TrimEnd(Separators)); }
            catch { return path; }
        }

        private static bool IsExcludedByFilter(ScanContext ctx, string fullPath, string name, bool isDirectory)
        {
            foreach (Filter f in ctx.Opt.Filters)
                if (f.Matches(fullPath, name, isDirectory)) return true;
            return false;
        }

        // thumbs.db and desktop.ini are always excluded, independent of configurable filters.
        private static bool IsBuiltInExcluded(string name)
        {
            return name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase)
                || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
        }

        // Expected, per-entry filesystem failures we handle locally without aborting the whole run.
        private static bool IsExpected(Exception ex)
        {
            return ex is UnauthorizedAccessException
                || ex is PathTooLongException
                || ex is IOException            // includes FileNotFoundException / DirectoryNotFoundException
                || ex is SecurityException
                || ex is ArgumentException      // invalid path characters, etc.
                || ex is NotSupportedException;
        }

        private static void PrintPlanSummary(ScanContext ctx, List<string> roots)
        {
            EffectiveOptions o = ctx.Opt;
            string modes = o.Created && o.Modified ? "created + modified" : (o.Created ? "created" : "modified");
            ctx.Report.Info(string.Empty);
            ctx.Report.Info("Plan summary" + (o.DryRun ? " (dry-run)" : string.Empty) + ":");
            ctx.Report.Info("  Roots: " + string.Join(", ", roots));
            ctx.Report.Info("  Timestamp modes: " + modes);
            ctx.Report.Info("  Planned directory updates: " + ctx.Plan.Count);
            ctx.Report.Info("  Excluded by filter: " + ctx.ExcludedByFilter);
            ctx.Report.Info("  Skipped reparse points: " + ctx.SkippedReparse);
            ctx.Report.Info("  Directories skipped (incomplete/inaccessible): " + ctx.IncompleteDirs);
            ctx.Report.Info("  Errors: " + ctx.ErrorCount);
        }

        /// <summary>
        /// Loads options and filters from an INI file. For an explicit /ini path, a missing/unreadable
        /// file or any invalid entry is a hard error (returns false); for the implicit default INI,
        /// invalid entries are warnings and loading continues.
        /// </summary>
        private static bool LoadIni(string path, IniConfig cfg, bool explicitPath)
        {
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception ex) when (IsExpected(ex))
            {
                if (explicitPath)
                {
                    Console.Error.WriteLine(string.Format(
                        "ERROR: cannot read configuration file '{0}': {1}", path, ex.Message));
                    return false;
                }
                return true; // a default INI that cannot be read is simply ignored
            }

            List<string> invalid = new List<string>();
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                if (line[0] == '/')
                {
                    string body = line.Substring(1);
                    int eq = body.IndexOf('=');
                    string key = (eq < 0 ? body : body.Substring(0, eq)).Trim().ToLowerInvariant();
                    bool hasValue = eq >= 0;
                    string value = eq < 0 ? null : body.Substring(eq + 1);
                    if (!ApplyIniOption(cfg, key, hasValue, value)) invalid.Add(line);
                    continue;
                }

                string pat = line.Trim(TrimChars);
                if (!Filter.IsValidPattern(pat)) invalid.Add(line);
                else cfg.Filters.Add(pat);
            }

            if (invalid.Count > 0)
            {
                if (explicitPath)
                {
                    Console.Error.WriteLine(string.Format(
                        "ERROR: invalid entries in configuration file '{0}': {1}",
                        path, string.Join(", ", invalid)));
                    return false;
                }
                Console.Error.WriteLine(string.Format(
                    "WARNING: ignoring invalid entries in configuration file '{0}': {1}",
                    path, string.Join(", ", invalid)));
            }
            return true;
        }

        private static bool ApplyIniOption(IniConfig cfg, string key, bool hasValue, string value)
        {
            switch (key)
            {
                case "created": return TryIniBool(hasValue, value, v => cfg.Created = v);
                case "modified": return TryIniBool(hasValue, value, v => cfg.Modified = v);
                case "verbose": return TryIniBool(hasValue, value, v => cfg.Verbose = v);
                case "quiet": return TryIniBool(hasValue, value, v => cfg.Quiet = v);
                case "dryrun": return TryIniBool(hasValue, value, v => cfg.DryRun = v);
                case "allowroot": return TryIniBool(hasValue, value, v => cfg.AllowRoot = v);
                case "defaults": return TryIniBool(hasValue, value, v => cfg.Defaults = v);
                default: return false; // /ini, /save, /list, /yes and unknown keys are not valid in an INI
            }
        }

        private static bool TryIniBool(bool hasValue, string value, Action<bool> set)
        {
            if (!hasValue) { set(true); return true; }
            string t = value.Trim();
            if (t.Equals("true", StringComparison.OrdinalIgnoreCase)) { set(true); return true; }
            if (t.Equals("false", StringComparison.OrdinalIgnoreCase)) { set(false); return true; }
            return false;
        }

        private static string GetExecutablePath()
        {
            try
            {
                Assembly asm = Assembly.GetEntryAssembly();
                if (asm != null && !string.IsNullOrEmpty(asm.Location)) return asm.Location;
            }
            catch { }
            try
            {
                using (System.Diagnostics.Process p = System.Diagnostics.Process.GetCurrentProcess())
                {
                    if (p.MainModule != null && !string.IsNullOrEmpty(p.MainModule.FileName))
                        return p.MainModule.FileName;
                }
            }
            catch { }
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UpdateFolderDates.exe");
        }

        private static string DefaultIniName()
        {
            return Path.GetFileNameWithoutExtension(GetExecutablePath()) + ".ini";
        }

        // Correct derivation: the directory of the executable itself (not of a stripped file name).
        private static string ExeDirIniPath()
        {
            string exeDir = Path.GetDirectoryName(GetExecutablePath());
            if (string.IsNullOrEmpty(exeDir)) exeDir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(exeDir, DefaultIniName());
        }

        private static string LocalAppDataIniPath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Locivir");
            return Path.Combine(dir, DefaultIniName());
        }

        // Default INI search order: executable directory, then %LOCALAPPDATA%\Locivir.
        private static string FindDefaultIni()
        {
            string exeIni = ExeDirIniPath();
            if (File.Exists(exeIni)) return exeIni;
            string localIni = LocalAppDataIniPath();
            if (File.Exists(localIni)) return localIni;
            return null;
        }

        private static void DoList(EffectiveOptions opt, Reporter report)
        {
            report.Info("Always-excluded built-in system files: thumbs.db, desktop.ini");
            if (opt.Filters.Count == 0)
            {
                report.Info("Configurable filters: (none)");
            }
            else
            {
                report.Info("Configurable filters:");
                foreach (Filter f in opt.Filters) report.Info("  " + f.ToIniLine());
            }
        }

        private static bool DoSave(CliOptions cli, EffectiveOptions opt, Reporter report)
        {
            // /save=<path> -> that path; /save (no value) -> explicit /ini path if given, else defaults.
            List<string> targets = new List<string>();
            if (cli.SaveHasValue) targets.Add(cli.SavePath);
            else if (cli.IniSpecified) targets.Add(cli.IniPath);
            else { targets.Add(ExeDirIniPath()); targets.Add(LocalAppDataIniPath()); }

            string[] content = BuildIniContent(opt);

            for (int i = 0; i < targets.Count; i++)
            {
                string target = targets[i];
                try
                {
                    string dir = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir); // create %LOCALAPPDATA%\Locivir etc. when absent
                    File.WriteAllLines(target, content);
                    report.Info("Configuration saved: " + target);
                    return true;
                }
                catch (Exception ex) when (IsExpected(ex))
                {
                    if (i == targets.Count - 1)
                        Console.Error.WriteLine(string.Format(
                            "ERROR: cannot write configuration file '{0}': {1}", target, ex.Message));
                    // otherwise fall through to the next fallback location
                }
            }
            return false;
        }

        private static string[] BuildIniContent(EffectiveOptions opt)
        {
            List<string> lines = new List<string>();
            lines.Add("# Configuration file for UpdateFolderDates");
            lines.Add("# -----------------------------------------------------------------------");
            lines.Add("# Lines starting with # are ignored.");
            lines.Add("# Command-line arguments override these settings, regardless of order.");
            lines.Add("# thumbs.db and desktop.ini are ALWAYS excluded automatically and need not be listed.");
            lines.Add("#");
            lines.Add("# Filter behavior with /defaults:");
            lines.Add("#   - no command-line /filter                         : the filters below are used.");
            lines.Add("#   - command-line /filter with /defaults=false (default): command-line filters REPLACE these.");
            lines.Add("#   - command-line /filter with /defaults=true        : command-line filters MERGE with these.");
            lines.Add("# -----------------------------------------------------------------------");
            lines.Add("/defaults=" + Lower(opt.Defaults));
            lines.Add("# Update the directory creation timestamp (default false; no value means true).");
            lines.Add("/created=" + Lower(opt.Created));
            lines.Add("# Update the directory modified timestamp (default true; no value means true).");
            lines.Add("/modified=" + Lower(opt.Modified));
            lines.Add("# Verbose diagnostics (does not change what gets updated).");
            lines.Add("/verbose=" + Lower(opt.Verbose));
            lines.Add("# Suppress informational output (errors still go to stderr).");
            lines.Add("/quiet=" + Lower(opt.Quiet));
            lines.Add("# -----------------------------------------------------------------------");
            lines.Add("# Filters (one per line). Wildcards: * = zero or more chars, ? = exactly one char.");
            lines.Add("# A trailing \\ marks a directory-only filter. Single quotes are optional.");
            lines.Add("# -----------------------------------------------------------------------");
            if (opt.Filters.Count == 0)
            {
                // Examples remain commented so an empty effective list never becomes active filters.
                lines.Add("# Example filters (uncomment to use):");
                lines.Add("#'*.bak'");
                lines.Add("#*.tmp");
                lines.Add("#*IMPORTANT*");
                lines.Add("#'*(keep).jpg'");
                lines.Add("#build\\");
            }
            else
            {
                foreach (Filter f in opt.Filters) lines.Add(f.ToIniLine());
            }
            return lines.ToArray();
        }

        private static string Lower(bool b) { return b ? "true" : "false"; }

        private static void PrintUsage()
        {
            string exe = Path.GetFileName(GetExecutablePath());
            Console.WriteLine("UpdateFolderDates - set each directory's timestamps from the files within its subtree.");
            Console.WriteLine();
            Console.WriteLine("A directory's creation time is set to the EARLIEST of every included descendant file's");
            Console.WriteLine("valid creation time and last-write time (a creation time before 1980-01-01 is treated as");
            Console.WriteLine("invalid and replaced by that file's last-write time). A directory's modified time is set");
            Console.WriteLine("to the LATEST last-write time among included descendant files. Because last-write times");
            Console.WriteLine("also count toward creation, a directory's creation time can end up earlier than a file's");
            Console.WriteLine("own creation time. Existing directory timestamps are never used as inputs.");
            Console.WriteLine();
            Console.WriteLine("Usage: " + exe + " [options] <path> [<path> ...]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  /ini=<file>        Configuration file (default: next to the executable, then");
            Console.WriteLine("                     %LOCALAPPDATA%\\Locivir\\).");
            Console.WriteLine("  /created[=bool]    Update creation timestamps (default false; no value = true).");
            Console.WriteLine("  /modified[=bool]   Update modified timestamps (default true; no value = true).");
            Console.WriteLine("  /verbose[=bool]    Extra diagnostics; does NOT change what gets updated.");
            Console.WriteLine("  /quiet[=bool]      Suppress informational output (errors still go to stderr).");
            Console.WriteLine("  /dryrun[=bool]     Scan and show the plan but change nothing.");
            Console.WriteLine("  /yes[=bool]        Skip the confirmation prompt (required for non-interactive runs).");
            Console.WriteLine("  /allowroot[=bool]  Permit operating on a drive or UNC share root (default false).");
            Console.WriteLine("  /filter=<pattern>  Exclude matching entries; repeatable. '*'=any run, '?'=one char.");
            Console.WriteLine("                     A trailing '\\' means directory-only (excludes the whole subtree).");
            Console.WriteLine("  /defaults[=bool]   Merge command-line filters with INI filters instead of replacing.");
            Console.WriteLine("  /list              Show the effective filters and exit (no traversal).");
            Console.WriteLine("  /save[=<file>]     Save the effective configuration and exit (no traversal).");
            Console.WriteLine();
            Console.WriteLine("Filter rules:");
            Console.WriteLine("  * With no command-line /filter, INI filters are used.");
            Console.WriteLine("  * With command-line /filter and /defaults=false (default), they REPLACE INI filters.");
            Console.WriteLine("  * With command-line /filter and /defaults=true, they MERGE with INI filters.");
            Console.WriteLine("  * thumbs.db and desktop.ini are always excluded.");
            Console.WriteLine();
            Console.WriteLine("Exit codes: 0 success; 1 usage/validation error (no writes); 2 partial (some failures");
            Console.WriteLine("            or incomplete subtrees); 3 confirmation declined; 4 unexpected fatal error.");
        }
    }
}
