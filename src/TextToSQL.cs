// Offline Text-to-SQL assistant. Compiles with the csc.exe that ships in Windows (C# 5).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

static class Paths
{
    public static readonly string Base = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
    public static readonly string Work = Path.Combine(Path.GetTempPath(), "TextToSQL_" + Process.GetCurrentProcess().Id);
    public static string At(params string[] p) { return Path.Combine(new[] { Base }.Concat(p).ToArray()); }
}

// Children die with us, even if this process crashes or the pendrive is pulled out.
static class Job
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string n);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr j, int c, IntPtr i, uint l);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr j, IntPtr p);
    static IntPtr h;
    public static void Add(Process p)
    {
        try
        {
            if (h == IntPtr.Zero)
            {
                h = CreateJobObject(IntPtr.Zero, null);
                int size = IntPtr.Size == 8 ? 144 : 112;
                IntPtr mem = Marshal.AllocHGlobal(size);
                for (int i = 0; i < size; i++) Marshal.WriteByte(mem, i, 0);
                Marshal.WriteInt32(mem, 16, 0x2000); // KILL_ON_JOB_CLOSE
                SetInformationJobObject(h, 9, mem, (uint)size);
                Marshal.FreeHGlobal(mem);
            }
            AssignProcessToJobObject(h, p.Handle);
        }
        catch { }
    }
}

class SqlResult { public bool Ok; public string Output = ""; public string Error = ""; }

class Column { public string Name, Type, Ref, RefGuess; public bool Pk, Uni; public List<string> Samples; }
class Table { public string Name; public bool IsView; public List<Column> Cols = new List<Column>(); public long Rows; }

// ------------------------------------------------------------------ sandbox database
class Sandbox
{
    Process server;
    readonly string pipe = "t2s" + Process.GetCurrentProcess().Id;
    string scriptFile;
    public string Db = "";
    public string LoadWarnings = "";
    public List<Table> Tables = new List<Table>();

    public void Start()
    {
        string data = Path.Combine(Paths.Work, "data");
        CopyDir(Paths.At("mariadb", "template"), data);
        string files = Path.Combine(Paths.Work, "files"); Directory.CreateDirectory(files);
        var si = new ProcessStartInfo(Paths.At("mariadb", "bin", "mysqld.exe"),
            "--no-defaults --basedir=\"" + Paths.At("mariadb") + "\" --datadir=\"" + data + "\"" +
            " --skip-networking --enable-named-pipe --socket=" + pipe +
            " --innodb-buffer-pool-size=24M --innodb-log-file-size=8M --key-buffer-size=2M --aria-pagecache-buffer-size=4M" +
            " --performance-schema=OFF --max-connections=10 --table-open-cache=100 --innodb-flush-log-at-trx-commit=0" +
            " --innodb-doublewrite=0 --query-cache-size=0 --secure-file-priv=\"" + files + "\"" +
            " --character-set-server=utf8mb4 --lower-case-table-names=1 --log-error=\"" + Path.Combine(Paths.Work, "db.log") + "\"" +
            " --sql-mode=NO_ZERO_IN_DATE,NO_ZERO_DATE,NO_ENGINE_SUBSTITUTION");
        si.UseShellExecute = false; si.CreateNoWindow = true;
        server = Process.Start(si); Job.Add(server);
        for (int i = 0; i < 240; i++)
        {
            if (server.HasExited) throw new Exception("The built-in database could not start. Log: " + SafeRead(Path.Combine(Paths.Work, "db.log")));
            if (Client("SELECT 1;", "", false, 10).Ok) return;
            Thread.Sleep(250);
        }
        throw new Exception("The built-in database did not start in time.");
    }
    static string SafeRead(string f) { try { return File.ReadAllText(f); } catch { return ""; } }
    static void CopyDir(string a, string b)
    {
        Directory.CreateDirectory(b);
        foreach (var f in Directory.GetFiles(a)) { var t = Path.Combine(b, Path.GetFileName(f)); File.Copy(f, t, true); File.SetAttributes(t, FileAttributes.Normal); }
        foreach (var d in Directory.GetDirectories(a)) CopyDir(d, Path.Combine(b, Path.GetFileName(d)));
    }

    // mode "" = raw tab separated, "-t -vv" = pretty tables
    SqlResult Client(string sql, string extra, bool useDb, int seconds)
    {
        var si = new ProcessStartInfo(Paths.At("mariadb", "bin", "mysql.exe"),
            "--no-defaults --sandbox --protocol=pipe --socket=" + pipe + " -uroot --default-character-set=utf8mb4 " + extra + (useDb && Db != "" ? " \"" + Db + "\"" : ""));
        si.UseShellExecute = false; si.CreateNoWindow = true;
        si.RedirectStandardInput = si.RedirectStandardOutput = si.RedirectStandardError = true;
        si.StandardOutputEncoding = si.StandardErrorEncoding = new UTF8Encoding(false);
        var r = new SqlResult();
        using (var p = Process.Start(si))
        {
            Job.Add(p);
            var so = new StringBuilder(); var se = new StringBuilder();
            p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (so) if (so.Length < 400000) so.Append(e.Data).Append('\n'); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (se) if (se.Length < 20000) se.Append(e.Data).Append('\n'); };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            var bytes = new UTF8Encoding(false).GetBytes(sql + "\n");
            var writer = new Thread(() => { try { p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length); p.StandardInput.BaseStream.Flush(); p.StandardInput.Close(); } catch { } });
            writer.IsBackground = true; writer.Start();
            if (!p.WaitForExit(seconds * 1000)) { try { p.Kill(); } catch { } r.Error = "The query took longer than " + seconds + " seconds and was stopped."; return r; }
            p.WaitForExit();
            r.Output = so.ToString(); r.Error = se.ToString().Trim(); r.Ok = p.ExitCode == 0;
        }
        return r;
    }

    public List<string[]> Rows(string sql)
    {
        var r = Client(sql, "-N --batch", true, 30);
        return r.Output.Split('\n').Where(l => l.Length > 0).Select(l => l.TrimEnd('\r').Split('\t')).ToList();
    }

    static string Adapt(string s)
    {
        s = Regex.Replace(s, @"utf8mb4_0900\w*|utf8mb4_unicode_520_ci", "utf8mb4_general_ci");
        s = Regex.Replace(s, @"DEFINER\s*=\s*\S+?@\S+", "", RegexOptions.IgnoreCase);
        if (Regex.IsMatch(s, @"^\s*PRAGMA\b", RegexOptions.IgnoreCase | RegexOptions.Multiline) || Regex.IsMatch(s, @"\bAUTOINCREMENT\b", RegexOptions.IgnoreCase))
        {   // SQLite dump
            s = Regex.Replace(s, @"^\s*PRAGMA\b[^;]*;", "", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            s = Regex.Replace(s, @"\bAUTOINCREMENT\b", "AUTO_INCREMENT", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"BEGIN\s+TRANSACTION", "START TRANSACTION", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bTEXT(\s+(?:NOT\s+NULL\s+)?(?:PRIMARY\s+KEY|UNIQUE))", "VARCHAR(255)$1", RegexOptions.IgnoreCase);
            s = "SET sql_mode='ANSI_QUOTES';\n" + s;
        }
        if (Regex.IsMatch(s, @"\bVARCHAR2\b", RegexOptions.IgnoreCase)) s = "SET sql_mode='ORACLE';\n" + s;   // Oracle script
        s = Regex.Replace(s, @"^\s*GO\s*$", "", RegexOptions.IgnoreCase | RegexOptions.Multiline);           // SQL Server
        s = Regex.Replace(s, @"\bNVARCHAR\b", "VARCHAR", RegexOptions.IgnoreCase);
        return s;
    }

    public void Load(string file)
    {
        string text;
        using (var sr = new StreamReader(file, new UTF8Encoding(false), true)) text = sr.ReadToEnd();
        scriptFile = Path.Combine(Paths.Work, "load.sql");
        File.WriteAllText(scriptFile, Adapt(text), new UTF8Encoding(false));
        Reset();
        if (Db == "") throw new Exception("No tables were found in this file.\n" + LoadWarnings);
        ReadSchema();
    }

    public void Reset()
    {
        Db = "";
        var drop = new StringBuilder("SET FOREIGN_KEY_CHECKS=0;\n");
        foreach (var d in Rows("SELECT schema_name FROM information_schema.schemata WHERE schema_name NOT IN ('mysql','information_schema','performance_schema','sys')"))
            drop.Append("DROP DATABASE `" + d[0] + "`;\n");
        foreach (var u in Rows("SELECT User, Host, is_role FROM mysql.user WHERE User NOT IN ('root','mariadb.sys','','PUBLIC')"))
            drop.Append(u.Length > 2 && u[2] == "Y" ? "DROP ROLE IF EXISTS `" + u[0] + "`;\n" : "DROP USER IF EXISTS '" + u[0] + "'@'" + u[1] + "';\n");
        drop.Append("CREATE DATABASE userdb CHARACTER SET utf8mb4;\nUSE userdb;\n");
        var r = Client(drop + "SET FOREIGN_KEY_CHECKS=0;\nSTART TRANSACTION;\n" + File.ReadAllText(scriptFile) + "\n;COMMIT;", "--force", false, 300);
        LoadWarnings = r.Error;
        var best = Rows("SELECT table_schema, COUNT(*) c FROM information_schema.tables WHERE table_schema NOT IN ('mysql','information_schema','performance_schema','sys') GROUP BY table_schema ORDER BY c DESC LIMIT 1");
        Db = best.Count > 0 ? best[0][0] : "";
    }

    public void ReadSchema()
    {
        Tables.Clear();
        foreach (var t in Rows("SELECT table_name, table_type, IFNULL(table_rows,0) FROM information_schema.tables WHERE table_schema=DATABASE() ORDER BY create_time, table_name"))
            Tables.Add(new Table { Name = t[0], IsView = t[1] == "VIEW", Rows = long.Parse(t[2]) });
        foreach (var c in Rows("SELECT table_name, column_name, column_type, column_key FROM information_schema.columns WHERE table_schema=DATABASE() ORDER BY table_name, ordinal_position"))
        {
            var t = Tables.FirstOrDefault(x => x.Name.Equals(c[0], StringComparison.OrdinalIgnoreCase));
            if (t != null) t.Cols.Add(new Column { Name = c[1], Type = Regex.Replace(c[2], @"^(int|bigint|smallint|tinyint)\(\d+\)", "$1").ToUpperInvariant().StartsWith("ENUM") ? c[2] : Regex.Replace(c[2], @"^(int|bigint|smallint|tinyint)\(\d+\)", "$1").ToUpperInvariant(), Pk = c.Length > 3 && c[3] == "PRI", Uni = c.Length > 3 && c[3] == "UNI" });
        }
        foreach (var k in Rows("SELECT table_name, column_name, referenced_table_name, referenced_column_name FROM information_schema.key_column_usage WHERE table_schema=DATABASE() AND referenced_table_name IS NOT NULL"))
        {
            var t = Tables.FirstOrDefault(x => x.Name.Equals(k[0], StringComparison.OrdinalIgnoreCase));
            var c = t == null ? null : t.Cols.FirstOrDefault(x => x.Name == k[1]);
            if (c != null) c.Ref = k[2] + "." + k[3];
        }
        // links that the script did not declare: Assigned_Employee_ID -> employee.Employee_ID
        foreach (var t in Tables) foreach (var c in t.Cols.Where(x => x.Ref == null))
            foreach (var o in Tables.Where(x => x != t && !x.IsView))
            {
                var pks = o.Cols.Where(x => x.Pk).ToList();
                if (pks.Count != 1 || pks[0].Name.Length < 4) continue;
                if (c.Name.Equals(pks[0].Name, StringComparison.OrdinalIgnoreCase) && c.Pk && t.Cols.Count(x => x.Pk) == 1) continue;
                if (c.Name.Equals(pks[0].Name, StringComparison.OrdinalIgnoreCase) || c.Name.EndsWith("_" + pks[0].Name, StringComparison.OrdinalIgnoreCase)) c.RefGuess = o.Name + "." + pks[0].Name;
            }
        // a few sample values of text columns that hold categories
        foreach (var t in Tables.Where(x => !x.IsView))
        {
            foreach (var c in t.Cols)
            {
                if (c.Pk || c.Ref != null || !Regex.IsMatch(c.Type, "CHAR|TEXT", RegexOptions.IgnoreCase)) continue;
                if (Regex.IsMatch(c.Name, "email|phone|address|comment|password|descr", RegexOptions.IgnoreCase)) continue;
                var v = Rows("SELECT DISTINCT `" + c.Name + "` FROM (SELECT `" + c.Name + "` FROM `" + t.Name + "` LIMIT 5000) z WHERE `" + c.Name + "` IS NOT NULL LIMIT 9").Select(x => x[0]).ToList();
                bool category = v.Count > 0 && v.Count <= 6;
                if (v.Count > 0 && v.All(x => x.Length <= 24)) c.Samples = category ? v : v.Take(1).ToList();
            }
        }
    }

    public static string One(Table t, bool samples)
    {
        var parts = t.Cols.Select(c => c.Name + " " + c.Type + (c.Pk ? " PK" : "") + (c.Ref != null ? " -> " + c.Ref : c.RefGuess != null ? " -> " + c.RefGuess : "") +
            (samples && c.Samples != null && c.Samples.Count > 0 ? " [" + string.Join(", ", c.Samples.Select(x => "'" + x + "'")) + (c.Samples.Count == 1 ? ", ..." : "") + "]" : ""));
        return t.Name + (t.IsView ? " (view)" : "") + "(" + string.Join(", ", parts) + ")";
    }

    // Whole schema when it is small; otherwise only the tables that match the question.
    public string SchemaText(string question, int budgetChars)
    {
        foreach (bool samples in new[] { true, false })
        {
            var all = string.Join("\n", Tables.Select(t => One(t, samples)));
            if (all.Length <= budgetChars) return all;
        }
        var q = new HashSet<string>(Knowledge.Tokens(question));
        var score = Tables.ToDictionary(t => t, t => Knowledge.Tokens(t.Name).Sum(w => q.Contains(w) ? 5.0 : 0) +
            t.Cols.Sum(c => Regex.IsMatch(question, @"\b" + Regex.Escape(c.Name) + @"\b", RegexOptions.IgnoreCase) ? 3.0 : Knowledge.Tokens(c.Name).Any(q.Contains) ? 0.5 : 0));
        foreach (var t in Tables.Where(x => score[x] >= 3).ToList())   // keep neighbours for joins
            foreach (var c in t.Cols) { var rf = c.Ref ?? c.RefGuess; if (rf != null) { var o = Tables.FirstOrDefault(x => rf.StartsWith(x.Name + ".", StringComparison.OrdinalIgnoreCase)); if (o != null) score[o] += 1; } }
        var pick = new List<Table>(); int len = 0;
        foreach (var t in Tables.OrderByDescending(x => score[x])) { int l = One(t, false).Length; if (len + l > budgetChars && pick.Count > 0) continue; pick.Add(t); len += l; }
        return string.Join("\n", Tables.Where(pick.Contains).Select(t => One(t, false)));
    }

    public SqlResult Run(string sql)
    {
        bool read = IsReadOnly(sql);
        return Client("SET SESSION max_statement_time=12; " + (read ? "SET SESSION sql_select_limit=300; " : "") + "\n" + sql, "-t -vv", true, 25);
    }
    // strict GROUP BY check, as taught in Lab Manual 04
    public SqlResult RunStrict(string sql)
    {
        return Client("SET SESSION max_statement_time=12; SET SESSION sql_select_limit=1; SET SESSION sql_mode=CONCAT(@@sql_mode,',ONLY_FULL_GROUP_BY');\n" + sql, "--batch", true, 25);
    }
    public static bool IsReadOnly(string sql)
    {
        var s = Regex.Replace(sql, @"--[^\n]*|/\*.*?\*/", " ", RegexOptions.Singleline);
        return !Regex.IsMatch(s, @"(^|;)\s*(INSERT|UPDATE|DELETE|REPLACE|ALTER|CREATE|DROP|TRUNCATE|RENAME|GRANT|REVOKE|CALL|SET|DELIMITER|START|BEGIN|COMMIT)\b", RegexOptions.IgnoreCase);
    }
    public void Stop()
    {
        try { if (server != null && !server.HasExited) { Client("SHUTDOWN;", "", false, 5); if (!server.WaitForExit(4000)) server.Kill(); server.WaitForExit(2000); } } catch { }
        for (int i = 0; i < 10; i++) { try { if (Directory.Exists(Paths.Work)) Directory.Delete(Paths.Work, true); return; } catch { Thread.Sleep(200); } }
    }
}

// ------------------------------------------------------------------ lab manual examples
class Example { public string Q, Sql; public Dictionary<string, int> Tok; }
class Knowledge
{
    readonly List<Example> items = new List<Example>();
    readonly Dictionary<string, double> idf = new Dictionary<string, double>();
    static readonly HashSet<string> Stop = new HashSet<string>("a an the of to in on for and or with by from is are be that this who whose which write sql query command statement display show list find get give me all table tables using use named output column columns only have has their its it as each data".Split(' '));

    public static List<string> Tokens(string s)
    {
        s = Regex.Replace(s, @"([a-z])([A-Z])", "$1 $2").ToLowerInvariant();
        var r = new List<string>();
        foreach (Match m in Regex.Matches(s, @"[a-z]+"))
        {
            var w = m.Value; if (Stop.Contains(w)) continue;
            if (w.Length > 5 && w.EndsWith("ies")) w = w.Substring(0, w.Length - 3) + "y";
            else if (w.Length > 5 && w.EndsWith("ing")) w = w.Substring(0, w.Length - 3);
            else if (w.Length > 4 && w.EndsWith("ed")) w = w.Substring(0, w.Length - 2);
            else if (w.Length > 3 && w.EndsWith("s") && !w.EndsWith("ss")) w = w.Substring(0, w.Length - 1);
            if (w.Length > 4 && w.EndsWith("e")) w = w.Substring(0, w.Length - 1);
            r.Add(w);
        }
        return r;
    }
    public Knowledge(string file)
    {
        if (!File.Exists(file)) return;
        Example cur = null; var sql = new StringBuilder(); string k = "";
        Action flush = () =>
        {
            if (cur != null && sql.Length > 0)
            {
                cur.Sql = sql.ToString().Trim(); cur.Tok = Tokens(cur.Q + " " + k).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count()); items.Add(cur);
            }
            cur = null; sql.Clear(); k = "";
        };
        foreach (var line in File.ReadAllLines(file))
        {
            if (line.StartsWith("//")) continue;
            if (line.StartsWith("Q: ")) { flush(); cur = new Example { Q = line.Substring(3).Trim() }; }
            else if (line.StartsWith("K: ")) k = line.Substring(3);
            else if (line.Trim() == "") flush();
            else sql.AppendLine(line);
        }
        flush();
        foreach (var w in items.SelectMany(i => i.Tok.Keys).Distinct()) idf[w] = Math.Log(1.0 + items.Count / (double)items.Count(i => i.Tok.ContainsKey(w)));
    }
    public List<Example> Best(string q, int n, Func<string, bool> fits)
    {
        var qt = Tokens(q).Distinct().ToList();
        return items.Select(i => new { i, s = qt.Sum(w => i.Tok.ContainsKey(w) ? idf[w] : 0) / Math.Sqrt(4 + i.Tok.Count) * (fits(i.Sql) ? 1.2 : 1) })
            .OrderByDescending(x => x.s).Where(x => x.s > 0).Take(n).Select(x => x.i).Reverse().ToList();
    }
}

// ------------------------------------------------------------------ language model
class Llm
{
    Process server; int port;
    readonly JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    public volatile bool Cancel;

    static int Cores()
    {
        try
        {
            int n = 0;
            using (var s = new System.Management.ManagementObjectSearcher("select NumberOfCores from Win32_Processor"))
                foreach (var o in s.Get()) n += Convert.ToInt32(o["NumberOfCores"]);
            if (n > 0) return Math.Max(1, Math.Min(n, 8));
        }
        catch { }
        return Math.Max(1, Environment.ProcessorCount / 2);
    }
    public void Start()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        string model = Directory.GetFiles(Paths.At("model"), "*.gguf").OrderBy(x => x).FirstOrDefault();
        if (model == null) throw new Exception("No .gguf file in the model folder.");
        string t = Environment.GetEnvironmentVariable("T2S_THREADS");
        // --no-mmap: read the model once into RAM, so a slow pendrive is never read again while answering
        var si = new ProcessStartInfo(Paths.At("engine", "llama-server.exe"),
            "-m \"" + model + "\" -c 4608 -np 1 -b 512 -ub 128 -t " + (string.IsNullOrEmpty(t) ? Cores().ToString() : t) +
            " -ngl 0 --no-mmap --cache-ram 0 --no-webui --host 127.0.0.1 --port " + port);
        si.WorkingDirectory = Paths.At("engine");
        si.UseShellExecute = false; si.CreateNoWindow = true; si.RedirectStandardError = si.RedirectStandardOutput = true;
        server = Process.Start(si); Job.Add(server);
        var log = new StringBuilder();
        DataReceivedEventHandler h = (s, e) => { if (e.Data != null) lock (log) { log.AppendLine(e.Data); if (log.Length > 8000) log.Remove(0, 4000); } };
        server.ErrorDataReceived += h; server.OutputDataReceived += h; server.BeginErrorReadLine(); server.BeginOutputReadLine();
        for (int i = 0; i < 2400; i++)
        {
            if (server.HasExited) { string s; lock (log) s = log.ToString(); throw new Exception("The model could not start (not enough free memory?). Close other programs and try again.\n" + s); }
            try { var r = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/health"); r.Proxy = null; r.Timeout = 3000; using (var x = (HttpWebResponse)r.GetResponse()) if (x.StatusCode == HttpStatusCode.OK) return; }
            catch (WebException) { }
            Thread.Sleep(250);
        }
        throw new Exception("The model took too long to load.");
    }
    public string Complete(string prompt, int max, double temp, string[] stop, Action<string> onText)
    {
        var body = new Dictionary<string, object> { { "prompt", prompt }, { "n_predict", max }, { "temperature", temp }, { "top_p", 0.9 }, { "seed", 7 }, { "stop", stop }, { "stream", true }, { "cache_prompt", true } };
        var data = Encoding.UTF8.GetBytes(js.Serialize(body));
        var rq = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/completion");
        rq.Method = "POST"; rq.ContentType = "application/json"; rq.Proxy = null; rq.Timeout = rq.ReadWriteTimeout = 30 * 60 * 1000;
        using (var s = rq.GetRequestStream()) s.Write(data, 0, data.Length);
        var sb = new StringBuilder();
        using (var rs = rq.GetResponse())
        using (var rd = new StreamReader(rs.GetResponseStream(), Encoding.UTF8))
        {
            string line;
            while ((line = rd.ReadLine()) != null)
            {
                if (Cancel) { rq.Abort(); throw new OperationCanceledException(); }
                if (!line.StartsWith("data: ")) continue;
                var o = js.Deserialize<Dictionary<string, object>>(line.Substring(6));
                object c; if (o.TryGetValue("content", out c)) { sb.Append(c); if (onText != null) onText((string)c); }
                object st; if (o.TryGetValue("stop", out st) && st is bool && (bool)st) break;
            }
        }
        return sb.ToString();
    }
    public void Stop() { try { if (server != null && !server.HasExited) server.Kill(); } catch { } }
}

// ------------------------------------------------------------------ question -> checked answer
class Answer { public string Question, Sql = "", Text = "", Output = "", Error = ""; public List<string> Notes = new List<string>(); public bool IsText, Ok; public int Tries; public double Seconds; }

class Assistant
{
    public readonly Sandbox Db = new Sandbox(); public readonly Llm Llm = new Llm();
    public readonly Knowledge Know = new Knowledge(Paths.At("knowledge", "manual_examples.txt"));
    public bool KeepChanges; readonly List<string> kept = new List<string>();
    const int SchemaBudget = 6500;

    const string Rules =
        "You are a MySQL/MariaDB expert for a university database lab. Write SQL that answers the question exactly.\n" +
        "Rules:\n" +
        "- Use only tables and columns that exist in the schema. Join on the links marked ->. Join every table whose columns you use.\n" +
        "- Select exactly the requested columns in the requested order, with the requested aliases.\n" +
        "- Apply every condition, sort and join type in the question, and nothing else. Keep names given in the question.\n" +
        "- Numbers have no commas: 30,000 is 30000. Use IS NULL / IS NOT NULL for null tests. YEAR(col) = 2022 for a year.\n" +
        "- at least = >=, at most = <=, more than = >, less than = <. Filter groups with HAVING, rows with WHERE.\n" +
        "- 'increase by 10%' means col * 1.10, 'reduce by 50' means col - 50. Use LEFT JOIN only when unmatched rows must appear.\n" +
        "- Every selected column that is not aggregated must be in GROUP BY.\n" +
        "- MySQL has no FULL OUTER JOIN: use LEFT JOIN UNION RIGHT JOIN. Use DELIMITER $$ for procedures, functions and triggers.\n" +
        "- Do not add USE, comments or explanations.\n";

    string System(string q) { return System(q, null); }
    string System(string q, string schema) { return "<|im_start|>system\n" + Rules + "\nSchema of database " + Db.Db + " (PK = primary key, -> = foreign key, [..] = values in the data):\n" + (schema ?? Db.SchemaText(q, SchemaBudget)) + "<|im_end|>\n"; }

    public void Open(string file) { kept.Clear(); Db.Load(file); }
    public void Warm() { try { Llm.Complete(System("") + "<|im_start|>user\n", 1, 0, new string[0], null); } catch (WebException) { } }
    public void ResetDb() { kept.Clear(); Db.Reset(); Db.ReadSchema(); }

    static bool IsConcept(string q)
    {
        var s = q.Trim();
        if (Regex.IsMatch(s, @"\b(write|display|show|list|find|retrieve|create|update|delete|insert|add|modify|query to|sql (query|command|statement) to)\b", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(s, @"^(what is (a|an|the difference)|explain|define|why|difference between)", RegexOptions.IgnoreCase)) return false;
        return Regex.IsMatch(s, @"^(explain|define|why|describe the (difference|concept)|difference between|what is (a|an) |what('s| is) the difference|what does|what are (the )?(types|rules|advantages)|compare|when (should|do|would))|\b(1NF|2NF|3NF|BCNF|normaliz)", RegexOptions.IgnoreCase);
    }

    static string Clean(string s)
    {
        s = s.Trim();
        int f = s.IndexOf("```", StringComparison.Ordinal); if (f >= 0) s = s.Substring(0, f);
        s = Regex.Replace(s, @"^\s*USE\s+[`\w]+\s*;", "", RegexOptions.IgnoreCase | RegexOptions.Multiline).Trim();
        s = Regex.Replace(s, @"^\s*(SHUTDOWN|DROP\s+DATABASE)\b[^;]*;", "", RegexOptions.IgnoreCase | RegexOptions.Multiline).Trim();
        if (s.Length > 0 && !s.EndsWith(";") && !Regex.IsMatch(s, @"DELIMITER\s+;\s*$", RegexOptions.IgnoreCase)) s += ";";
        // procedure written without DELIMITER
        if (!Regex.IsMatch(s, @"^\s*DELIMITER\b", RegexOptions.IgnoreCase | RegexOptions.Multiline))
        {
            var m = Regex.Match(s, @"\bCREATE\s+(OR\s+REPLACE\s+)?(PROCEDURE|FUNCTION|TRIGGER)\b", RegexOptions.IgnoreCase);
            int e = s.LastIndexOf("END;", StringComparison.OrdinalIgnoreCase);
            if (m.Success && e > m.Index && !Regex.IsMatch(s.Substring(e), @"IF|CASE|LOOP|WHILE", RegexOptions.IgnoreCase))
                s = s.Substring(0, m.Index) + "DELIMITER $$\n" + s.Substring(m.Index, e - m.Index) + "END $$\nDELIMITER ;" + s.Substring(e + 4);
        }
        else if (Regex.Matches(s, @"^\s*DELIMITER\b", RegexOptions.IgnoreCase | RegexOptions.Multiline).Count % 2 == 1)
        {
            s = s.TrimEnd(); if (s.EndsWith("END;", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 1) + " $$";
            s += "\nDELIMITER ;";
        }
        return s;
    }

    // Things the question asks for literally that can be checked without understanding it.
    List<string> Lint(string q, string sql)
    {
        var bad = new List<string>();
        string noStr = Regex.Replace(sql, @"'[^']*'", "''");
        Func<string, bool> has = w => Regex.IsMatch(sql, @"(?<![A-Za-z0-9_])" + Regex.Replace(Regex.Escape(w), @"(\\ )+", @"\s+") + @"(?![A-Za-z0-9_])", RegexOptions.IgnoreCase);
        foreach (Match m in Regex.Matches(q, @"\b(?:aliased as|alias(?:ed)? it as|renamed? as|with (?:the )?alias|named|called|labell?ed as|display(?:ed)? as)\s+[""'`]?([A-Za-z]\w*_\w+|[A-Z][a-z]+[A-Z]\w*)", RegexOptions.IgnoreCase))
            if (!has(m.Groups[1].Value)) bad.Add("the name " + m.Groups[1].Value + " requested in the question is missing");
        foreach (var c in Db.Tables.SelectMany(t => t.Cols).Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Where(n => n.Contains("_")))
            if (Regex.IsMatch(q, @"(?<!\w)" + Regex.Escape(c) + @"(?!\w)", RegexOptions.IgnoreCase) && !has(c) && !has("*")) bad.Add("the column " + c + " named in the question is not used");
        foreach (var w in new[] { "RIGHT JOIN", "LEFT JOIN", "CROSS JOIN", "UNION ALL", "UNION", "INTERSECT", "EXCEPT", "NOT EXISTS", "EXISTS", "NOT IN", "BETWEEN", "DISTINCT", "HAVING", "CASE", "ANY", "PROCEDURE", "TRIGGER", "FUNCTION", "VIEW", "INDEX" })
        {
            bool asked = Regex.IsMatch(q, @"\b" + w + @"\b") || (w.Length > 5 && Regex.IsMatch(q, @"\b(using|use)( an?| the)? " + w + @"\b", RegexOptions.IgnoreCase))
                || (w == "PROCEDURE" && Regex.IsMatch(q, "create.*procedure", RegexOptions.IgnoreCase)) || (w == "TRIGGER" && Regex.IsMatch(q, @"create.*\btrigger\b", RegexOptions.IgnoreCase)) || (w == "VIEW" && Regex.IsMatch(q, @"create.*\bview\b", RegexOptions.IgnoreCase));
            if (asked && !has(w)) bad.Add("the question asks for " + w);
        }
        var only = Regex.Matches(q, @"\b(?:from|of|in|across all records in) the (\w+) table\b", RegexOptions.IgnoreCase).Cast<Match>().Select(m => m.Groups[1].Value.ToLower()).Distinct().ToList();
        if (only.Count == 1 && Db.Tables.Any(t => t.Name.Equals(only[0], StringComparison.OrdinalIgnoreCase)) && Regex.IsMatch(sql, @"\bJOIN\b", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(q, @"join|tables|related|along with", RegexOptions.IgnoreCase)
            && !Db.Tables.Any(t => !t.Name.Equals(only[0], StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(q, @"\b" + Regex.Escape(t.Name) + @"s?\b", RegexOptions.IgnoreCase)))
            bad.Add("use only the table " + only[0] + ", without any JOIN");
        foreach (var w in new[] { "PROCEDURE", "FUNCTION", "TRIGGER", "VIEW" })
            if (Regex.IsMatch(sql, @"^\s*CREATE\s+(OR\s+REPLACE\s+)?" + w, RegexOptions.IgnoreCase | RegexOptions.Multiline) && !Regex.IsMatch(q, w + "|routine|automatic", RegexOptions.IgnoreCase))
                bad.Add("no " + w + " was asked for, answer with a plain statement");
        var verb = Regex.Match(q, @"^\W*(?:Write (?:an? |the )?(?:SQL |MySQL )?(?:command|query|statement)s? to )?(give|increase|decrease|reduce|raise|update|change|set|delete|remove|insert|add|modify|rename|drop|create)\b", RegexOptions.IgnoreCase);
        if (verb.Success && Regex.IsMatch(sql, @"^\s*(SELECT|WITH)\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(q, @"^\W*(give|create)( me)? (a|an|the) (list|report|summary)", RegexOptions.IgnoreCase))
            bad.Add("the question asks to change the database, so a SELECT is wrong (use UPDATE, DELETE, INSERT, ALTER or CREATE)");
        bool select = Regex.IsMatch(sql, @"^\s*(\(|WITH|SELECT)", RegexOptions.IgnoreCase);
        if (select)
        {
            int lastOrder = sql.LastIndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase);
            if (Regex.IsMatch(q, @"\b(sort|sorted|order(ed)? (by|the)|alphabetical(ly)?)\b", RegexOptions.IgnoreCase) && lastOrder < 0) bad.Add("ORDER BY is missing");
            if (Regex.IsMatch(q, @"\bdescending\b|highest first", RegexOptions.IgnoreCase) && !has("DESC")) bad.Add("the sort must be DESC");
            if (!Regex.IsMatch(q, @"\bdescending\b|\bDESC\b|highest|largest|most|top|latest|newest|recent|last|max|desc", RegexOptions.IgnoreCase) && Regex.IsMatch(q, "ascending|alphabetical", RegexOptions.IgnoreCase) && has("DESC")) bad.Add("the sort must be ascending");
        }
        foreach (Match m in Regex.Matches(q, @"\b(at least|at most|more than|greater than|less than|fewer than|over|exceed(?:s|ing)?)\s+(?:BDT|\$|Tk\.?)?\s*([\d][\d,]*(?:\.\d+)?)", RegexOptions.IgnoreCase))
        {
            string n = Regex.Escape(m.Groups[2].Value.Replace(",", "")); string w = m.Groups[1].Value.ToLower();
            string want = w == "at least" ? ">=" : w == "at most" ? "<=" : w.StartsWith("less") || w.StartsWith("few") ? "<" : ">";
            var f = Regex.Match(noStr, @"(>=|<=|<>|>|<|=)\s*" + n + @"(?:\.0+)?(?![\d.])");
            if (f.Success && f.Groups[1].Value != want && f.Groups[1].Value != "=") bad.Add("'" + m.Value + "' means " + want + " " + m.Groups[2].Value.Replace(",", ""));
        }
        foreach (Match m in Regex.Matches(q, @"(?<![A-Za-z])['""‘“]([^'""‘’“”]{2,40})['""’”](?![A-Za-z])"))
            if (sql.IndexOf(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase) < 0) bad.Add("the value '" + m.Groups[1].Value + "' is missing");
        return bad.Distinct().Take(4).ToList();
    }

    void Restore()
    {
        Db.Reset();
        foreach (var k in kept) Db.Run(k);
    }

    // The manuals also use tables like "employee". If the user's table of that name has other columns,
    // rename it inside the example so the model does not copy columns that do not exist.
    bool Fits(string sql)
    {
        var known = new HashSet<string>(Db.Tables.SelectMany(t => t.Cols.Select(c => c.Name).Concat(new[] { t.Name })), StringComparer.OrdinalIgnoreCase);
        var alias = new HashSet<string>(Regex.Matches(sql, @"\bAS\s+(\w+)", RegexOptions.IgnoreCase).Cast<Match>().Select(m => m.Groups[1].Value), StringComparer.OrdinalIgnoreCase);
        return Regex.Matches(Regex.Replace(sql, "'[^']*'", ""), @"\b([A-Za-z]+_[A-Za-z]+|Fname|Lname|Dname|Dno|Essn|Salary|Pno|Ssn)\b").Cast<Match>().All(m => known.Contains(m.Value) || alias.Contains(m.Value));
    }
    string Disguise(string sql)
    {
        if (Fits(sql)) return sql;
        foreach (var t in Db.Tables) sql = Regex.Replace(sql, @"(?<![\w.'])" + Regex.Escape(t.Name) + @"(?![\w'])", "sample_" + t.Name.ToLower(), RegexOptions.IgnoreCase);
        return sql;
    }

    static string Key(string output)   // result rows only, without headers, timing or order
    {
        var l = output.Replace("\r", "").Split('\n').Where(x => x.StartsWith("|")).Select(x => Regex.Replace(x, @"\s+", " ")).ToList();
        return string.Join("\n", l.Skip(1).OrderBy(x => x, StringComparer.Ordinal));
    }

    static string ErrText(string e)
    {
        e = Regex.Replace(e, @"^ERROR (\d+) \((\w+)\) at line \d+( in file: '[^']*')?:", "ERROR $1:", RegexOptions.Multiline).Trim();
        return e.Length > 400 ? e.Substring(0, 400) : e;
    }

    public Answer Ask(string q, Action<string> status, Action<string> stream)
    {
        var sw = Stopwatch.StartNew(); var a = new Answer { Question = q };
        Llm.Cancel = false;
        string sys = System(q);
        if (IsConcept(q))
        {
            a.IsText = true;
            status("Writing the explanation...");
            string p = "<|im_start|>system\nYou are a teaching assistant for a university MySQL database lab. Answer clearly and briefly (under 150 words), with a short MySQL example when useful.<|im_end|>\n<|im_start|>user\n" + q + "<|im_end|>\n<|im_start|>assistant\n";
            a.Text = Llm.Complete(p, 400, 0, new[] { "<|im_end|>" }, stream).Trim(); a.Ok = true; a.Seconds = sw.Elapsed.TotalSeconds; return a;
        }
        int nEx; if (!int.TryParse(Environment.GetEnvironmentVariable("T2S_EX"), out nEx)) nEx = 3;
        var ex = Know.Best(q, nEx, Fits);
        var sb = new StringBuilder();
        string plain = "Answer this question using only the schema above:\n" + q;
        if (ex.Count > 0)
        {
            sb.Append("Examples from the lab manual. They use a DIFFERENT database, so copy only the technique:\n");
            foreach (var e in ex) sb.Append("Q: " + e.Q + "\n" + Disguise(e.Sql) + "\n\n");
        }
        sb.Append("Now answer this question using only the schema above:\n" + q);
        string prompt = sys + "<|im_start|>user\n" + sb + "<|im_end|>\n<|im_start|>assistant\n```sql\n";

        int bestScore = int.MaxValue; string hints = ""; bool leaked = false;
        for (int i = 0; i < 3; i++)
        {
            a.Tries = i + 1;
            status(i == 0 ? "Writing SQL..." : "Fixing SQL (try " + (i + 1) + ")...");
            if (i > 0 && stream != null) stream("\n\n-- correcting:\n");
            string sql = Clean(Llm.Complete(prompt, 600, i == 2 ? 0.5 : 0, new[] { "```", "<|im_end|>" }, stream));
            if (sql == "" ) { prompt += " "; continue; }
            status("Checking SQL on a copy of the database...");
            bool ro = Sandbox.IsReadOnly(sql);
            var r = Db.Run(sql);
            string err = r.Ok ? "" : ErrText(r.Error);
            if (r.Ok && ro && Regex.IsMatch(sql, "GROUP\\s+BY", RegexOptions.IgnoreCase)) { var s2 = Db.RunStrict(sql); if (!s2.Ok && s2.Error.Contains("GROUP BY") && !s2.Error.Contains("timeout")) err = ""; if (!s2.Ok && Regex.IsMatch(s2.Error, "1055")) a.Notes.Add("strict"); }
            var lint = Lint(q, sql);
            int score = (r.Ok ? 0 : 100) + lint.Count;
            if (!ro) { if (r.Ok && lint.Count == 0 && KeepChanges) kept.Add(sql); else Restore(); }
            if (score < bestScore)
            {
                bestScore = score; a.Sql = sql; a.Output = r.Output; a.Error = err; a.Ok = r.Ok;
                a.Notes = lint.ToList();
            }
            if (score == 0)
            {   // second opinion: same question without examples; if the two results differ, a third answer decides
                if (ro && a.Tries < 3 && Environment.GetEnvironmentVariable("T2S_VOTE") != "0")
                {
                    status("Double-checking the answer...");
                    var votes = new List<string[]> { new[] { sql, Key(r.Output) } };
                    string basePrompt = sys + "<|im_start|>user\n" + plain + "<|im_end|>\n<|im_start|>assistant\n```sql\n";
                    for (int v = 0; v < 2; v++)
                    {
                        string alt = Clean(Llm.Complete(v == 0 ? basePrompt : prompt, 600, v == 0 ? 0 : 0.6, new[] { "```", "<|im_end|>" }, null));
                        if (alt == "" || !Sandbox.IsReadOnly(alt)) continue;
                        if (Regex.Replace(alt, @"\s+", " ").Equals(Regex.Replace(sql, @"\s+", " "), StringComparison.OrdinalIgnoreCase)) { votes.Add(new[] { sql, votes[0][1] }); break; }
                        var ar = Db.Run(alt); if (!ar.Ok || Lint(q, alt).Count > 0) continue;
                        votes.Add(new[] { alt, Key(ar.Output) });
                        if (votes.Last()[1] == votes[0][1]) break;
                        if (votes.Count == 3 && votes[1][1] == votes[2][1]) { a.Sql = votes[1][0]; a.Output = Db.Run(a.Sql).Output; }
                    }
                    if (votes.Count > 1 && votes.GroupBy(x => x[1]).All(x => x.Count() == 1))
                        a.Notes.Add("two drafts gave different results; the other draft was:\n" + votes[1][0]);
                }
                break;
            }
            string fb = !r.Ok ? "MariaDB rejected it: " + err + "\n" : "";
            if (lint.Count > 0) fb += "Check again: " + string.Join("; ", lint) + ".\n";
            var uc = Regex.Match(err, @"Unknown column '(?:[^'.]*\.)*([^'.]+)'");
            if (uc.Success)
            {
                var own = Db.Tables.Where(t => t.Cols.Any(c => c.Name.Equals(uc.Groups[1].Value, StringComparison.OrdinalIgnoreCase))).Select(t => t.Name).ToList();
                fb += own.Count > 0 ? "Column " + uc.Groups[1].Value + " exists only in: " + string.Join(", ", own.Take(5)) + ".\n" : "No table has a column " + uc.Groups[1].Value + ".\n";
            }
            if (err.Contains("1093")) fb += "Wrap the subquery in a derived table: (SELECT x FROM (SELECT ...) AS t).\n";
            hints += fb;
            bool names = Regex.IsMatch(err, "Unknown column|doesn't exist|Unknown table");
            if (names) foreach (var t in Db.Tables.Where(t => Regex.IsMatch(sql, @"\b" + Regex.Escape(t.Name) + @"\b", RegexOptions.IgnoreCase)).Take(4))
                hints += "Table " + t.Name + " has only: " + string.Join(", ", t.Cols.Select(c => c.Name)) + ".\n";
            leaked |= names;
            var alone = lint.Select(x => Regex.Match(x, @"^use only the table (\w+)")).FirstOrDefault(x => x.Success);
            var lone = alone == null ? null : Db.Tables.First(t => t.Name.Equals(alone.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
            if (lone != null) prompt = System(q, Sandbox.One(lone, true)) + "<|im_start|>user\n" + plain + "<|im_end|>\n<|im_start|>assistant\n```sql\n";   // show only that table
            else if (leaked) prompt = sys + "<|im_start|>user\n" + plain + "\nImportant:\n" + hints + "<|im_end|>\n<|im_start|>assistant\n```sql\n";   // without the examples
            else if (i == 1) prompt = sys + "<|im_start|>user\n" + sb + "\nImportant:\n" + hints + "<|im_end|>\n<|im_start|>assistant\n```sql\n";   // fresh start, not anchored on the wrong SQL
            else prompt += sql + "\n```<|im_end|>\n<|im_start|>user\n" + fb + "Write the corrected full SQL.<|im_end|>\n<|im_start|>assistant\n```sql\n";
        }
        if (a.Ok && KeepChanges && !Sandbox.IsReadOnly(a.Sql)) { if (!kept.Contains(a.Sql)) { kept.Add(a.Sql); Restore(); } Db.ReadSchema(); }
        a.Seconds = sw.Elapsed.TotalSeconds;
        return a;
    }

    // "1. ... 2. ..." pasted from a PDF becomes separate questions; wrapped lines are rejoined.
    public static List<string> Split(string text)
    {
        var res = new List<string>(); StringBuilder cur = null;
        var lines = text.Replace("\r", "").Split('\n');
        bool numbered = lines.Count(l => Regex.IsMatch(l, @"^\s*(Q(uestion)?\s*)?\d{1,2}\s*[\.\):]\s+\S", RegexOptions.IgnoreCase)) > 0;
        foreach (var l in lines)
        {
            var m = Regex.Match(l, @"^\s*(?:Q(?:uestion)?\s*)?\d{1,2}\s*[\.\):]\s+(.*)$", RegexOptions.IgnoreCase);
            if (numbered && m.Success) { if (cur != null) res.Add(cur.ToString()); cur = new StringBuilder(m.Groups[1].Value.Trim()); }
            else if (l.Trim() == "" && !numbered) { if (cur != null) { res.Add(cur.ToString()); cur = null; } }
            else { if (cur == null) cur = new StringBuilder(); if (cur.Length > 0) cur.Append(' '); cur.Append(l.Trim()); }
        }
        if (cur != null) res.Add(cur.ToString());
        return res.Select(x => x.Trim()).Where(x => x.Length > 2).ToList();
    }
    // ---- scenario text -> new database, no file needed
    const string DesignRules =
        "You are a relational database designer for a university MySQL lab. Turn the scenario into CREATE TABLE statements for MySQL.\n" +
        "Rules:\n" +
        "- One table for each entity in the scenario, and no extra tables. Use the table and column names given in the scenario exactly, with singular table names.\n" +
        "- Include every attribute the scenario mentions and do not invent others.\n" +
        "- Every table has a PRIMARY KEY. IDs are INT. Names are VARCHAR(100), phone numbers, ISBN, card and code numbers VARCHAR(20), dates DATE, money DECIMAL(10,2).\n" +
        "- One-to-one ('at most one'): the foreign key column is also UNIQUE.\n" +
        "- One-to-many (A has, offers, keeps or places several B): table B holds the foreign key to A. Check every sentence for such a link. Many-to-many: a junction table holds both foreign keys.\n" +
        "- A junction table that records events over time has a composite PRIMARY KEY of both foreign keys plus the date and the type.\n" +
        "- Declare every link as FOREIGN KEY (col) REFERENCES Parent(col). Foreign key columns and names are NOT NULL.\n" +
        "- Only list fixed values with ENUM when the scenario gives all of them; 'e.g.' values use VARCHAR.\n" +
        "- Create parent tables first. No INSERT, no USE, no comments, no explanations.\n";
    const string DesignExample =
        "Scenario: A school tracks Teachers (Teacher_ID, Name) and Courses (Course_ID, Title, and the Teacher_ID who teaches it). Students have Student_ID and Full_Name. " +
        "A student can take many courses and a course has many students; an Attendance table records the student, the course, the exact Class_Date and the Status.";
    const string DesignAnswer =
        "CREATE TABLE Teacher (\n  Teacher_ID INT PRIMARY KEY,\n  Name VARCHAR(100) NOT NULL\n);\n\n" +
        "CREATE TABLE Course (\n  Course_ID INT PRIMARY KEY,\n  Title VARCHAR(100) NOT NULL,\n  Teacher_ID INT NOT NULL,\n  FOREIGN KEY (Teacher_ID) REFERENCES Teacher(Teacher_ID)\n);\n\n" +
        "CREATE TABLE Student (\n  Student_ID INT PRIMARY KEY,\n  Full_Name VARCHAR(100) NOT NULL\n);\n\n" +
        "CREATE TABLE Attendance (\n  Student_ID INT NOT NULL,\n  Course_ID INT NOT NULL,\n  Class_Date DATE NOT NULL,\n  Status VARCHAR(20) NOT NULL,\n  PRIMARY KEY (Student_ID, Course_ID, Class_Date, Status),\n" +
        "  FOREIGN KEY (Student_ID) REFERENCES Student(Student_ID),\n  FOREIGN KEY (Course_ID) REFERENCES Course(Course_ID)\n);";

    public static bool IsDesign(string q)
    {
        return q.Length > 120 && Regex.IsMatch(q, @"\bdatabase design\b|\bscenario\s*:|\bdesign (a|an|the) (\w+ )?(database|schema)\b|\bneeds? (a|to build a|to design a) (relational )?database\b", RegexOptions.IgnoreCase);
    }

    // Keeps only table definitions and puts every parent before its children.
    static string DesignScript(string raw)
    {
        int fence = raw.IndexOf("```", StringComparison.Ordinal); if (fence >= 0) raw = raw.Substring(0, fence);
        raw = Regex.Replace(raw, @"--[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);
        var st = raw.Split(';').Select(x => x.Trim()).Where(x => Regex.IsMatch(x, @"^(CREATE\s+TABLE|ALTER\s+TABLE)\b", RegexOptions.IgnoreCase)).ToList();
        var make = st.Where(x => Regex.IsMatch(x, @"^CREATE", RegexOptions.IgnoreCase)).ToList();
        Func<string, string> name = x => Regex.Match(x, @"^CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?[`""]?(\w+)", RegexOptions.IgnoreCase).Groups[1].Value.ToLowerInvariant();
        var all = new HashSet<string>(make.Select(name)); var done = new HashSet<string>(); var res = new List<string>();
        while (make.Count > 0)
        {
            var next = make.FirstOrDefault(x => Regex.Matches(x, @"REFERENCES\s+[`""]?(\w+)", RegexOptions.IgnoreCase).Cast<Match>().Select(m => m.Groups[1].Value.ToLowerInvariant())
                .All(p => p == name(x) || done.Contains(p) || !all.Contains(p))) ?? make[0];
            make.Remove(next); done.Add(name(next)); res.Add(next);
        }
        res.AddRange(st.Where(x => Regex.IsMatch(x, @"^ALTER", RegexOptions.IgnoreCase)));
        var sql = string.Join("\n\n", res.Select(x => x + ";"));
        // table names that are reserved words, such as Order, need backticks
        return Regex.Replace(sql, @"(\b(?:CREATE\s+TABLE(?:\s+IF\s+NOT\s+EXISTS)?|ALTER\s+TABLE|REFERENCES)\s+)(Order|Group|Table|Key|Index|Condition|Rank|Range|Rows|Select|Check|Column|Grant|Schema|Use|Usage|Match|Release|Return|Leave|Load|Call|Case|Change|Option|References|Trigger)(?=\s*\(|\s)", "$1`$2`", RegexOptions.IgnoreCase);
    }

    List<string> DesignLint(string q)
    {
        var bad = new List<string>();
        var names = new HashSet<string>(Db.Tables.Select(t => t.Name).Concat(Db.Tables.SelectMany(t => t.Cols).Select(c => c.Name)), StringComparer.OrdinalIgnoreCase);
        foreach (var w in Regex.Matches(q, @"\b[A-Za-z]+(?:_[A-Za-z]+)+\b").Cast<Match>().Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!names.Contains(w)) bad.Add(w + " from the scenario is missing");
        if (Regex.IsMatch(q, @"\bat most one\b", RegexOptions.IgnoreCase) && !Db.Tables.Any(t => t.Cols.Any(c => c.Uni && c.Ref != null) || (t.Cols.Count(c => c.Pk) == 1 && t.Cols.Any(c => c.Pk && c.Ref != null))))
            bad.Add("the 'at most one' link needs UNIQUE on its foreign key column");
        foreach (var t in Db.Tables)
        {
            foreach (var c in t.Cols.Where(c => Regex.IsMatch(c.Name, "isbn|phone|mobile", RegexOptions.IgnoreCase) && Regex.IsMatch(c.Type, "INT", RegexOptions.IgnoreCase))) bad.Add(c.Name + " in " + t.Name + " must be VARCHAR(20), not INT");
            var pk = t.Cols.Where(c => c.Pk).ToList();
            if (pk.Count == 0) bad.Add("table " + t.Name + " has no PRIMARY KEY");
            foreach (var c in t.Cols.Where(c => c.Ref == null && c.RefGuess != null)) bad.Add("in " + t.Name + " declare FOREIGN KEY (" + c.Name + ") REFERENCES " + c.RefGuess.Replace(".", "(") + ")");
            if (pk.Count >= 2 && pk.All(c => c.Ref != null))
            {
                var d = t.Cols.FirstOrDefault(c => !c.Pk && c.Type.StartsWith("DATE"));
                if (d != null) bad.Add("the PRIMARY KEY of " + t.Name + " must also include " + d.Name + ", or the same pair could be stored only once");
            }
        }
        if (Db.Tables.Count > 1) foreach (var t in Db.Tables)
            if (!t.Cols.Any(c => c.Ref != null) && !Db.Tables.Any(o => o.Cols.Any(c => c.Ref != null && c.Ref.StartsWith(t.Name + ".", StringComparison.OrdinalIgnoreCase)))) bad.Add("table " + t.Name + " is not linked: the table that belongs to it (what it has, offers or places) needs its primary key as a FOREIGN KEY");
        return bad.Distinct().Take(8).ToList();
    }

    string Relations(string sql)
    {
        var shown = Regex.Matches(sql, @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?[`""]?(\w+)", RegexOptions.IgnoreCase).Cast<Match>().Select(m => m.Groups[1].Value).GroupBy(x => x.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        Func<string, string> N = x => shown.ContainsKey(x.ToLowerInvariant()) ? shown[x.ToLowerInvariant()] : x;   // keep the capitals of the script
        var sb = new StringBuilder();
        foreach (var t in Db.Tables)
        {
            var fks = t.Cols.Where(c => c.Ref != null).ToList(); bool single = t.Cols.Count(c => c.Pk) == 1;
            foreach (var c in fks) sb.Append(N(c.Ref.Split('.')[0]) + ((c.Pk && single) || c.Uni ? "  1 : 1  " : "  1 : many  ") + N(t.Name) + "   (" + N(t.Name) + "." + c.Name + " -> " + N(c.Ref.Split('.')[0]) + "." + c.Ref.Split('.')[1] + ")\n");
            var parents = fks.Where(c => c.Pk).Select(c => c.Ref.Split('.')[0]).Distinct().ToList();
            if (parents.Count == 2) sb.Append(N(parents[0]) + "  many : many  " + N(parents[1]) + "   (through " + N(t.Name) + ")\n");
        }
        return sb.Length == 0 ? "No relationships.\n" : sb.ToString();
    }

    public static string DesignFile { get { return Path.Combine(Paths.Work, "design.sql"); } }

    public Answer Design(string scenario, Action<string> status, Action<string> stream)
    {
        var sw = Stopwatch.StartNew(); var a = new Answer { Question = scenario };
        Llm.Cancel = false;
        if (scenario.Length > 6000) throw new Exception("The scenario is too long. Paste only the design part (up to about 900 words).");
        string head = "<|im_start|>system\n" + DesignRules + "<|im_end|>\n<|im_start|>user\n" + DesignExample + "<|im_end|>\n<|im_start|>assistant\n```sql\n" + DesignAnswer + "\n```<|im_end|>\n<|im_start|>user\n" + scenario;
        string tail = "<|im_end|>\n<|im_start|>assistant\n```sql\n"; string prompt = head + tail;
        string temp = Path.Combine(Paths.Work, "candidate.sql"); int best = int.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            a.Tries = i + 1;
            status(i == 0 ? "Designing the tables..." : "Improving the design (try " + (i + 1) + ")...");
            if (i > 0 && stream != null) stream("\n\n-- correcting:\n");
            string sql = DesignScript(Llm.Complete(prompt, 1200, i == 2 ? 0.4 : 0, new[] { "```", "<|im_end|>" }, stream));
            string err = ""; List<string> lint = new List<string>(); bool made = false;
            if (sql == "") err = "no CREATE TABLE statement was written";
            else
            {
                status("Creating the tables in the built-in database...");
                File.WriteAllText(temp, "SET FOREIGN_KEY_CHECKS=1;\n" + sql, new UTF8Encoding(false));   // strict: parents must exist first
                try { Open(temp); made = true; err = ErrText(Regex.Match(Db.LoadWarnings, @"ERROR[\s\S]*").Value); lint = DesignLint(scenario); }
                catch (OperationCanceledException) { throw; }
                catch (Exception x) { err = ErrText(x.Message); }
            }
            int score = (made ? 0 : 1000) + (err != "" ? 100 : 0) + lint.Count;
            if (sql != "" && score < best) { best = score; a.Sql = sql; a.Error = err; a.Ok = made && err == ""; a.Notes = lint; }
            if (score == 0) break;
            prompt = head + "\n\nA first attempt was:\n" + sql + "\n\nProblems to fix:\n" + (err != "" ? "- MariaDB said: " + err + "\n" : "") + string.Concat(lint.Select(l => "- " + l + "\n")) + "Write the full corrected design." + tail;
        }
        if (best < 1000)
        {
            File.WriteAllText(DesignFile, "SET FOREIGN_KEY_CHECKS=1;\n" + a.Sql, new UTF8Encoding(false));
            Open(DesignFile); a.Output = Relations(a.Sql);
        }
        a.Seconds = sw.Elapsed.TotalSeconds;
        return a;
    }

    public void Stop() { Llm.Stop(); Db.Stop(); }
}

// ------------------------------------------------------------------ window
class MainForm : Form
{
    readonly Assistant ai = new Assistant();
    readonly RichTextBox outBox = new RichTextBox(); readonly TextBox ask = new TextBox(); readonly TreeView tree = new TreeView();
    readonly Button open = new Button(), send = new Button(), reset = new Button(), save = new Button(), copy = new Button(), design = new Button();
    readonly CheckBox keep = new CheckBox(); readonly Label status = new Label();
    bool ready, busy; string dbFile; string lastSql = "";
    readonly StringBuilder sqlLog = new StringBuilder();
    static readonly Color Ink = Color.FromArgb(28, 33, 40), Accent = Color.FromArgb(0, 98, 155);

    public MainForm(string file)
    {
        Text = "Text to SQL (offline)"; Width = 1080; Height = 700; MinimumSize = new Size(720, 480);
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10f); BackColor = Color.White; ForeColor = Ink;
        AllowDrop = true;
        DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
        DragDrop += (s, e) => { var f = (string[])e.Data.GetData(DataFormats.FileDrop); if (f.Length > 0) LoadDb(f[0]); };

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(6, 6, 6, 4), BackColor = Color.FromArgb(243, 245, 248), WrapContents = true, AutoSize = true };
        Btn(open, "Open database (.sql)", 180, true); Btn(design, "Design from scenario", 170, true); Btn(reset, "Reset database", 130, false); Btn(copy, "Copy last SQL", 120, false); Btn(save, "Save all answers", 135, false);
        keep.Text = "Keep changes between questions"; keep.AutoSize = true; keep.Margin = new Padding(12, 8, 0, 0);
        keep.CheckedChanged += (s, e) => ai.KeepChanges = keep.Checked;
        top.Controls.AddRange(new Control[] { open, design, reset, copy, save, keep });

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 136, Padding = new Padding(8) };
        ask.Multiline = true; ask.Dock = DockStyle.Fill; ask.ScrollBars = ScrollBars.Vertical; ask.AcceptsReturn = true; ask.MaxLength = 100000;
        Btn(send, "Ask", 110, true); send.Dock = DockStyle.Right;
        status.Dock = DockStyle.Bottom; status.Height = 24; status.TextAlign = ContentAlignment.MiddleLeft; status.ForeColor = Color.FromArgb(80, 88, 99);
        bottom.Controls.Add(ask); bottom.Controls.Add(send); bottom.Controls.Add(status);

        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        tree.Dock = DockStyle.Fill; tree.BorderStyle = BorderStyle.None; tree.Font = new Font("Segoe UI", 9f); split.Panel1.Controls.Add(tree);
        outBox.Dock = DockStyle.Fill; outBox.ReadOnly = true; outBox.BorderStyle = BorderStyle.None; outBox.BackColor = Color.White; outBox.WordWrap = false; outBox.ScrollBars = RichTextBoxScrollBars.Both; outBox.DetectUrls = false; outBox.HideSelection = false;
        split.Panel2.Padding = new Padding(8, 4, 0, 0); split.Panel2.Controls.Add(outBox);
        Controls.Add(split); Controls.Add(bottom); Controls.Add(top);
        Load += (s, e) => { split.SplitterDistance = 230; };

        open.Click += (s, e) => { using (var d = new OpenFileDialog { Filter = "SQL files|*.sql;*.txt|All files|*.*" }) if (d.ShowDialog(this) == DialogResult.OK) LoadDb(d.FileName); };
        send.Click += (s, e) => { if (busy) { ai.Llm.Cancel = true; SetStatus("Stopping..."); } else Go(); };
        design.Click += (s, e) => GoDesign();
        reset.Click += (s, e) => { if (dbFile == null) { SetStatus("There is no database to reset yet."); return; } Bg("Resetting the database...", () => { ai.ResetDb(); UI(FillTree); }); };
        copy.Click += (s, e) => { if (lastSql != "") { try { Clipboard.SetText(lastSql); SetStatus("SQL copied."); } catch { } } };
        save.Click += (s, e) => { using (var d = new SaveFileDialog { Filter = "SQL file|*.sql", FileName = "answers.sql" }) if (d.ShowDialog(this) == DialogResult.OK) { File.WriteAllText(d.FileName, sqlLog.ToString(), new UTF8Encoding(true)); SetStatus("Saved."); } };
        ask.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && e.Control) { e.SuppressKeyPress = true; if (!busy) Go(); } };
        FormClosing += (s, e) => { ai.Llm.Cancel = true; Hide(); ai.Stop(); };

        Write("Starting. Open a database file, then type or paste questions (one, or a numbered list) and press Ask or Ctrl+Enter.\nNo database file? Paste a scenario and press Design from scenario.\n", Color.Gray, false);
        Shown += (s, e) => Bg("Starting the database and the model (slow pendrives need a minute)...", () =>
        {
            Exception err = null;
            var t = new Thread(() => { try { ai.Llm.Start(); } catch (Exception x) { err = x; } }); t.Start();
            ai.Db.Start(); t.Join(); if (err != null) throw err;
            ready = true;
            if (file != null) { Thread.Sleep(50); BeginInvoke((Action)(() => LoadDb(file))); }
        });
    }
    void Btn(Button b, string t, int w, bool primary)
    {
        b.Text = t; b.Width = w; b.Height = 32; b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = primary ? Accent : Color.Silver; b.BackColor = primary ? Accent : Color.White; b.ForeColor = primary ? Color.White : Ink;
    }
    void UI(Action a) { try { if (InvokeRequired) BeginInvoke(a); else a(); } catch (InvalidOperationException) { } }
    void SetStatus(string s) { UI(() => status.Text = s); }
    void Write(string s, Color c, bool mono)
    {
        UI(() =>
        {
            outBox.SelectionStart = outBox.TextLength; outBox.SelectionColor = c;
            outBox.SelectionFont = mono ? new Font("Consolas", 10f) : new Font("Segoe UI", 10f);
            outBox.AppendText(s); outBox.ScrollToCaret();
        });
    }
    void Bg(string what, Action work)
    {
        if (busy) return; busy = true; SetStatus(what); Toggle();
        var t = new Thread(() =>
        {
            string end = "Ready.";
            try { work(); }
            catch (OperationCanceledException) { end = "Stopped."; }
            catch (Exception x) { end = "Problem: " + x.Message.Split('\n')[0]; Write("\n" + x.Message + "\n", Color.Firebrick, false); }
            busy = false; UI(() => { Toggle(); if (ready && dbFile == null && end == "Ready.") end = "Ready. Open a .sql database file, or paste a scenario and press Design from scenario."; status.Text = end; });
        });
        t.IsBackground = true; t.Start();
    }
    void Toggle() { open.Enabled = design.Enabled = reset.Enabled = keep.Enabled = !busy; send.Text = busy && ready ? "Stop" : "Ask"; send.Enabled = ready; }

    void LoadDb(string f)
    {
        if (!ready) { SetStatus("Still starting, please wait."); return; }
        Bg("Loading the database...", () =>
        {
            ai.Open(f); dbFile = f;
            UI(FillTree);
            Write("\nLoaded " + Path.GetFileName(f) + ": database " + ai.Db.Db + ", " + ai.Db.Tables.Count + " tables. Your file itself is never changed.\n", Accent, false);
            if (ai.Db.LoadWarnings != "") Write("Some statements in the file failed:\n" + string.Join("\n", ai.Db.LoadWarnings.Split('\n').Take(5)) + "\n", Color.DarkOrange, false);
            SetStatus("Reading the schema into the model (once per database)...");
            ai.Warm();
        });
    }
    void FillTree()
    {
        tree.BeginUpdate(); tree.Nodes.Clear();
        foreach (var t in ai.Db.Tables) { var n = tree.Nodes.Add(t.Name + (t.IsView ? " (view)" : "")); foreach (var c in t.Cols) n.Nodes.Add(c.Name + "  " + c.Type.ToLower() + (c.Pk ? "  PK" : "") + (c.Ref != null ? "  > " + c.Ref : "")); }
        tree.EndUpdate();
    }
    void GoDesign()
    {
        if (!ready) { SetStatus("Still starting, please wait."); return; }
        string text = ask.Text.Replace("\r", "").Trim();
        if (text.Length < 40) { SetStatus("Paste the scenario into the box below first."); ask.Focus(); return; }
        if (busy) return;
        ask.Clear(); string before = dbFile;
        Bg("Designing the database...", () =>
        {
            Write("\n" + text + "\n", Accent, false);
            int start = 0; var gate = new ManualResetEvent(false);
            UI(() => { start = outBox.TextLength; gate.Set(); }); gate.WaitOne(2000);
            Answer a = null; string keepOld = null;
            if (before != null) { keepOld = Path.Combine(Paths.Work, "before.sql"); File.Copy(before, keepOld, true); }
            try { a = ai.Design(text, SetStatus, s => Write(s, Color.Gray, true)); }
            finally
            {   // stopped or failed: bring the previous database back
                if (a == null || a.Output == "")
                {
                    if (keepOld != null) { if (before == Assistant.DesignFile) File.Copy(keepOld, before, true); ai.Open(before); }
                    else { ai.Db.Tables.Clear(); ai.Db.Db = ""; }
                    UI(FillTree);
                }
            }
            UI(() => { outBox.Select(start, outBox.TextLength - start); outBox.ReadOnly = false; outBox.SelectedText = ""; outBox.ReadOnly = true; });
            Write(a.Sql + "\n", Ink, true);
            if (a.Sql != "") { UI(() => lastSql = a.Sql); sqlLog.AppendLine("/* " + text.Replace("*/", "* /").Replace("\n", "\r\n") + " */\r\n" + a.Sql.Replace("\n", "\r\n") + "\r\n"); }
            if (a.Ok) Write("\nAll tables were created without errors (" + a.Seconds.ToString("0") + " s).\n", Color.SeaGreen, false);
            else Write("\nProblem in this design: " + a.Error + "\n", Color.Firebrick, false);
            foreach (var n in a.Notes) Write("Check this design: " + n + ".\n", Color.DarkOrange, false);
            if (a.Output == "") return;
            dbFile = Assistant.DesignFile; UI(FillTree);
            Write("\nRelationships\n" + a.Output, Color.FromArgb(70, 78, 88), true);
            Write("This is now the open database" + (before != null ? " (it replaced the previous one)" : "") + ". The tables are empty; you can ask questions about them.\n", Accent, false);
            SetStatus("Reading the schema into the model (once per database)..."); ai.Warm();
        });
    }
    void Go()
    {
        if (Assistant.IsDesign(ask.Text)) { GoDesign(); return; }
        if (dbFile == null) { SetStatus("Open a database file first, or press Design from scenario."); return; }
        var qs = Assistant.Split(ask.Text); if (qs.Count == 0) return;
        ask.Clear();
        Bg("Thinking...", () =>
        {
            for (int i = 0; i < qs.Count; i++)
            {
                string tag = qs.Count > 1 ? "Question " + (i + 1) + " of " + qs.Count + ": " : "";
                Write("\n" + (qs.Count > 1 ? (i + 1) + ". " : "") + qs[i] + "\n", Accent, false);
                int start = 0; var gate = new ManualResetEvent(false);
                UI(() => { start = outBox.TextLength; gate.Set(); }); gate.WaitOne(2000);
                var a = ai.Ask(qs[i], s => SetStatus(tag + s), s => Write(s, Color.Gray, true));
                UI(() => { outBox.Select(start, outBox.TextLength - start); outBox.ReadOnly = false; outBox.SelectedText = ""; outBox.ReadOnly = true; });
                if (a.IsText) { Write(a.Text + "\n", Ink, false); sqlLog.AppendLine("-- " + qs[i] + "\r\n/* " + a.Text.Replace("*/", "* /") + " */\r\n"); continue; }
                Write(a.Sql + "\n", Ink, true);
                UI(() => lastSql = a.Sql);
                sqlLog.AppendLine("-- " + (i + 1) + ". " + qs[i] + "\r\n" + a.Sql.Replace("\n", "\r\n") + "\r\n");
                if (a.Ok) Write("Runs without errors (" + a.Seconds.ToString("0") + " s).\n", Color.SeaGreen, false);
                else Write("Could not get this one to run: " + a.Error + "\n", Color.Firebrick, false);
                foreach (var n in a.Notes) Write("Check this answer: " + n + ".\n", Color.DarkOrange, false);
                if (a.Ok) Write(Pretty(a.Output) + "\n", Color.FromArgb(70, 78, 88), true);
                UI(FillTree);
            }
        });
    }
    public static string Pretty(string o)
    {
        var lines = o.Replace("\r", "").Split('\n').ToList();
        // -vv echoes each statement between dashed lines; drop the echo, keep tables and counts
        var res = new List<string>(); bool echo = false;
        foreach (var l in lines)
        {
            if (Regex.IsMatch(l, "^-{10,}$")) { echo = !echo; continue; }
            if (echo || l.StartsWith("Bye") || l.Trim() == "") continue;
            res.Add(Regex.Replace(l, @" \(\d+[\d.]* sec\)$| \([\d.]+ sec\)", ""));
        }
        while (res.Count > 0 && res[0].StartsWith("Query OK, 0 rows")) res.RemoveAt(0);
        if (res.Count == 0) return "Done.";
        if (res.Count > 70) return string.Join("\n", res.Take(60)) + "\n... (" + (res.Count - 60) + " more lines)";
        return string.Join("\n", res);
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // leftovers of a previous crash
        try { foreach (var d in Directory.GetDirectories(Path.GetTempPath(), "TextToSQL_*")) { int id; if (int.TryParse(Path.GetFileName(d).Substring(10), out id) && id != Process.GetCurrentProcess().Id) { bool alive = true; try { Process.GetProcessById(id); } catch (ArgumentException) { alive = false; } if (!alive) Directory.Delete(d, true); } } } catch { }
        Directory.CreateDirectory(Paths.Work);
        if (args.Length >= 4 && args[0] == "--cli") return Cli(args);
        if (args.Length >= 3 && args[0] == "--design") return CliDesign(args);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(args.Length > 0 && File.Exists(args[0]) ? args[0] : null));
        return 0;
    }
    // TextToSQL.exe --design scenario.txt result.txt
    static int CliDesign(string[] a)
    {
        var ai = new Assistant();
        try
        {
            ai.Db.Start(); ai.Llm.Start();
            var r = ai.Design(File.ReadAllText(a[1]), s => { }, null);
            File.WriteAllText(a[2], r.Sql + "\n\n== ok=" + r.Ok + " tries=" + r.Tries + " seconds=" + r.Seconds.ToString("0.0") + " error=" + r.Error + "\n== notes: " + string.Join(" | ", r.Notes) + "\n" + r.Output, new UTF8Encoding(false));
            return 0;
        }
        catch (Exception x) { File.WriteAllText(a[2], x.ToString()); return 1; }
        finally { ai.Stop(); }
    }
    // TextToSQL.exe --cli database.sql questions.txt answers.json
    static int Cli(string[] a)
    {
        var ai = new Assistant();
        try
        {
            ai.Db.Start(); ai.Llm.Start(); ai.Open(a[1]); ai.Warm();
            var list = new List<object>();
            foreach (var q in Assistant.Split(File.ReadAllText(a[2])))
            {
                var r = ai.Ask(q, s => { }, null);
                string verdict = "";
                if (a.Length > 4)
                {   // gold file: one block per question, separated by lines of ###; "-- PROBE" starts a state check
                    var g = Regex.Split(File.ReadAllText(a[4]).Replace("\r", ""), @"^###.*\n", RegexOptions.Multiline).Where(x => x.Trim() != "").ToArray()[list.Count];
                    var gp = Regex.Split(g, "-- PROBE"); string probe = gp.Length > 1 ? gp[1] : "";
                    Func<string, List<string>> run = x => { if (probe != "") { ai.Db.Reset(); ai.Db.Rows(x); } return ai.Db.Rows(probe != "" ? probe : x).Select(z => string.Join("|", z)).ToList(); };
                    var go = run(gp[0]); var po = run(r.Sql); if (probe != "") ai.Db.Reset();
                    bool ord = gp[0].Contains("-- ORDERED");
                    verdict = (ord ? go.SequenceEqual(po) : go.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(po.OrderBy(x => x, StringComparer.Ordinal))) ? "PASS" : "FAIL gold=" + go.Count + " got=" + po.Count;
                }
                list.Add(new { verdict, question = q, sql = r.Sql, text = r.Text, ok = r.Ok, error = r.Error, notes = r.Notes, tries = r.Tries, seconds = Math.Round(r.Seconds, 1), output = MainForm.Pretty(r.Output) });
                File.WriteAllText(a[3], new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(new { db = ai.Db.Db, warnings = ai.Db.LoadWarnings, schema = ai.Db.SchemaText("", 6500), answers = list }), new UTF8Encoding(false));
            }
            return 0;
        }
        catch (Exception x) { File.WriteAllText(a[3], x.ToString()); return 1; }
        finally { ai.Stop(); }
    }
}
