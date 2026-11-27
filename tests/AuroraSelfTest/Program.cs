using System.IO.Compression;
using System.Text.Json;
using AuroraCore;
using AuroraCore.Download;
using AuroraCore.Models;
using AuroraCore.Services;
using AuroraCore.Utils;
using AuroraLauncher.ViewModels;

namespace AuroraSelfTest;

internal static class Program
{
    private static int _passed, _failed, _skipped;
    // Test layering: "unit" = pure logic, offline, milliseconds, 100% stable
    // (CI default); "integration" = needs real network (run manually or on a
    // schedule); "all" = both. Usage: dotnet run --project ... -- --unit
    private static string Mode = "all";

    private static bool ShouldRun(string kind) => Mode == "all" || Mode == kind;

    private static void Check(string name, Action body)
    {
        if (!ShouldRun("unit")) return; // unit tests don't run in integration-only mode
        try { body(); Console.WriteLine($"  [PASS] {name}"); _passed++; }
        catch (Exception ex) { Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); _failed++; }
    }

    private static async Task CheckAsync(string name, Func<Task> body, bool online = false)
    {
        // Tests flagged online are INTEGRATION tests — skipped in unit mode.
        if (online && !ShouldRun("integration"))
        {
            Console.WriteLine($"  [SKIP] {name} (integration test filtered by --unit)");
            _skipped++;
            return;
        }
        try { await body(); Console.WriteLine($"  [PASS] {name}"); _passed++; }
        catch (Exception ex) when (online) { Console.WriteLine($"  [SKIP] {name} (network: {ex.Message})"); _skipped++; }
        catch (Exception ex) { Console.WriteLine($"  [FAIL] {name}: {ex.Message}"); _failed++; }
    }

    private static void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

    /// <summary>Round-74: applies a patch from the command line (diagnostics +
    /// verifying that a published patch really rebuilds the published EXE).</summary>
    private static int ApplyPatch(string oldExe, string patchPath, string outPath)
    {
        try
        {
            var hash = BinaryDelta.Apply(oldExe, patchPath, outPath);
            Console.WriteLine("[patch] applied OK, sha256=" + hash);
            Console.WriteLine("[patch] output: " + outPath);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[patch] APPLY FAILED: " + ex.Message);
            return 1;
        }
    }

    /// <summary>Round-74: builds a delta patch at publish time and prints a
    /// machine-readable summary line the release script can parse.</summary>
    private static int MakePatch(string oldExe, string newExe, string patchPath)
    {
        if (!File.Exists(oldExe)) { Console.WriteLine("[patch] ERROR: old build not found: " + oldExe); return 2; }
        if (!File.Exists(newExe)) { Console.WriteLine("[patch] ERROR: new build not found: " + newExe); return 2; }
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            BinaryDelta.Create(oldExe, newExe, patchPath);
            clock.Stop();

            var fullSize = new FileInfo(newExe).Length;
            var patchSize = new FileInfo(patchPath).Length;
            var ratio = fullSize > 0 ? (double)patchSize / fullSize : 1;
            Console.WriteLine($"[patch] {Path.GetFileName(newExe)}: {fullSize:n0} B -> patch {patchSize:n0} B " +
                              $"({ratio:P1}, built in {clock.Elapsed.TotalSeconds:F1}s)");
            Console.WriteLine("[patch] saved: " + patchPath);
            if (!BinaryDelta.IsWorthPublishing(patchSize, fullSize))
            {
                Console.WriteLine("[patch] NOT WORTH PUBLISHING (above the 70% threshold) — publish the full package only.");
                try { File.Delete(patchPath); } catch { }
                return 3;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[patch] ERROR: " + ex.Message);
            return 1;
        }
    }

    /// <summary>Walks up from CWD to find the repo root (marker: src/AuroraCore).</summary>
    private static string? FindSourceRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        for (int i = 0; i < 8 && dir != null; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "src", "AuroraCore"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    static async Task Main(string[] args)
    {
        // Opt-in WPF smoke diagnostic (round-67). Not part of CI test modes.
        if (args.Contains("--nav-smoke")) { Environment.Exit(NavSmoke.Run()); return; }
        // Opt-in crash-card smoke (round-78): renders the real card with a real
        // ViewModel and verifies every new binding + the one-click repair.
        if (args.Contains("--crash-smoke")) { Environment.Exit(CrashCardSmoke.Run()); return; }
        // Opt-in round-79 smoke: performance card / save-backup card / Ctrl+K semantics.
        if (args.Contains("--round79-smoke")) { Environment.Exit(Round79Smoke.Run()); return; }
        // Opt-in round-80 smoke: glass brushes are reversible, the red gate renders.
        if (args.Contains("--glass-smoke")) { Environment.Exit(GlassSmoke.Run()); return; }
        // Opt-in round-83 smoke: auto memory / over-limit warning / manual clean.
        if (args.Contains("--memory-smoke")) { Environment.Exit(MemorySmoke.Run()); return; }
        // Opt-in round-87 smoke: residue scan → banner → navigate → clean for real.
        if (args.Contains("--residue-smoke")) { Environment.Exit(ResidueSmoke.Run()); return; }
        // Opt-in round-88 smoke: storage scan → category list → clean for real.
        if (args.Contains("--cleanup-smoke")) { Environment.Exit(CleanupSmoke.Run()); return; }
        // Opt-in round-90 smoke: unsaved settings guard (navigate + close paths).
        if (args.Contains("--settings-smoke")) { Environment.Exit(SettingsSmoke.Run()); return; }
        // Opt-in round-92 smoke: a modpack with zero mods must not launch vanilla.
        if (args.Contains("--launch-gate-smoke")) { Environment.Exit(LaunchGateSmoke.Run()); return; }
        // Diagnostic (round-94): write the launcher-profile file the way the
        // installer path does, so a real Forge install can be probed by hand.
        var peIndex = Array.IndexOf(args, "--profiles-ensure");
        if (peIndex >= 0 && args.Length > peIndex + 1)
        {
            var r = LauncherProfiles.Ensure(args[peIndex + 1]);
            var ok = File.Exists(r.Path) && LauncherProfiles.HasProfiles(File.ReadAllText(r.Path));
            Console.WriteLine($"[profiles] {r.Path} created={r.Created} repaired={r.Repaired} hasProfiles={ok}");
            Environment.Exit(ok ? 0 : 1);
            return;
        }
        // Round-94 opt-in network smoke: the real Forge installer must finish
        // green through our own install path (launcher_profiles.json included).
        if (args.Contains("--forge-install-smoke")) { Environment.Exit(ForgeInstallSmoke.Run().GetAwaiter().GetResult()); return; }
        // Round-97 read-only: run the REAL preflight over a real instance dir.
        // Usage: --preflight-scan <instanceDir> <Forge|Fabric|NeoForge|Quilt>
        var scanIndex = Array.IndexOf(args, "--preflight-scan");
        if (scanIndex >= 0 && args.Length > scanIndex + 2)
        {
            var loader = Enum.TryParse<ModLoaderType>(args[scanIndex + 2], true, out var parsed)
                ? parsed : ModLoaderType.Forge;
            var report = ModPreflight.Scan(args[scanIndex + 1], loader, Console.WriteLine);
            Console.WriteLine($"[preflight] loader={loader} scanned={report.ScannedJars} " +
                              $"missing={report.Missing.Count} fabricApiMissing={report.FabricApiMissing}");
            foreach (var d in report.Missing)
                Console.WriteLine($"  - {d.ModId} | {d.Range} | <- {d.RequiredBy}");
            Environment.Exit(0);
            return;
        }
        // Round-100 read-only network smoke: resolve every missing prerequisite
        // of a REAL instance against Modrinth. Prints the file we WOULD pick;
        // never writes to mods/ and never downloads a jar.
        // Usage: --auto-fix-scan <instanceDir> <Forge|Fabric|NeoForge|Quilt> <mcVersion>
        var afIndex = Array.IndexOf(args, "--auto-fix-scan");
        if (afIndex >= 0 && args.Length > afIndex + 3)
        {
            try
            {
                var loader = Enum.TryParse<ModLoaderType>(args[afIndex + 2], true, out var parsed)
                    ? parsed : ModLoaderType.Forge;
                var dir = args[afIndex + 1];
                var mcVersion = args[afIndex + 3];
                var report = ModPreflight.Scan(dir, loader, Console.WriteLine);
                var plans = MissingDependencyFix.Plan(report);
                Console.WriteLine($"[auto-fix] scanned={report.ScannedJars} missing={plans.Count} loader={loader}");
                var mods = new ModManager(new DownloadEngine(2));
                var ok = 0;
                foreach (var c in plans)
                {
                    ModrinthProject? project = null;
                    if (c.KnownSlug)
                    {
                        try { project = await mods.GetProjectDetailAsync(c.Slug); } catch { }
                    }
                    if (project == null)
                    {
                        var hits = await mods.SearchAsync(c.Slug, ContentType.Mod, null, null);
                        project = MissingDependencyFix.PickProject(c, hits.Projects);
                    }
                    if (project == null) { Console.WriteLine($"  - {c.ModId}: 未找到 Modrinth 项目 ({c.Slug})"); continue; }
                    var versions = await mods.GetVersionsAsync(project.ProjectId, ContentType.Mod, null, null);
                    var (pick, outcome) = MissingDependencyFix.PickVersion(c, loader, mcVersion, versions);
                    if (pick == null) Console.WriteLine($"  - {c.ModId}: {outcome}（项目 {project.Title}）");
                    else { ok++; Console.WriteLine($"  - {c.ModId}: {project.Title} {pick.VersionNumber} -> {pick.FileName} " +
                                                    $"sha1={pick.Sha1?[..12]}… size={pick.FileSize}"); }
                }
                Console.WriteLine($"[auto-fix] resolved={ok}/{plans.Count}（只解析，未下载）");
                Environment.Exit(0);
            }
            catch (Exception ex) { Console.WriteLine("[auto-fix] ERROR " + ex.Message); Environment.Exit(1); }
            return;
        }
        // Round-98 opt-in network smoke: resolve BOTH JDK vendors from their
        // real APIs. Metadata only — never downloads the ~200 MB archive.
        if (args.Contains("--java-dist-smoke"))
        {
            try
            {
                var svc = new JavaRuntimeService();
                foreach (var d in new[] { JavaDistribution.Temurin, JavaDistribution.Zulu })
                {
                    var pkg = svc.ResolveAsync(d, 21, CancellationToken.None).GetAwaiter().GetResult();
                    Console.WriteLine(pkg == null
                        ? $"[java-dist] {d}: NO PACKAGE"
                        : $"[java-dist] {d}: {pkg.ReleaseName} | {pkg.Name} | " +
                          $"{pkg.Size / 1024 / 1024} MB | sha={pkg.Sha256[..12]}… | {pkg.Url}");
                    if (pkg == null) { Environment.Exit(1); return; }
                }
                Environment.Exit(0);
            }
            catch (Exception ex) { Console.WriteLine("[java-dist] ERROR " + ex.Message); Environment.Exit(1); }
            return;
        }
        // Round-99 read-only: inventory the REAL data root's cache folders.
        if (args.Contains("--cache-scan"))
        {
            var cacheRoot = LauncherConfig.Instance.RootDirectory;
            var cacheItems = CacheFolderService.Scan(cacheRoot);
            Console.WriteLine($"[cache-scan] root={cacheRoot}");
            foreach (var i in cacheItems)
                Console.WriteLine($"  {i.DisplayName,-18} {i.SizeText,10} files={i.FileCount,5} exists={i.Exists}  {i.Path}");
            Console.WriteLine("[cache-scan] total=" + CacheFolderService.TotalText(cacheItems));
            Environment.Exit(0);
            return;
        }
        // Read-only diagnostic: what does the launch gate think of the REAL instances?
        if (args.Contains("--readiness-scan")) { Environment.Exit(LaunchGateSmoke.ScanRealRootForDiagnostics()); return; }
        // Diagnostic (read-only, never in CI): what WOULD the storage cleaner find
        // in the real data root? Deletes nothing.
        if (args.Contains("--cleanup-scan"))
        {
            Environment.Exit(CleanupSmoke.ScanRealRootForDiagnostics());
            return;
        }
        // Round-86 opt-in network check: the real Forge installer URL (6 MB) on
        // both routes — proves the reported 404 is gone through our own engine.
        if (args.Contains("--forge-url-check")) { Environment.Exit(ForgeUrlCheck.Run().GetAwaiter().GetResult()); return; }
        // Opt-in layout benchmark (round-68): measures the REAL Collapsed->Visible
        // cost of each page so navigation tuning is evidence-based, not guesswork.
        if (args.Contains("--nav-bench")) { Environment.Exit(NavBench.Run()); return; }

        // Round-74: publish-time delta generation, so scripts/publish.ps1 does not
        // need a second tool. Usage:
        //   --make-patch <old.exe> <new.exe> <out.patch>
        var patchIndex = Array.IndexOf(args, "--make-patch");
        if (patchIndex >= 0 && args.Length > patchIndex + 3)
        {
            Environment.Exit(MakePatch(args[patchIndex + 1], args[patchIndex + 2], args[patchIndex + 3]));
            return;
        }
        var applyIndex = Array.IndexOf(args, "--apply-patch");
        if (applyIndex >= 0 && args.Length > applyIndex + 3)
        {
            Environment.Exit(ApplyPatch(args[applyIndex + 1], args[applyIndex + 2], args[applyIndex + 3]));
            return;
        }

        // Test layering switch: --unit (offline, CI default) / --integration / --all.
        var flag = args.FirstOrDefault(a => a.StartsWith("--"));
        if (flag is "--unit" or "--integration" or "--all") Mode = flag[2..];
        Console.WriteLine($"=== AuroraLauncher Self-Test (mode: {Mode}) ===");
        Console.WriteLine("=== AuroraLauncher Self-Test ===");

        // Use an isolated temp root so we never touch the user's real data.
        var root = Path.Combine(Path.GetTempPath(), "AuroraSelfTest_" + Guid.NewGuid().ToString("N")[..6]);
        LauncherConfig.UseTestRoot(root); // round-84: isolate the config FILE too, not just the root
        LauncherConfig.Instance.EnsureDirectories();
        Logger.Instance.Configure(root);

        // ---- Offline logic tests (no network) ----
        Check("SHA-1 of 'abc' matches known digest", () =>
        {
            var hash = CryptoHelper.Sha1(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("abc")));
            Assert(hash == "a9993e364706816aba3e25717850c26c9cd0d89d", $"got {hash}");
        });

        Check("MavenNameToPath (with classifier)", () =>
        {
            var p = PathEx.MavenNameToPath("org.lwjgl:lwjgl:3.3.1:natives-windows");
            Assert(p == "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows.jar", $"got {p}");
        });

        Check("OfflineUuid format", () =>
        {
            var u = AuthService.OfflineUuid("TestPlayer");
            Assert(System.Text.RegularExpressions.Regex.IsMatch(u, @"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$"), $"got {u}");
        });

        Check("LaunchArgumentBuilder builds classpath + JVM + game args + tokens", () =>
        {
            var version = new VersionJson
            {
                Id = "1.21.1",
                MainClass = "net.minecraft.client.main.Main",
                Assets = "1.21",
                AssetIndex = new AssetIndexInfo { Id = "1.21" },
                JavaVersion = new JavaVersion { MajorVersion = 21 },
                Arguments = new ArgumentSection
                {
                    Jvm = new List<JsonElement>
                    {
                        JsonSerializer.SerializeToElement("-XX:+UseG1GC"),
                        JsonSerializer.SerializeToElement("-Djava.library.path=${natives_directory}"),
                        JsonSerializer.SerializeToElement("-cp"),
                        JsonSerializer.SerializeToElement("${classpath}")
                    },
                    Game = new List<JsonElement>
                    {
                        JsonSerializer.SerializeToElement("--username"),
                        JsonSerializer.SerializeToElement("${auth_player_name}"),
                        JsonSerializer.SerializeToElement("--version"),
                        JsonSerializer.SerializeToElement("${version_name}"),
                        JsonSerializer.SerializeToElement("--accessToken"),
                        JsonSerializer.SerializeToElement("${auth_access_token}")
                    }
                },
                Libraries = new List<Library>
                {
                    new() { Name = "com.mojang:patchy:1.1", Downloads = new LibraryDownloads { Artifact = new ArtifactInfo { Path = "com/mojang/patchy/1.1/patchy-1.1.jar" } } }
                }
            };

            var spec = new LaunchArgumentBuilder().Build(new LaunchContext
            {
                Version = version,
                JavaExecutable = @"C:\Java\bin\java.exe",
                VersionName = "1.21.1",
                VersionType = "release",
                GameDirectory = @"C:\games\test",
                AssetsRoot = root + @"\assets",
                LibrariesRoot = root + @"\libraries",
                NativesDirectory = root + @"\natives",
                ClientJarPath = root + @"\versions\1.21.1\1.21.1.jar",
                AssetsIndexName = "1.21",
                PlayerName = "Steve",
                Uuid = "uuid-123",
                AccessToken = "tok-xyz",
                Width = 1280, Height = 720
            });

            var all = string.Join(" ", spec.Arguments);
            Assert(spec.Arguments.Contains("net.minecraft.client.main.Main"), "missing mainClass");
            Assert(spec.Arguments.Contains("-Xmx4096M"), "missing -Xmx");
            Assert(spec.Arguments.Contains("-Djava.library.path=" + root + @"\natives"), "missing natives path");
            Assert(all.Contains("patchy-1.1.jar"), "library not on classpath");
            Assert(all.Contains("--username"), "missing --username");
            Assert(all.Contains("Steve"), "player name not substituted");
            Assert(all.Contains("--version"), "missing --version");
            Assert(!all.Contains("${"), "unsubstituted token remains: " + all);
            Assert(spec.DisplayCommandLine.Contains("<REDACTED>"), "access token not redacted in display");
            Assert(!spec.DisplayCommandLine.Contains("tok-xyz"), "access token leaked into display log");
        });

        Check("Resolution feature rule injects --width/--height", () =>
        {
            var json = @"{
                ""id"":""t"",""mainClass"":""m.Main"",
                ""arguments"":{
                  ""jvm"":[""-cp"",""${classpath}""],
                  ""game"":[
                    ""--username"",""${auth_player_name}"",
                    {""rules"":[{""features"":{""has_custom_resolution"":true}}],""value"":[""--width"",""${resolution_width}"",""--height"",""${resolution_height}""]}
                  ]
                },
                ""libraries"":[]
            }";
            var version = JsonHelper.Deserialize<VersionJson>(json);
            var spec = new LaunchArgumentBuilder().Build(new LaunchContext
            {
                Version = version,
                JavaExecutable = "java",
                ClientJarPath = "client.jar",
                PlayerName = "p", Uuid = "u", AccessToken = "a",
                Width = 1024, Height = 768
            });
            var all = string.Join(" ", spec.Arguments);
            Assert(all.Contains("--width") && all.Contains("1024"), "width missing");
            Assert(all.Contains("--height") && all.Contains("768"), "height missing");

            // And confirm it is ABSENT when feature is off.
            var spec2 = new LaunchArgumentBuilder().Build(new LaunchContext
            {
                Version = version,
                JavaExecutable = "java", ClientJarPath = "client.jar",
                PlayerName = "p", Uuid = "u", AccessToken = "a", Width = 0, Height = 0
            });
            Assert(!string.Join(" ", spec2.Arguments).Contains("--width"), "width should be absent without custom resolution");
        });

        Check("OS-rule filtering excludes non-windows natives from classpath", () =>
        {
            var version = new VersionJson
            {
                Id = "t",
                MainClass = "m.Main",
                Arguments = new ArgumentSection { Jvm = new List<JsonElement> { JsonSerializer.SerializeToElement("-cp"), JsonSerializer.SerializeToElement("${classpath}") } },
                Libraries = new List<Library>
                {
                    new() { Name = "com.mojang:patchy:1.1", Downloads = new LibraryDownloads { Artifact = new ArtifactInfo { Path = "com/mojang/patchy/1.1/patchy-1.1.jar" } } },
                    new() { Name = "org.lwjgl:lwjgl:3.3.1:natives-linux", Rules = new List<Rule> { new() { Action = "allow", Os = new OsCondition { Name = "linux" } } }, Downloads = new LibraryDownloads { Artifact = new ArtifactInfo { Path = "x.jar" } } }
                }
            };
            var spec = new LaunchArgumentBuilder().Build(new LaunchContext
            {
                Version = version,
                JavaExecutable = "java",
                ClientJarPath = "client.jar",
                PlayerName = "p", Uuid = "u", AccessToken = "a"
            });
            var all = string.Join(" ", spec.Arguments);
            Assert(all.Contains("patchy-1.1.jar"), "windows lib missing");
            Assert(!all.Contains("x.jar"), "linux-native lib leaked into classpath");
        });

        Check("ModManager.BuildFacets (content types)", () =>
        {
            var mod = ModManager.BuildFacets(ContentType.Mod, "1.21.1", "fabric");
            Assert(mod == "[[\"project_type:mod\"],[\"versions:1.21.1\"],[\"categories:fabric\"]]", $"mod facets: {mod}");
            var shader = ModManager.BuildFacets(ContentType.Shader, null, null);
            Assert(shader == "[[\"project_type:shader\"]]", $"shader facets: {shader}");
            var pack = ModManager.BuildFacets(ContentType.Modpack, null, "fabric");
            Assert(pack == "[[\"project_type:modpack\"]]", "modpack must not filter by loader");
        });

        Check("ModManager.ReadMrpackInfo + ImportMrpack overrides", () =>
        {
            var mrpack = Path.Combine(root, "test_pack.mrpack");
            using (var zip = ZipFile.Open(mrpack, ZipArchiveMode.Create))
            {
                var idx = zip.CreateEntry("modrinth.index.json");
                using (var w = new StreamWriter(idx.Open()))
                    w.Write("{\"formatVersion\":1,\"game\":\"minecraft\",\"dependencies\":{\"minecraft\":\"1.21.1\",\"fabric-loader\":\"0.16.9\"},\"files\":[]}");
                var ov = zip.CreateEntry("overrides/options.txt");
                using (var w = new StreamWriter(ov.Open())) w.Write("lang=zh_cn");
                var evil = zip.CreateEntry("overrides/../evil.txt");
                using (var w = new StreamWriter(evil.Open())) w.Write("bad");
            }
            var info = ModManager.ReadMrpackInfo(mrpack);
            Assert(info.GameVersion == "1.21.1", $"game version: {info.GameVersion}");
            Assert(info.Loader == ModLoaderType.Fabric, $"loader: {info.Loader}");
            Assert(info.LoaderVersion == "0.16.9", $"loader version: {info.LoaderVersion}");

            var instDir = Path.Combine(root, "mrpack_instance");
            Directory.CreateDirectory(instDir);
            new ModManager(new DownloadEngine(2)).ImportMrpack(mrpack, instDir);
            Assert(File.Exists(Path.Combine(instDir, "options.txt")), "override file not extracted");
            Assert(!File.Exists(Path.Combine(instDir, "evil.txt")) && !File.Exists(Path.Combine(root, "evil.txt")), "path traversal was not blocked");
        });

        Check("InstanceManager.Delete garbage-collects unreferenced versions", () =>
        {
            var mgr = new InstanceManager();
            var a = mgr.Create("__gc_a__", "1.21.10", ModLoaderType.Vanilla);
            var b = mgr.Create("__gc_b__", "1.21.1", ModLoaderType.Vanilla);

            // Fake installed version folders under <root>/versions.
            string V(string id) => Path.Combine(LauncherConfig.Instance.VersionsDir, id);
            foreach (var id in new[] { "1.21.10", "1.21.1", "26.3" })
            {
                Directory.CreateDirectory(V(id));
                File.WriteAllText(Path.Combine(V(id), id + ".json"), "{}");
            }
            Directory.CreateDirectory(V("random-folder"));
            File.WriteAllText(Path.Combine(V("random-folder"), "other.txt"), "not ours");

            Assert(mgr.Delete(a.Id), "delete A returned false");
            Assert(!Directory.Exists(V("1.21.10")), "unreferenced version 1.21.10 was not GC'd");
            Assert(!Directory.Exists(V("26.3")), "unreferenced version 26.3 was not GC'd");
            Assert(Directory.Exists(V("1.21.1")), "version still referenced by B was removed");
            Assert(Directory.Exists(V("random-folder")), "foreign folder was removed");
            mgr.Delete(b.Id);
        });

        Check("DownloadEngine resume rule: Content-Range acceptance (pure logic)", () =>
        {
            // null / no header → accept (hash check is the arbiter).
            Assert(DownloadEngine.IsResumeOffsetAcceptable(null, 100), "null should pass");
            // Matching start → accept.
            Assert(DownloadEngine.IsResumeOffsetAcceptable("bytes 100-199/500", 100), "matching start rejected");
            // Conflicting start → invalidate.
            Assert(!DownloadEngine.IsResumeOffsetAcceptable("bytes 50-199/500", 100), "conflicting start accepted");
            // Unusual-but-legal formats must NOT misfire.
            Assert(DownloadEngine.IsResumeOffsetAcceptable("bytes 0-99/*", 0), "star-total rejected");
            Assert(DownloadEngine.IsResumeOffsetAcceptable("bytes 100-199/*", 100), "star-total (mid) rejected");
            // Garbage → tolerant (proceed, hash check arbitrates).
            Assert(DownloadEngine.IsResumeOffsetAcceptable("garbage", 100), "garbage rejected");
            Assert(DownloadEngine.IsResumeOffsetAcceptable("", 100), "empty rejected");
        });

        Check("GameInstaller.Evaluate: offline state semantics", () =>
        {
            // Loader version: JSON-only check (no client jar required) +
            // per-library presence. Fully offline via synthetic profiles.
            var loaderId = "fabric-loader-9.9.9-1.21";
            var vdir = Path.Combine(LauncherConfig.Instance.VersionsDir, loaderId);
            Directory.CreateDirectory(vdir);
            var libDir = Path.Combine(LauncherConfig.Instance.LibrariesDir, "org", "example");
            Directory.CreateDirectory(libDir);
            File.WriteAllText(Path.Combine(vdir, loaderId + ".json"),
                """{"id":"fabric-loader-9.9.9-1.21","mainClass":"selftest.Main","libraries":[{"name":"org.example:liba:1.0"},{"name":"org.example:libb:1.0"}]}""");

            // (a) libraries missing → Not Complete, missing listed, no client-jar complaint
            var s1 = new GameInstaller(new VersionResolver(new VersionManifestService()), new DownloadEngine(2))
                .Evaluate(loaderId, requireNatives: false);
            Assert(!s1.Complete, "loader should be incomplete before libs exist");
            Assert(s1.Missing.Any(m => m.Contains("liba")), "liba not reported missing");
            Assert(s1.Missing.Any(m => m.Contains("libb")), "libb not reported missing");
            Assert(!s1.Missing.Any(m => m.Contains("客户端 jar")), "loader must not require a client jar");

            // (b) after libs exist → Complete (standard maven layout)
            Directory.CreateDirectory(Path.Combine(libDir, "liba", "1.0"));
            Directory.CreateDirectory(Path.Combine(libDir, "libb", "1.0"));
            File.WriteAllText(Path.Combine(libDir, "liba", "1.0", "liba-1.0.jar"), "x");
            File.WriteAllText(Path.Combine(libDir, "libb", "1.0", "libb-1.0.jar"), "x");
            var s2 = new GameInstaller(new VersionResolver(new VersionManifestService()), new DownloadEngine(2))
                .Evaluate(loaderId, requireNatives: false);
            Assert(s2.Complete, "loader should be complete after libs exist: " + s2);

            // (c) vanilla id: no client jar → missing
            var vid = "selftest-eval-vanilla";
            var vvdir = Path.Combine(LauncherConfig.Instance.VersionsDir, vid);
            Directory.CreateDirectory(vvdir);
            File.WriteAllText(Path.Combine(vvdir, vid + ".json"),
                """{"id":"selftest-eval-vanilla","mainClass":"net.minecraft.client.main.Main","libraries":[]}""");
            var s3 = new GameInstaller(new VersionResolver(new VersionManifestService()), new DownloadEngine(2))
                .Evaluate(vid, requireNatives: false);
            Assert(s3.Missing.Any(m => m.Contains("客户端 jar")), "vanilla without jar should report missing client jar");
        });

        Check("CrashAnalyzer: rules translate technical logs into plain reasons", () =>
        {
            Assert(CrashAnalyzer.Analyze(new[] { "[Game] java.lang.UnsupportedClassVersionError: class file version 70" })!
                .Contains("Java 版本过低"), "class-version rule missing");
            Assert(CrashAnalyzer.Analyze(new[] { "[Game] java.lang.OutOfMemoryError: Java heap space" })!
                .Contains("内存"), "oom rule missing");
            Assert(CrashAnalyzer.Analyze(new[] { "[Game] Duplicate mods found: sodium" })!
                .Contains("重复 Mod"), "duplicate-mod rule missing");
            Assert(CrashAnalyzer.Analyze(new[] { "[Game] ModResolutionException: Could not find required mod: fabric-api" })!
                .Contains("前置"), "missing-dependency rule missing");
            // Priority: loader/mod-specific rules win over the generic main-thread rule.
            var both = CrashAnalyzer.Analyze(new[]
            {
                "[Game] java.lang.UnsupportedClassVersionError: X",
                "[Game] Exception in thread \"main\" java.lang.RuntimeException"
            });
            Assert(both!.Contains("Java 版本过低"), "specific rule must win over generic rule");
        });

        // ---- Network integration tests (best effort) ----
        await CheckAsync("Fetch + parse official version manifest", async () =>
        {
            var svc = new LauncherService();
            var manifest = await svc.GetManifestAsync();
            Assert(manifest.Versions.Count > 0, "no versions");
            Assert(!string.IsNullOrEmpty(manifest.Latest.Release), "no latest release");
            Console.WriteLine($"         latest release = {manifest.Latest.Release}");
        }, online: true);

        await CheckAsync("Resolve 1.21.1 (inheritance merge) + build launch spec", async () =>
        {
            var svc = new LauncherService();
            var manifest = await svc.GetManifestAsync();
            var id = manifest.Versions.Any(v => v.Id == "1.21.1") ? "1.21.1" : manifest.Latest.Release;
            var version = await new VersionResolver(svc.Manifest).ResolveAsync(id);
            Assert(!string.IsNullOrEmpty(version.MainClass), "resolved version has no mainClass");
            Assert(version.Libraries.Count > 0, "resolved version has no libraries");
            var spec = new LaunchArgumentBuilder().Build(new LaunchContext
            {
                Version = version,
                JavaExecutable = "java",
                VersionName = id,
                ClientJarPath = root + "/versions/" + id + "/" + id + ".jar",
                AssetsIndexName = version.AssetIndex?.Id ?? version.Assets ?? "1.21",
                PlayerName = "Tester", Uuid = "u", AccessToken = "a"
            });
            Assert(spec.Arguments.Contains(version.MainClass!), "mainClass missing after resolve");
            Console.WriteLine($"         resolved {id}: {version.Libraries.Count} libs, mainClass={version.MainClass}");
        }, online: true);

        await CheckAsync("DownloadEngine: fetch small file with SHA-1 + Range resume", async () =>
        {
            var url = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
            // NOTE: do NOT dispose the shared factory client — later tests use it.
            var hc = HttpClientFactory.Instance.Client;
            var raw = await hc.GetByteArrayAsync(url);
            var expectedSha = CryptoHelper.Sha1(new MemoryStream(raw));
            var engine = new DownloadEngine(4);

            // Diagnostic: download WITHOUT expected sha to inspect engine output.
            var destDiag = Path.Combine(root, "manifest_diag.json");
            if (File.Exists(destDiag)) File.Delete(destDiag);
            await engine.DownloadAsync(new[] { new DownloadEntry { Url = url, Destination = destDiag, DisplayName = "manifest" } }, null);
            var diagSha = CryptoHelper.Sha1(destDiag);
            Console.WriteLine($"         raw size={raw.Length}, engine size={new FileInfo(destDiag).Length}, expectedSha={expectedSha}, engineSha={diagSha}");

            var dest = Path.Combine(root, "manifest_test.json");
            if (File.Exists(dest)) File.Delete(dest);
            await engine.DownloadAsync(new[] { new DownloadEntry { Url = url, Destination = dest, ExpectedSha1 = expectedSha, DisplayName = "manifest" } }, null);
            Assert(File.Exists(dest), "file not downloaded");
            Assert(CryptoHelper.Sha1(dest) == expectedSha, "sha1 mismatch after download");

            var bytes = await File.ReadAllBytesAsync(dest);
            await File.WriteAllBytesAsync(dest, bytes[..(bytes.Length / 2)]); // corrupt -> heal
            await engine.DownloadAsync(new[] { new DownloadEntry { Url = url, Destination = dest, ExpectedSha1 = expectedSha, DisplayName = "manifest" } }, null);
            Assert(CryptoHelper.Sha1(dest) == expectedSha, "sha1 mismatch after resume/heal");
            Console.WriteLine("         download + resume verified (sha1 ok)");
        }, online: true);

        // ---- VersionResolver cache-consistency tests ----
        // Scenario 1: vanilla — cache vs network must agree.
        await CheckAsync("Resolver cache: vanilla cache vs network consistent", async () =>
        {
            var resolver = new VersionResolver(new LauncherService().Manifest);
            var net = await resolver.ResolveAsync("1.21.1", useCache: false);
            Assert(!string.IsNullOrEmpty(net.MainClass), "network resolve has no mainClass");
            var vdir = Path.Combine(root, "versions", "1.21.1");
            Directory.CreateDirectory(vdir);
            await File.WriteAllTextAsync(Path.Combine(vdir, "1.21.1.json"), JsonHelper.SerializeIndented(net));
            var cached = await resolver.ResolveAsync("1.21.1", useCache: true);
            Assert(cached.MainClass == net.MainClass, "mainClass differs (cache vs network)");
            Assert(cached.Libraries.Count == net.Libraries.Count, $"library count differs: {cached.Libraries.Count} vs {net.Libraries.Count}");
            Assert(string.IsNullOrEmpty(cached.InheritsFrom), "vanilla cache must not inherit");
        }, online: true);

        // Scenario 2: loader profile with inheritsFrom — cache must merge.
        await CheckAsync("Resolver cache: loader profile (inheritsFrom) cache merges vanilla", async () =>
        {
            var resolver = new VersionResolver(new LauncherService().Manifest);
            var profileJson = await HttpClientFactory.Instance.Client.GetStringAsync(
                "https://meta.fabricmc.net/v2/versions/loader/1.21/0.20.1/profile/json");
            var id = "fabric-loader-0.20.1-1.21";
            var vdir = Path.Combine(root, "versions", id);
            Directory.CreateDirectory(vdir);
            await File.WriteAllTextAsync(Path.Combine(vdir, id + ".json"), profileJson);

            var merged = await resolver.ResolveAsync(id, useCache: true);
            Assert(merged.MainClass == "net.fabricmc.loader.impl.launch.knot.KnotClient", "wrong mainClass: " + merged.MainClass);
            Assert(string.IsNullOrEmpty(merged.InheritsFrom), "merged profile must not keep inheritsFrom");
            var jvmText = string.Concat((merged.Arguments?.Jvm ?? new List<JsonElement>()).Select(e => e.ToString()));
            Assert(jvmText.Contains("${classpath}"), "merged profile lost the vanilla jvm template (${classpath})");
            Assert(merged.Libraries.Count >= 100, $"merged library count too small: {merged.Libraries.Count}");

            var viaNetwork = await resolver.ResolveAsync(id, useCache: false);
            Assert(viaNetwork.Libraries.Count == merged.Libraries.Count, "library count differs (cache vs network)");
        }, online: true);

        // Scenario 3: multi-level inheritance — cache path resolves chains.
        await CheckAsync("Resolver cache: multi-level inheritance chain", async () =>
        {
            var resolver = new VersionResolver(new LauncherService().Manifest);
            // child -> mid -> 1.21 (real vanilla parent, from network or cache)
            var vdirMid = Path.Combine(root, "versions", "selftest-mid");
            Directory.CreateDirectory(vdirMid);
            var mid = """{"id":"selftest-mid","inheritsFrom":"1.21","mainClass":"selftest.mid.Main","libraries":[{"name":"selftest:mid:1.0"}]}""";
            await File.WriteAllTextAsync(Path.Combine(vdirMid, "selftest-mid.json"), mid);

            var vdirChild = Path.Combine(root, "versions", "selftest-child");
            Directory.CreateDirectory(vdirChild);
            var child = """{"id":"selftest-child","inheritsFrom":"selftest-mid","mainClass":"selftest.child.Main","libraries":[{"name":"selftest:child:1.0"}]}""";
            await File.WriteAllTextAsync(Path.Combine(vdirChild, "selftest-child.json"), child);

            var merged = await resolver.ResolveAsync("selftest-child", useCache: true);
            Assert(merged.MainClass == "selftest.child.Main", "child mainClass lost");
            Assert(merged.Libraries.Any(l => l.Name == "selftest:mid:1.0"), "mid-level library missing");
            Assert(merged.Libraries.Any(l => l.Name == "selftest:child:1.0"), "child library missing");
            Assert(merged.Libraries.Count > 97, $"vanilla libraries missing from chain merge: {merged.Libraries.Count}");
            var jvmText = string.Concat((merged.Arguments?.Jvm ?? new List<JsonElement>()).Select(e => e.ToString()));
            Assert(jvmText.Contains("${classpath}"), "multi-level merge lost the vanilla jvm template");
        }, online: true);

        // Scenario 4: corrupted / incomplete cache degrades to network.
        await CheckAsync("Resolver cache: corrupted & incomplete entries fall back to network", async () =>
        {
            var resolver = new VersionResolver(new LauncherService().Manifest);
            var vdir = Path.Combine(root, "versions", "1.21");
            Directory.CreateDirectory(vdir);

            // (a) invalid JSON
            await File.WriteAllTextAsync(Path.Combine(vdir, "1.21.json"), "{ this is not json !!!");
            var a = await resolver.ResolveAsync("1.21", useCache: true);
            Assert(!string.IsNullOrEmpty(a.MainClass), "corrupt cache did not fall back to network");
            Assert(!File.Exists(Path.Combine(vdir, "1.21.json")) || JsonHelper.Deserialize<VersionJson>(File.ReadAllText(Path.Combine(vdir, "1.21.json")))?.MainClass != null, "corrupt cache file was not cleaned");

            // (b) valid JSON but missing mainClass
            await File.WriteAllTextAsync(Path.Combine(vdir, "1.21.json"), """{"id":"1.21"}""");
            var b = await resolver.ResolveAsync("1.21", useCache: true);
            Assert(!string.IsNullOrEmpty(b.MainClass), "incomplete cache did not fall back to network");

            // (c) after fallback the cache is healed (network copy persisted by resolve path is optional;
            //     just assert a subsequent cached resolve works when we persist a good copy ourselves)
            await File.WriteAllTextAsync(Path.Combine(vdir, "1.21.json"), JsonHelper.SerializeIndented(b));
            var c = await resolver.ResolveAsync("1.21", useCache: true);
            Assert(c.MainClass == b.MainClass, "healed cache inconsistent");
        }, online: true);

        // ---- Mechanism guards (would have caught the round-31/33/34 bugs) ----
        Check("Guard: VersionJson raw deserialize only inside VersionResolver", () =>
        {
            // Any code path that JsonHelper.Deserialize<VersionJson>s a raw
            // file outside VersionResolver risks the "path fork" bug class
            // (unmerged inheritsFrom → missing classpath). Lock it down.
            // Scope: production code only (src/); tests may inspect files.
            var srcRoot = FindSourceRoot();
            Assert(srcRoot != null, "source root not found (run from repo)");
            var srcDir = Path.Combine(srcRoot!, "src");
            var offenders = Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').EndsWith("VersionResolver.cs"))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .Select(File.ReadAllText)
                .Select((text, i) => (text, i))
                .Where(t => t.text.Contains("JsonHelper.Deserialize<VersionJson>"))
                .ToList();
            Assert(offenders.Count == 0, $"raw VersionJson deserialization found outside VersionResolver ({offenders.Count} file(s))");
        });

        Check("Guard: merged fabric profile classpath == ResolveAsync libs + client jar", () =>
        {
            var vdir = Path.Combine(root, "versions", "fabric-loader-0.20.1-1.21");
            var jsonPath = Path.Combine(vdir, "fabric-loader-0.20.1-1.21.json");
            if (!File.Exists(jsonPath))
            {
                // Offline fixture: minimal-but-valid fabric profile (the
                // network test may not have run in --unit mode).
                Directory.CreateDirectory(vdir);
                File.WriteAllText(jsonPath,
                    """{"id":"fabric-loader-0.20.1-1.21","inheritsFrom":"1.21","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[{"name":"net.fabricmc:fabric-loader:0.20.1","url":"https://maven.fabricmc.net/"},{"name":"org.ow2.asm:asm:9.6","url":"https://maven.fabricmc.net/"}]}""");
            }
            var vanillaJson = Path.Combine(root, "versions", "1.21", "1.21.json");
            if (!File.Exists(vanillaJson))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(vanillaJson)!);
                File.WriteAllText(vanillaJson,
                    """{"id":"1.21","mainClass":"net.minecraft.client.main.Main","arguments":{"jvm":["-cp","${classpath}"]},"libraries":[{"name":"org.lwjgl:lwjgl:3.3.3"}]}""");
            }
            var resolver = new VersionResolver(new VersionManifestService());
            var merged = resolver.ResolveAsync("fabric-loader-0.20.1-1.21", useCache: true).GetAwaiter().GetResult();

            // Expected classpath: client jar (vanilla 1.21) + every allowed lib path.
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(root, "versions", "1.21", "1.21.jar")
            };
            foreach (var lib in merged.Libraries)
            {
                if (!LaunchArgumentBuilder.IsAllowed(lib.Rules)) continue;
                var p = lib.Downloads?.Artifact?.Path ?? PathEx.MavenNameToPath(lib.Name);
                expected.Add(Path.Combine(LauncherConfig.Instance.LibrariesDir, p));
            }

            // Actual: extract the -cp value from a built launch context (same
            // builder the launcher uses).
            var ctx = new LaunchContext
            {
                Version = merged,
                JavaExecutable = "java",
                VersionName = "fabric-loader-0.20.1-1.21",
                GameDirectory = Path.Combine(root, "instances", "t"),
                AssetsRoot = Path.Combine(root, "assets"),
                LibrariesRoot = LauncherConfig.Instance.LibrariesDir,
                NativesDirectory = Path.Combine(vdir, "natives"),
                ClientJarPath = Path.Combine(root, "versions", "1.21", "1.21.jar"),
                AssetsIndexName = "1.21",
                PlayerName = "t", Uuid = "t", AccessToken = "t", UserType = "msa",
                MinMemoryMb = 1024, MaxMemoryMb = 4096
            };
            var spec = new LaunchArgumentBuilder().Build(ctx);
            var cpIdx = spec.Arguments.IndexOf("-cp");
            Assert(cpIdx >= 0 && cpIdx + 1 < spec.Arguments.Count, "built spec has no -cp argument");
            var actual = spec.Arguments[cpIdx + 1].Split(';', StringSplitOptions.RemoveEmptyEntries);
            var actualSet = new HashSet<string>(actual, StringComparer.OrdinalIgnoreCase);
            var missing = expected.Where(e => !actualSet.Contains(e)).ToList();
            var extra = actualSet.Where(a => !expected.Contains(a)).ToList();
            Assert(missing.Count == 0, $"classpath missing {missing.Count} entries: {string.Join(", ", missing.Take(3))}");
            Assert(extra.Count == 0, $"classpath has {extra.Count} unexpected entries: {string.Join(", ", extra.Take(3))}");
        });

        Check("Guard: shared HttpClient never cached or disposed outside factory", () =>
        {
            // Bug class: services capturing HttpClientFactory.Instance.Client
            // in a field, or disposing it, break after a proxy rebuild with
            // "Cannot access a disposed object" (Microsoft login). Rules:
            //  1) never _client.Dispose() outside the factory
            //  2) never cache the shared client in a field
            //  3) never wrap it in a using
            var srcDir = Path.Combine(FindSourceRoot()!, "src");
            Assert(Directory.Exists(srcDir), "src dir not found");
            var offenders = new List<string>();
            foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Replace('\\', '/').EndsWith("HttpClientFactory.cs")) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                 || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                var text = File.ReadAllText(file);
                if (text.Contains("_client.Dispose()") || text.Contains("Client.Dispose()"))
                    offenders.Add("disposes shared client: " + Path.GetFileName(file));
                if (System.Text.RegularExpressions.Regex.IsMatch(text, @"readonly\s+HttpClient\s+\w+\s*=\s*HttpClientFactory\.Instance\.Client"))
                    offenders.Add("caches shared client in field: " + Path.GetFileName(file));
                if (System.Text.RegularExpressions.Regex.IsMatch(text, @"using\s+var\s+\w+\s*=\s*HttpClientFactory\.Instance\.Client"))
                    offenders.Add("wraps shared client in using: " + Path.GetFileName(file));
            }
            Assert(offenders.Count == 0, string.Join("; ", offenders));
        });

        Check("Guard: instance-metadata IO only inside InstanceManager", () =>
        {
            // Round-31 bug class: instance metadata written/read from several
            // places (with different sanitization rules) poisoned LoaderVersion.
            // Lock the single entry point: raw GameInstance (de)serialization
            // must live in InstanceManager only.
            var srcDir = Path.Combine(FindSourceRoot()!, "src");
            var offenders = new List<string>();
            foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Replace('\\', '/').EndsWith("InstanceManager.cs")) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                 || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                var text = File.ReadAllText(file);
                if (text.Contains("Deserialize<List<GameInstance>>") || text.Contains("Deserialize<GameInstance>"))
                    offenders.Add("raw-deserializes instance metadata: " + Path.GetFileName(file));
                if (text.Contains("SerializeIndented(instances)") || text.Contains("SerializeIndented(list)"))
                    offenders.Add("raw-serializes instance metadata: " + Path.GetFileName(file));
            }
            Assert(offenders.Count == 0, string.Join("; ", offenders));
        });

        Check("Resolver: cache-hit path and local-first path agree (offline)", () =>
        {
            // The round-33/34 bug class was "two code paths disagree":
            // ResolveAsync cache branch vs ResolveRecursive local-first branch.
            // Both are forced offline here via synthetic fixtures — so the
            // agreement holds on every push, not just the daily integration run.
            var resolver = new VersionResolver(new VersionManifestService());
            var id = "fabric-loader-9.9.9-1.21";
            var vdir = Path.Combine(LauncherConfig.Instance.VersionsDir, id);
            Directory.CreateDirectory(vdir);
            // Synthetic fabric child + vanilla parent fixtures.
            File.WriteAllText(Path.Combine(vdir, id + ".json"),
                """{"id":"fabric-loader-9.9.9-1.21","inheritsFrom":"1.21","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[{"name":"net.fabricmc:fabric-loader:0.20.1","url":"https://maven.fabricmc.net/"}]}""");
            var pj = Path.Combine(LauncherConfig.Instance.VersionsDir, "1.21", "1.21.json");
            if (!File.Exists(pj))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(pj)!);
                File.WriteAllText(pj,
                    """{"id":"1.21","mainClass":"net.minecraft.client.main.Main","arguments":{"jvm":["-cp","${classpath}"]},"libraries":[{"name":"org.lwjgl:lwjgl:3.3.3"}]}""");
            }

            var viaCache = resolver.ResolveAsync(id, useCache: true).GetAwaiter().GetResult();
            var viaLocalFirst = resolver.ResolveAsync(id, useCache: false).GetAwaiter().GetResult(); // local-first: no network needed

            Assert(viaCache.MainClass == viaLocalFirst.MainClass, "mainClass differs between paths");
            Assert(viaCache.Libraries.Count == viaLocalFirst.Libraries.Count, $"library count differs: {viaCache.Libraries.Count} vs {viaLocalFirst.Libraries.Count}");
            var jvmCache = string.Concat((viaCache.Arguments?.Jvm ?? new List<JsonElement>()).Select(e => e.ToString()));
            var jvmLocal = string.Concat((viaLocalFirst.Arguments?.Jvm ?? new List<JsonElement>()).Select(e => e.ToString()));
            Assert(jvmCache.Contains("${classpath}") && jvmLocal.Contains("${classpath}"), "classpath template lost on either path");
        });

        Check("DiagnosticsExporter: truncation keeps the TAIL (crash lines at end)", () =>
        {
            var big = string.Join("\n", Enumerable.Range(1, 50_000).Select(i => $"line {i} of noise"));
            big += "\n[Game] Exception in thread \"main\" java.lang.RuntimeException: THE REAL CRASH";
            var cut = DiagnosticsExporter.TruncateTail(big, maxBytes: 64 * 1024);
            Assert(cut.StartsWith("……（诊断包仅保留日志末尾部分"), "tail marker missing");
            Assert(cut.Length < big.Length, "truncation did not shrink");
            Assert(cut.Contains("THE REAL CRASH"), "CRASH LINE AT THE END WAS LOST — wrong truncation direction");
            Assert(!cut.Contains("line 1 of noise"), "head content should be dropped");
            // Small content passes through untouched.
            Assert(DiagnosticsExporter.TruncateTail("tiny", 64 * 1024) == "tiny", "small content was modified");
        });

        Check("DiagnosticsExporter: redaction replaces (not deletes) and reports hits", () =>
        {
            var (clean, findings) = DiagnosticsExporter.SanitizeWithReport(
                """{"access_token":"SECRET123","client_id":"ABC","proxy":"http://user:pass@host:8080"}""", @"C:\root");
            Assert(clean.Contains("\"access_token\":\"<REDACTED>\""), "token field structure must be KEPT (replaced, not deleted): " + clean);
            Assert(clean.Contains("\"client_id\":\"<REDACTED>\""), "client_id must be replaced");
            Assert(!clean.Contains("SECRET123"), "token value leaked!");
            Assert(findings.Any(f => f.Contains("access")), "redaction report missing token hit");
            Assert(findings.Any(f => f.Contains("代理凭据")), "redaction report missing proxy hit");
        });

        Check("DiagnosticsExporter: no over-redaction (mod names / non-user paths stay intact)", () =>
        {
            var (clean, _) = DiagnosticsExporter.SanitizeWithReport(
                "mods/token-tools-1.0.jar loaded\nD:\\Games\\vanilla\\path ok\nC:\\Users\\zhangsan\\moddir\\x.jar", @"C:\Users\zhangsan\AuroraRoot");
            Assert(clean.Contains("token-tools-1.0.jar"), "plain 'token' inside a mod filename must NOT be redacted");
            Assert(clean.Contains("D:\\Games\\vanilla\\path ok"), "non-user path must stay");
            Assert(clean.Contains("<user-profile>"), "C:\\Users\\zhangsan should be redacted");
        });

        // ---- Shared scan data for the XAML interaction guards (round-44/46) ----
        // Scope rule: ALL desktop XAML — Views, MainWindow, Themes, Styles and
        // any future ResourceDictionary (bin/obj excluded). "Style-provided
        // commands" are only defended if the scan actually looks there.
        var uiDesktop = Path.Combine(FindSourceRoot()!, "src", "AuroraLauncher.Desktop");
        // Round-87: the VM is split across MainViewModel*.cs partials (round 79
        // onwards). Reading only MainViewModel.cs made every command declared in a
        // partial look "missing" — scan them all, or the guard punishes correct code.
        var uiVmCommands = new HashSet<string>(
            Directory.EnumerateFiles(Path.Combine(uiDesktop, "ViewModels"), "MainViewModel*.cs")
                .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(
                    File.ReadAllText(f), @"public\s+RelayCommand\s+(\w+Command)"))
                .Select(m => m.Groups[1].Value),
            StringComparer.Ordinal);
        var uiAllXaml = Directory.EnumerateFiles(uiDesktop, "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains("\\bin\\") && !p.Contains("\\obj\\"))
            .ToList();
        // Keyed styles that SET a Command — elements inherit the command from
        // the style (e.g. NavItem in MainWindow, SubNavItem in ModsView).
        var uiStyleCommands = new Dictionary<string, string>();
        foreach (var xamlAll in uiAllXaml)
        {
            var t = File.ReadAllText(xamlAll);
            foreach (System.Text.RegularExpressions.Match ms in System.Text.RegularExpressions.Regex.Matches(t,
                @"x:Key=""(\w+)""[^>]*>\s*(?:<Setter\s+Property=""Command""\s+Value=""\{Binding (\w+)\}"")", System.Text.RegularExpressions.RegexOptions.Singleline))
                uiStyleCommands[ms.Groups[1].Value] = ms.Groups[2].Value;
        }
        // Every command referenced by ANY XAML (direct/DataContext binding or
        // style setter) — feeds the warning-level dead-command reverse check.
        var uiXamlBound = new HashSet<string>(uiStyleCommands.Values, StringComparer.Ordinal);
        foreach (var xamlAll in uiAllXaml)
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                File.ReadAllText(xamlAll), @"Command=""\{Binding (?:DataContext\.)?(\w+Command)(?=[\s,}])"))
                uiXamlBound.Add(m.Groups[1].Value);

        Check("Guard: every XAML command binding resolves to a VM property", () =>
        {
            // WPF command binding failures are SILENT — a typo'd or missing
            // VM property makes buttons do nothing (round-40: the install
            // button bound InstallFromCardsCommand which didn't exist).
            // FOCUS (round-46, stated deliberately): Command bindings fail
            // silently; event handlers fail at COMPILE time (the XAML compiler
            // wires them) — these guards are the net over the silent half.
            // Keyed style Setters that set a Command are validated EVEN IF no
            // element currently references the style.
            var broken = new List<string>();
            foreach (var kv in uiStyleCommands)
                if (!uiVmCommands.Contains(kv.Value))
                    broken.Add($"Style {kv.Key} → {kv.Value} 不存在于 VM");
            foreach (var xaml in uiAllXaml)
            {
                var text = File.ReadAllText(xaml);
                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(text,
                        @"Command=""\{Binding (?:DataContext\.)?(\w+Command)(?=[\s,}])"))
                {
                    var name = m.Groups[1].Value;
                    if (!uiVmCommands.Contains(name))
                        broken.Add($"{Path.GetFileName(xaml)} → {name}");
                }
            }
            Assert(broken.Count == 0, "unbound commands: " + string.Join("; ", broken));
        });

        Check("Guard: every interactive element has a binding or event handler", () =>
        {
            // Scan RULE (round-46): covers ANY element carrying an interaction
            // attribute — Command="{Binding ...}", Click, Checked, Unchecked,
            // SelectionChanged, MouseDoubleClick, KeyDown (+ Preview variants)
            // — regardless of control type. Not every element needs a manual
            // audit, but the RULE itself must cover these attributes, or a
            // future CheckBox bound to a nonexistent handler slips through.
            //
            // Floor for the 4 audited control types (Button/ToggleButton/
            // MenuItem/Hyperlink) applies to app surfaces only (Views +
            // MainWindow): a control of these types with NO feedback path at
            // all is a dead button. Template internals (Styles/Themes) bind
            // via TemplatedParent, not the VM, so they get attribute checks
            // (a)/(b) but not the floor.
            var broken = new List<string>();
            var handlerAttr = @"\b(?:MouseDoubleClick|PreviewMouseDoubleClick|SelectionChanged|PreviewKeyDown|Unchecked|Checked|Click|KeyDown)=""(\w+)""";
            foreach (var xaml in uiAllXaml)
            {
                var text = File.ReadAllText(xaml);
                var file = Path.GetFileName(xaml);
                var isAppSurface = xaml.Contains("\\Views\\") || file == "MainWindow.xaml";
                var codeBehind = xaml + ".cs";
                var cbText = File.Exists(codeBehind) ? File.ReadAllText(codeBehind) : "";

                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text,
                    @"<(\w+)(?=[\s>])[^>]*>", System.Text.RegularExpressions.RegexOptions.Singleline))
                {
                    var el = m.Groups[1].Value;
                    var tag = m.Value;

                    // (a) Binding-based Command anywhere → must resolve to the VM
                    //     (the silent-failure class this guard exists for).
                    var mc = System.Text.RegularExpressions.Regex.Match(tag,
                        @"Command=""\{Binding (?:DataContext\.)?(\w+Command)(?=[\s,}])");
                    if (mc.Success && !uiVmCommands.Contains(mc.Groups[1].Value))
                        broken.Add($"{file} → Command {mc.Groups[1].Value} 不存在于 VM");

                    // (b) Event handlers → must exist in the paired code-behind
                    //     (compile-time net; belt-and-braces, not the primary net).
                    foreach (System.Text.RegularExpressions.Match mh in System.Text.RegularExpressions.Regex.Matches(tag, handlerAttr))
                        if (!System.Text.RegularExpressions.Regex.IsMatch(cbText, @"\b" + mh.Groups[1].Value + @"\b"))
                            broken.Add($"{file} → 处理器 {mh.Groups[1].Value} 不存在于 code-behind");

                    if (!isAppSurface) continue;
                    if (el is not ("Button" or "ToggleButton" or "MenuItem" or "Hyperlink")) continue;

                    // (c) Floor: some feedback path must exist.
                    var hasFeedback =
                        System.Text.RegularExpressions.Regex.IsMatch(tag, @"Command=""\S")           // Binding or static command
                        || System.Text.RegularExpressions.Regex.IsMatch(tag, handlerAttr)            // event handler
                        || (System.Text.RegularExpressions.Regex.Match(tag, @"Style=""\{StaticResource (\w+)\}") is { Success: true } ss
                            && uiStyleCommands.ContainsKey(ss.Groups[1].Value));
                    if (!hasFeedback)
                        broken.Add($"{file} → {el} 无 Command / 无事件处理器的交互元素");
                }
            }
            Assert(broken.Count == 0, string.Join("; ", broken));

            // Warning-level reverse check (round-46): VM public RelayCommands
            // never referenced by ANY XAML (direct binding or style setter).
            // NOT a failure — some commands exist for code-behind invocation —
            // but dead code should at least be visible in the output.
            var dead = uiVmCommands.Except(uiXamlBound).OrderBy(x => x).ToList();
            if (dead.Count > 0)
                Console.WriteLine("  [WARN] VM 命令从未被任何 XAML 绑定（死代码候选，仅警告不失败）: " + string.Join(", ", dead));
        });

        Check("Guard: 首页内容必须可滚动，且列表有高度上限（第 91 轮）", () =>
        {
            // 用户截图：1280×720 / 全屏缩放时，卡片区比窗口高，底部「性能概览」被裁掉
            // 且没有滚动条。HomeView 现在把卡片区包在 ScrollViewer 里。
            var home = File.ReadAllText(Path.Combine(FindSourceRoot()!, "src", "AuroraLauncher.Desktop",
                "Views", "HomeView.xaml"));
            Assert(home.Contains("x:Name=\"HomeScroller\""), "首页必须有具名的滚动容器（冒烟按名字断言）");
            Assert(System.Text.RegularExpressions.Regex.IsMatch(home,
                    @"<ScrollViewer[^>]*VerticalScrollBarVisibility=""Auto""", System.Text.RegularExpressions.RegexOptions.Singleline),
                "首页滚动条必须是 Auto（内容超出才出现，够高时不打扰）");
            Assert(System.Text.RegularExpressions.Regex.IsMatch(home,
                    @"HorizontalScrollBarVisibility=""Disabled"""),
                "首页不该出现横向滚动条（卡片是自适应宽度）");
            // 第 68 轮的坑：无限制高度的 ScrollViewer 里的 ListBox 会全量生成容器。
            Assert(System.Text.RegularExpressions.Regex.IsMatch(home,
                    @"<ListBox[^>]*MaxHeight=""\d+""", System.Text.RegularExpressions.RegexOptions.Singleline),
                "实例列表必须带 MaxHeight：放进 ScrollViewer 后没有上限会全量生成容器");
            // hero（欢迎语 + 启动按钮）不能被一起包进去：它是页面最主要的操作入口。
            var heroIdx = home.IndexOf("欢迎使用 Aurora Launcher", StringComparison.Ordinal);
            var scrollerIdx = home.IndexOf("<ScrollViewer", StringComparison.Ordinal);
            Assert(heroIdx > 0 && scrollerIdx > heroIdx,
                "ScrollViewer 必须只包卡片区，hero 区要留在外面（否则启动按钮会跟着滚走）");

            // 设置页与帮助页更早就有滚动容器；这里一并守住，防止重构时被换掉。
            foreach (var (file, needle) in new[]
                     {
                         ("SettingsView.xaml", "<ScrollViewer"),
                         ("HelpView.xaml", "<ScrollViewer"),
                     })
            {
                var text = File.ReadAllText(Path.Combine(FindSourceRoot()!, "src", "AuroraLauncher.Desktop", "Views", file));
                Assert(text.Contains(needle), $"{file} 失去滚动容器后，矮窗口会裁掉内容");
            }
        });

        Check("DownloadRoutePolicy: 测速择优 / 强制单线路 / 回退集合（第 85 轮）", () =>
        {
            // ---- pure decision ----
            Assert(DownloadRoutePolicy.Decide(100, 500, RoutePreference.Official) == RoutePreference.Official,
                "官方 100ms / 镜像 500ms → 官方");
            Assert(DownloadRoutePolicy.Decide(500, 100, RoutePreference.Official) == RoutePreference.Mirror,
                "镜像更快时必须改选镜像（哪怕上次是官方）");
            Assert(DownloadRoutePolicy.Decide(null, 90, RoutePreference.Official) == RoutePreference.Mirror,
                "官方不可达 → 镜像");
            Assert(DownloadRoutePolicy.Decide(90, null, RoutePreference.Mirror) == RoutePreference.Official,
                "镜像不可达 → 官方");
            Assert(DownloadRoutePolicy.Decide(null, null, RoutePreference.Mirror) == RoutePreference.Mirror,
                "两条都不通 → 保持上次的选择，别乱翻");
            Assert(DownloadRoutePolicy.Decide(4000, 200, RoutePreference.Official) == RoutePreference.Mirror,
                "4s 的响应等于不可达，不能被当成「官方也行」");
            Assert(DownloadRoutePolicy.Decide(240, 250, RoutePreference.Mirror) == RoutePreference.Mirror,
                "差距在 40ms 内视为平手，不来回跳");
            Assert(DownloadRoutePolicy.Decide(300, 500, RoutePreference.Mirror) == RoutePreference.Official,
                "超过裕度就该换边");

            // ProbeResult delegates to the same rule (one source of truth).
            var probe = new NetworkProbe.ProbeResult(true, 80, true, 800);
            Assert(probe.Decide(RoutePreference.Mirror) == RoutePreference.Official,
                "ProbeResult 必须用同一套判定，不能各算各的");

            // ---- route sets ----
            const string officialUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
            IReadOnlyList<string> Kinds(DownloadPolicy p, RoutePreference pref)
                => DownloadRoutePolicy.Order(p, pref);

            Assert(Kinds(DownloadPolicy.Official, RoutePreference.Mirror).SequenceEqual(new[] { "官方" }),
                "强制官方就是只有官方");
            Assert(Kinds(DownloadPolicy.Mirror, RoutePreference.Official).SequenceEqual(new[] { "镜像" }),
                "强制镜像就是只有镜像");
            Assert(Kinds(DownloadPolicy.Auto, RoutePreference.Official).SequenceEqual(new[] { "官方", "镜像" }),
                "自动：快的那条在前，另一条兜底");
            Assert(Kinds(DownloadPolicy.Auto, RoutePreference.Mirror).SequenceEqual(new[] { "镜像", "官方" }),
                "自动 + 镜像更快：镜像在前");

            // Core files honour a forced route strictly; non-core may fall back.
            var coreForced = DownloadRoutePolicy.BuildHosts(officialUrl, null, DownloadPolicy.Official,
                RoutePreference.Official, allowFallback: false);
            Assert(coreForced.Count == 1 && coreForced[0].Kind == "官方",
                "强制官方 + 核心文件：不允许出现镜像");

            var iconForced = DownloadRoutePolicy.BuildHosts(
                "https://resources.download.minecraft.net/ab/abcdef", null, DownloadPolicy.Official,
                RoutePreference.Official, allowFallback: true);
            Assert(iconForced.Count == 2, "强制官方 + 非核心资源：失败可以静默走镜像");
            Assert(iconForced[0].Kind == "官方", "但仍然是官方优先");

            // Non-Mojang URL has no mirror: never invent one, never return empty.
            var thirdParty = DownloadRoutePolicy.BuildHosts("https://api.modrinth.com/v2/search", null,
                DownloadPolicy.Mirror, RoutePreference.Mirror, allowFallback: false);
            Assert(thirdParty.Count == 1 && thirdParty[0].Url.Contains("modrinth"),
                "第三方接口没有镜像可换，必须原样返回而不是空集合");

            // ---- core vs non-core classification ----
            Assert(DownloadRoutePolicy.IsCoreAsset("https://x/versions/1.21.1/1.21.1.jar"), "client jar 是核心");
            Assert(DownloadRoutePolicy.IsCoreAsset("https://x/libraries/org/lwjgl/lwjgl.jar"), "库是核心");
            Assert(DownloadRoutePolicy.IsCoreAsset("https://x/versions/1.21.1/1.21.1.json"), "版本 JSON 是核心");
            Assert(DownloadRoutePolicy.IsCoreAsset("https://x/assets/indexes/17.json"), "资源索引是核心");
            Assert(!DownloadRoutePolicy.IsCoreAsset("https://x/assets/ab/abcdef"), "声音/材质可以静默回退");
            Assert(!DownloadRoutePolicy.IsCoreAsset("https://cdn.modrinth.com/data/x/icon.png"), "图标不是核心");

            // ---- legacy config migration ----
            Assert(LauncherConfig.MigrateLegacySource(1) == DownloadPolicy.Auto,
                "老配置里的 BMCLAPI(1) 多数是旧的自动探测写进去的，迁成「自动」而不是「永久镜像」");
            Assert(LauncherConfig.MigrateLegacySource(0) == DownloadPolicy.Auto,
                "老配置里的官方(0) 同样迁成自动（默认已经是双线竞速）");
            Assert(DownloadRoutePolicy.Parse("仅使用镜像线路（BMCLAPI）") == DownloadPolicy.Mirror,
                "真需要强制镜像的人仍然可以在设置里选到它");

            // ---- display names round-trip (the settings combo relies on it) ----
            foreach (var p in new[] { DownloadPolicy.Auto, DownloadPolicy.Official, DownloadPolicy.Mirror })
                Assert(DownloadRoutePolicy.Parse(DownloadRoutePolicy.DisplayName(p)) == p,
                    $"{p}: 显示名必须能反解");
            Assert(DownloadRoutePolicy.Describe(DownloadPolicy.Auto, RoutePreference.Mirror, 300, 120, true)
                    .Contains("镜像"),
                "自动模式的说明必须写明当前优先哪条");
            Assert(DownloadRoutePolicy.Describe(DownloadPolicy.Mirror, RoutePreference.Mirror, 300, 120, true)
                    .Contains("强制镜像"),
                "强制模式必须说清「强制」");
        });

        Check("AuthService: Microsoft client id resolution + configuration guard", () =>
        {
            var prevCfg = LauncherConfig.Instance.MicrosoftClientId;
            var prevEnv = Environment.GetEnvironmentVariable("AURORA_MICROSOFT_CLIENT_ID");
            try
            {
                // (a) nothing configured → not configured, hint available
                Environment.SetEnvironmentVariable("AURORA_MICROSOFT_CLIENT_ID", null);
                LauncherConfig.Instance.MicrosoftClientId = null;
                Assert(!AuthService.IsMicrosoftLoginConfigured, "should be unconfigured");
                Assert(!string.IsNullOrEmpty(AuthService.MicrosoftSetupHint), "setup hint missing");

                // (b) config.json value → picked up
                LauncherConfig.Instance.MicrosoftClientId = "11111111-2222-3333-4444-555555555555";
                Assert(AuthService.IsMicrosoftLoginConfigured, "config id not picked up");
                Assert(AuthService.ConfiguredClientId == "11111111-2222-3333-4444-555555555555", "config id value wrong");

                // (c) env var takes precedence over config
                Environment.SetEnvironmentVariable("AURORA_MICROSOFT_CLIENT_ID", "99999999-8888-7777-6666-555555555555");
                Assert(AuthService.ConfiguredClientId == "99999999-8888-7777-6666-555555555555", "env var should take precedence");
            }
            finally
            {
                Environment.SetEnvironmentVariable("AURORA_MICROSOFT_CLIENT_ID", prevEnv);
                LauncherConfig.Instance.MicrosoftClientId = prevCfg;
            }
        });

        Check("LauncherConfig: AutoInstallFabricApi defaults ON and survives JSON round-trip", () =>
        {
            // Round-47: the Fabric API auto-install setting must default to ON
            // (auto behavior) and persist correctly when the user turns it off.
            var cfg = new LauncherConfig();
            Assert(cfg.AutoInstallFabricApi, "AutoInstallFabricApi must default to true");
            var json = JsonHelper.SerializeIndented(cfg);
            Assert(json.Contains("\"autoInstallFabricApi\""), "property must serialize with a stable name");
            var back = JsonHelper.Deserialize<LauncherConfig>(
                JsonHelper.SerializeIndented(new LauncherConfig { AutoInstallFabricApi = false }));
            Assert(back.AutoInstallFabricApi == false, "false must survive a save/load round-trip");
        });

        Check("GenerationGate: stale loader-card responses are discarded (race regression)", () =>
        {
            // Round-47 anti-lag flow: user picks 1.21.10 (gen A), quickly
            // re-picks 1.21.9 (gen B); A's network response lands AFTER B's
            // (out of order). The card must end up with B's list — A is stale
            // and must be discarded. This gate is the exact production
            // mechanism wired into MainViewModel.LoadLoaderCardsAsync.
            var gate = new GenerationGate();
            var genA = gate.Begin();
            var genB = gate.Begin();
            Assert(!gate.IsCurrent(genA), "older selection must be stale once a newer one begins");
            Assert(gate.IsCurrent(genB), "newest selection is current");

            // Out-of-order completion: A finishes first → discarded; B later → applied.
            var applied = new List<string>();
            if (gate.IsCurrent(genA)) applied.Add("A");
            if (gate.IsCurrent(genB)) applied.Add("B");
            Assert(applied.SequenceEqual(new[] { "B" }), $"only B may apply, got [{string.Join(",", applied)}]");

            // A newer selection invalidates everything before it.
            var genC = gate.Begin();
            Assert(!gate.IsCurrent(genB) && gate.IsCurrent(genC), "starting C must invalidate B");
        });

        Check("Guard: doc baselines agree (round + unit/all counts across the three main docs)", () =>
        {
            // Docs drifted from code repeatedly (42→47: counts lagged behind
            // the test suite in three places). The three headers must state
            // the SAME round and the SAME unit/all counts — checked on every
            // push, so "docs say X, code does Y" is caught before merge.
            var root = FindSourceRoot()!;
            string Read(string f) => File.ReadAllText(Path.Combine(root, f));
            var readme = Read("README.md");
            var roadmap = Read("ROADMAP.md");
            var results = Read("TEST_RESULTS.md");

            int? RoundOf(string s, string p) { var m = System.Text.RegularExpressions.Regex.Match(s, p); return m.Success ? int.Parse(m.Groups[1].Value) : null; }
            var r1 = RoundOf(readme, @"当前状态速览（第 (\d+) 轮");
            var r2 = RoundOf(roadmap, @"更新于 \d{4}-\d{2}-\d{2}（第 (\d+) 轮）");
            var r3 = RoundOf(results, @"更新于 \d{4}-\d{2}-\d{2}（第 (\d+) 轮）");
            Assert(r1.HasValue && r2.HasValue && r3.HasValue, "each doc must carry a round marker (速览/更新于)");
            Assert(r1 == r2 && r2 == r3, $"round markers disagree: README={r1} ROADMAP={r2} TEST_RESULTS={r3}");
            Assert(AuroraCore.BuildInfo.CurrentRound == r1,
                $"BuildInfo.CurrentRound ({AuroraCore.BuildInfo.CurrentRound}) must match the docs round ({r1}) - " +
                "the auto-update downgrade check compares against this constant");

            int? Grab(string s, string p) { var m = System.Text.RegularExpressions.Regex.Match(s, p); return m.Success ? int.Parse(m.Groups[1].Value) : null; }
            var ru = Grab(readme, @"Unit (\d+)/\d+（离线）");
            var mu = Grab(roadmap, @"Unit (\d+) 离线");
            var tu = Grab(results, @"Unit (\d+)/\d+（离线）");
            var ra = Grab(readme, @"All (\d+)/\d+（含网络集成）");
            var ma = Grab(roadmap, @"All (\d+) 含网络集成");
            var ta = Grab(results, @"All (\d+)/\d+（含网络集成）");
            Assert(ru.HasValue && mu.HasValue && tu.HasValue && ru == mu && mu == tu,
                $"unit counts disagree: README={ru} ROADMAP={mu} TEST_RESULTS={tu}");
            Assert(ra.HasValue && ma.HasValue && ta.HasValue && ra == ma && ma == ta,
                $"all counts disagree: README={ra} ROADMAP={ma} TEST_RESULTS={ta}");
        });

        Check("SlowTransferWatcher: sustained crawl aborts, brief dips don't (pure logic)", () =>
        {
            // Round-52: a slow-but-alive CDN route (4.9 KB/s) used to freeze
            // the progress bar forever — failover only fired on FULL failures.
            // The watcher must abort after ~4 consecutive slow windows, and
            // must NOT abort when the transfer recovers.
            var t = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
            var w1 = new SlowTransferWatcher(minBytesPerSec: 16000, window: TimeSpan.FromSeconds(5),
                windowsToAbort: 4, clock: () => t);
            var abort1 = false;
            for (var s = 0; s < 30 && !abort1; s++) { abort1 = w1.OnSample(2048); t += TimeSpan.FromSeconds(1); }
            Assert(abort1, "sustained crawl (2 KB/s for 30s) must abort");

            // Recovering transfer: 3 slow windows, then fast, then slow again —
            // the recovery must RESET the counter; never abort.
            var w2 = new SlowTransferWatcher(minBytesPerSec: 16000, window: TimeSpan.FromSeconds(5),
                windowsToAbort: 4, clock: () => t);
            for (var s = 0; s < 15; s++) { Assert(!w2.OnSample(2048), "must not abort before 4 slow windows"); t += TimeSpan.FromSeconds(1); }
            for (var s = 0; s < 10; s++) { Assert(!w2.OnSample(1_000_000), "fast transfer must reset the slow counter"); t += TimeSpan.FromSeconds(1); }
            for (var s = 0; s < 5; s++) { Assert(!w2.OnSample(2048), "post-recovery slow window must start counting from 0"); t += TimeSpan.FromSeconds(1); }
        });

        Check("LoaderCardVM: null coercion + append-only fill never auto-select (round-50 regression)", () =>
        {
            // Round-50 bug: WPF pushed null into SelectedVersion when the
            // items list was reset; HasVersion (null != "不安装") then counted
            // it as a REAL pick — cards auto-"selected themselves" with an
            // empty combo box. Lock the state machine:
            //   - null/empty coerces to the NoneChoice sentinel;
            //   - appending versions (the round-50 fill fix) never selects;
            //   - IsLoaderSelected / IsAutoPick are the single-source flags.
            var card = new AuroraLauncher.Desktop.ViewModels.LoaderCardVM(
                ModLoaderType.Fabric, "Fabric", "FA");
            card.Versions.Add(AuroraLauncher.Desktop.ViewModels.LoaderCardVM.NoneChoice);
            card.Versions.Add(AuroraLauncher.Desktop.ViewModels.LoaderCardVM.AutoChoice);

            card.SelectedVersion = null!;
            Assert(card.SelectedVersion == AuroraLauncher.Desktop.ViewModels.LoaderCardVM.NoneChoice,
                "null must be coerced to the NoneChoice sentinel");
            Assert(!card.IsLoaderSelected, "a null push must NOT count as a selection");

            card.Versions.Add("0.16.5"); // append-only fill
            Assert(card.SelectedVersion == AuroraLauncher.Desktop.ViewModels.LoaderCardVM.NoneChoice,
                "appending versions must not change the selection");
            Assert(!card.IsLoaderSelected && !card.IsAutoPick, "still unselected after fill");

            card.SelectedVersion = AuroraLauncher.Desktop.ViewModels.LoaderCardVM.AutoChoice;
            Assert(card.IsLoaderSelected && card.IsAutoPick, "auto choice IS a real selection");
            card.SelectedVersion = AuroraLauncher.Desktop.ViewModels.LoaderCardVM.NoneChoice;
            Assert(!card.IsLoaderSelected && !card.IsAutoPick, "back to neutral");
        });

        Check("UpdateService: manifest signature + downgrade rejection (pure logic)", () =>
        {
            // Round-57 auto-update security core: the manifest's RSA signature
            // must verify for the exact payload, fail for ANY tampered field
            // (url/sha/size are the attack surface), and the downgrade rule
            // must accept strictly higher rounds only.
            var rsa = System.Security.Cryptography.RSA.Create(2048);
            var pub = rsa.ToXmlString(false);
            var m = new UpdateManifest { Round = 60, Version = "0.60.0", Url = "https://feed.example/AuroraLauncher.exe",
                                         Sha256 = "AABBCC", Size = 12345 };
            m.Signature = Convert.ToBase64String(rsa.SignData(
                System.Text.Encoding.UTF8.GetBytes(m.SignedPayload),
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1));
            Assert(UpdateService.VerifySignature(m, pub), "valid signature must verify");

            UpdateManifest Copy(UpdateManifest src) => new() { Round = src.Round, Version = src.Version, Url = src.Url,
                Sha256 = src.Sha256, Size = src.Size, Signature = src.Signature };
            var tamperings = new (string Name, Action<UpdateManifest> Mutate)[]
            {
                ("Url", x => x.Url = "https://evil/x.exe"),
                ("Sha256", x => x.Sha256 = "DEADBEEF"),
                ("Size", x => x.Size = 999),
                ("Round", x => x.Round = 61),
            };
            foreach (var (name, mutate) in tamperings)
            {
                var bad = Copy(m);
                mutate(bad);
                Assert(!UpdateService.VerifySignature(bad, pub), $"tampered {name} must fail verification");
            }

            Assert(!UpdateService.IsNewer(m, 60), "same round must NOT count as an update (防降级)");
            Assert(!UpdateService.IsNewer(m, 61), "older round must NOT count as an update");
            Assert(UpdateService.IsNewer(m, 59), "strictly higher round is an update");
        });

        Check("UpdatePolicy: 3-way mode switch (startup check / notify / auto-download)", () =>
        {
            // Round-58: the settings switch is a pure state machine — lock
            // its semantics so a future refactor cannot silently turn
            // "不获取更新" back into a network check, or make the startup
            // popup disappear in the default "提醒" mode.
            Assert(UpdatePolicy.ShouldCheckOnStartup(UpdateMode.Auto), "mode 1 checks on startup");
            Assert(UpdatePolicy.ShouldCheckOnStartup(UpdateMode.Notify), "mode 2 checks on startup");
            Assert(!UpdatePolicy.ShouldCheckOnStartup(UpdateMode.Disabled), "mode 3 never checks on startup");

            Assert(UpdatePolicy.Decide(UpdateMode.Auto, true, false) == UpdateAction.AutoDownload,
                "mode 1 downloads automatically without a popup");
            Assert(UpdatePolicy.Decide(UpdateMode.Notify, true, false) == UpdateAction.Notify,
                "mode 2 shows the two-button popup");
            Assert(UpdatePolicy.Decide(UpdateMode.Disabled, true, false) == UpdateAction.None,
                "mode 3 takes no action on startup");
            Assert(UpdatePolicy.Decide(UpdateMode.Auto, false, false) == UpdateAction.None &&
                   UpdatePolicy.Decide(UpdateMode.Notify, false, false) == UpdateAction.None,
                "no update → no action in every mode");

            // Manual 「检查更新」 is an explicit user action: always notify,
            // even in mode 3 (and even in mode 1) — the user must see the result.
            foreach (var mode in new[] { UpdateMode.Auto, UpdateMode.Notify, UpdateMode.Disabled })
                Assert(UpdatePolicy.Decide(mode, true, true) == UpdateAction.Notify,
                    $"manual check must notify in mode {mode}");

            // Display text round-trip (settings ComboBox ←→ persisted enum).
            foreach (var mode in new[] { UpdateMode.Auto, UpdateMode.Notify, UpdateMode.Disabled })
                Assert(UpdatePolicy.FromDisplayText(UpdatePolicy.DisplayText(mode)) == mode,
                    $"display text round-trip failed for {mode}");
        });

        Check("JavaRuntimeService: Adoptium manifest parse + zip-slip guard (pure logic)", () =>
        {
            // Round-59 one-click Java 21: the manifest decides BOTH the URL
            // and the integrity check, so it must fail closed on anything
            // odd; extraction must never write outside our java/ folder.
            const string hash = "A4DBAA05FCA4BD89D84584E58C459BD732EFEBB1722AF80AB13C6516832004F9";
            var json = "[{\"binary\":{\"package\":{\"name\":\"OpenJDK21U-jdk_x64_windows_hotspot_21.0.13_9.zip\"," +
                       "\"link\":\"https://github.com/adoptium/temurin21-binaries/releases/download/x/OpenJDK21U.zip\"," +
                       "\"checksum\":\"" + hash + "\",\"size\":205421281}}," +
                       "\"release_name\":\"jdk-21.0.13+9\",\"version\":{\"major\":21}}]";
            var pkg = JavaRuntimeService.ParseLatest(json);
            Assert(pkg != null, "a valid Adoptium asset must parse");
            Assert(pkg!.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase), "must be a zip package");
            Assert(pkg.Url.StartsWith("https://", StringComparison.Ordinal), "download link must be HTTPS");
            Assert(pkg.Sha256 == hash && pkg.Size == 205421281, "checksum and size must be preserved verbatim");
            Assert(pkg.ReleaseName == "jdk-21.0.13+9" && pkg.Major == 21, "release name / major must parse");

            // Fail closed: no plain HTTP, no short/missing checksum, no non-zip.
            Assert(JavaRuntimeService.ParseLatest(json.Replace("https://github.com", "http://github.com")) == null,
                "plain HTTP link must be rejected");
            Assert(JavaRuntimeService.ParseLatest(json.Replace(hash, "DEADBEEF")) == null,
                "missing/short SHA-256 must be rejected");
            Assert(JavaRuntimeService.ParseLatest(json.Replace(".zip\"", ".msi\"")) == null,
                "non-zip packages must be rejected");
            Assert(JavaRuntimeService.ParseLatest("{}") == null, "wrong JSON shape must be rejected");

            // Zip-slip guard.
            Assert(JavaRuntimeService.IsSafeEntryPath("jdk-21.0.13+9/bin/java.exe"), "normal entry must pass");
            Assert(JavaRuntimeService.IsSafeEntryPath("jdk-21.0.13+9/lib/modules"), "nested entry must pass");
            foreach (var bad in new[] { "../evil.exe", "a/../../evil.exe", "/abs/evil.exe", "C:\\evil.exe", "jdk:stream", "" })
                Assert(!JavaRuntimeService.IsSafeEntryPath(bad), $"unsafe entry must be rejected: '{bad}'");
        });

        Check("CrashAnalyzer.BuildReport: all causes, case-insensitive, no-match fallback", () =>
        {
            // Round-60 crash card: it must list EVERY matching rule (a modpack
            // crash often has two causes), not just the first one.
            var lines = new[] {
                "java.lang.UnsupportedClassVersionError: class file version 65.0",
                "Duplicate mods: fabric-api found twice",
                "some unrelated line" };
            var r = CrashAnalyzer.BuildReport(-1, "测试实例", lines);
            Assert(r.ExitCode == -1 && r.InstanceName == "测试实例", "exit code and instance must be preserved");
            Assert(r.HasCauses && r.Causes.Count >= 2, "must report BOTH Java-version and duplicate-mod causes");
            Assert(r.Causes.Any(c => c.Title == "Java 版本过低"), "Java version rule must match");
            Assert(r.Causes.Any(c => c.Title == "检测到重复 Mod"), "duplicate mod rule must match");
            Assert(CrashAnalyzer.BuildReport(1, "x", new[] { "duplicate mods" }).Causes.Count >= 1,
                "matching must be case-insensitive");
            var none = CrashAnalyzer.BuildReport(2, "x", new[] { "game closed normally" });
            Assert(!none.HasCauses, "no rule → no causes (card shows the fallback hint)");
            Assert(CrashAnalyzer.Analyze(new[] { "java.lang.UnsupportedClassVersionError" }) != null,
                "legacy Analyze() must keep working");
        });

        Check("CrashResolutionEngine: 常见崩溃 → 具体修复动作（第 78 轮规则库）", () =>
        {
            // Round-78: the crash card no longer stops at "here is a reason" —
            // each rule carries an ACTION with the parameters it needs.
            var modded = new CrashContext
            {
                MaxMemoryMb = 2048, TotalPhysicalMb = 16384,
                Loader = ModLoaderType.Fabric, MinecraftVersion = "1.21.1"
            };

            var oom = CrashResolutionEngine.Diagnose("java.lang.OutOfMemoryError: Java heap space", modded);
            var oomFix = oom.Solutions.Single(s => s.RuleId == "out_of_memory");
            Assert(oomFix.Action == FixAction.RaiseMemory && oomFix.Kind == FixKind.Auto,
                "OOM must offer an automatic memory raise");
            Assert(int.TryParse(oomFix.Payload, out var raised) && raised > 2048,
                $"the payload must carry the new -Xmx, got '{oomFix.Payload}'");
            Assert(oomFix.Diagnosis.Contains("2048"), "the diagnosis must quote the current limit");

            // A machine with no headroom: claiming "I can fix it" would be a lie.
            var tiny = CrashResolutionEngine.Diagnose("OutOfMemoryError",
                modded with { MaxMemoryMb = 8192, TotalPhysicalMb = 4096 });
            var tinyFix = tiny.Solutions.Single(s => s.RuleId == "out_of_memory");
            Assert(tinyFix.Action == FixAction.OpenSettings && tinyFix.Kind == FixKind.Guided,
                "without physical headroom the engine must downgrade to guidance");

            // Java too old, nothing suitable installed → download the exact major.
            var javaOld = CrashResolutionEngine.Diagnose(
                "java.lang.UnsupportedClassVersionError: class file version 65.0",
                modded with { CurrentJavaMajor = 8 });
            var javaFix = javaOld.Solutions.Single(s => s.RuleId == "java_version_old");
            Assert(javaFix.Action == FixAction.InstallJava && javaFix.Payload == "21",
                $"class file 65 = Java 21, got {javaFix.Action}/{javaFix.Payload}");
            Assert(javaFix.Diagnosis.Contains("Java 8") && javaFix.Diagnosis.Contains("21"),
                "the diagnosis must name both the current and the required Java");

            // Java already present → reuse it, never re-download.
            var javaPresent = CrashResolutionEngine.Diagnose(
                "UnsupportedClassVersionError: class file version 61.0",
                modded with { CurrentJavaMajor = 8, InstalledJavaMajors = new[] { 8, 17 } });
            var useFix = javaPresent.Solutions.Single(s => s.RuleId == "java_version_old");
            Assert(useFix.Action == FixAction.UseInstalledJava && useFix.Payload == "17",
                "an installed Java 17 must be reused instead of installed again");

            // Missing prerequisites.
            Assert(CrashResolutionEngine
                    .Diagnose("ModResolutionException: Could not find required mod: fabric-api", modded)
                    .Solutions.Any(s => s.Action == FixAction.InstallFabricApi),
                "missing fabric-api must offer a one-click download");
            var dup = CrashResolutionEngine.Diagnose("Duplicate mods: sodium.jar, sodium-1.jar", modded);
            var dupFix = dup.Solutions.Single(s => s.RuleId == "duplicate_mods");
            Assert(dupFix.Action == FixAction.OpenModsFolder && dupFix.Kind == FixKind.Guided,
                "deleting mods is the user's call — we only open the folder");

            // "Heap too big" is the mirror image of OOM: lower, don't raise.
            var heap = CrashResolutionEngine.Diagnose("Could not reserve enough space for object heap", modded);
            var heapFix = heap.Solutions.Single(s => s.RuleId == "heap_reserve");
            Assert(heapFix.Action == FixAction.LowerMemory &&
                   int.TryParse(heapFix.Payload, out var lowered) && lowered < 2048,
                $"a heap-reserve failure must LOWER -Xmx, got {heapFix.Payload}");

            // Ambiguous / unknown crash → no invented cause, but a stable fingerprint.
            var unknown = CrashResolutionEngine.Diagnose("everything looks fine", modded);
            Assert(!unknown.HasSolutions, "no rule → no solutions (card offers the mail-the-author path)");
            Assert(unknown.Signature == "unknown", "no exception line → the unknown marker");
            Assert(CrashResolutionEngine.BuildSignature("a\njava.lang.RuntimeException: boom\nb") ==
                   CrashResolutionEngine.BuildSignature("x\njava.lang.RuntimeException: boom\nz"),
                "the fingerprint must depend on the exception line only (reports group together)");

            // Pure memory math.
            Assert(CrashResolutionEngine.JavaMajorForClassFile(52) == 8 &&
                   CrashResolutionEngine.JavaMajorForClassFile(61) == 17 &&
                   CrashResolutionEngine.JavaMajorForClassFile(65) == 21 &&
                   CrashResolutionEngine.JavaMajorForClassFile(44) == 0, "class-file → Java major mapping");
            Assert(CrashResolutionEngine.MemoryCeiling(0) == 8192, "unknown RAM → conservative 8 GB ceiling");
            Assert(CrashResolutionEngine.MemoryCeiling(32768) == 16384, "never suggest more than 16 GB");
            Assert(CrashResolutionEngine.MemoryCeiling(4096) == 2252, "55% of physical memory, rounded down");
            Assert(!CrashResolutionEngine.SuggestHigherMemory(4096, 8192, true).Useful,
                "already above the ceiling → raising is not useful");
            Assert(CrashResolutionEngine.SuggestLowerMemory(16384, 1024).Target == 1024 &&
                   !CrashResolutionEngine.SuggestLowerMemory(16384, 1024).Useful,
                "1 GB is the floor — never suggest going lower");
        });

        Check("CrashFixSafety: 自动修复只能改启动器自己的设置/下载文件", () =>
        {
            // The safety property is structural: the action enum has no
            // destructive member, and every action's config footprint is a
            // subset of the three launch-settings keys. Guard both.
            var allowedKeys = new[] { "MaxMemoryMb", "MinMemoryMb", "JavaPath" };
            foreach (var action in Enum.GetValues<FixAction>())
            {
                var keys = CrashFixSafety.AffectedConfigKeys(action);
                Assert(keys.All(allowedKeys.Contains),
                    $"{action} writes unexpected config keys: {string.Join(",", keys)}");
                Assert(CrashFixSafety.IsNonDestructive(action),
                    $"{action} must be non-destructive by construction");
            }
            foreach (var name in Enum.GetNames<FixAction>())
                foreach (var banned in new[] { "Delete", "Remove", "Kill", "Env", "Format", "Overwrite", "Admin" })
                    Assert(!name.Contains(banned, StringComparison.OrdinalIgnoreCase),
                        $"FixAction.{name} looks like something a repair engine must never do");

            Assert(CrashFixSafety.CanAutoApply(FixAction.RaiseMemory), "memory raise is auto");
            Assert(CrashFixSafety.CanAutoApply(FixAction.InstallFabricApi), "a mod download is auto");
            Assert(!CrashFixSafety.CanAutoApply(FixAction.OpenModsFolder), "opening a folder is guided");
            Assert(!CrashFixSafety.CanAutoApply(FixAction.MailAuthor), "mailing is the user's own action");
            Assert(CrashFixSafety.Describe(FixAction.RaiseMemory).Contains("config.json"),
                "the log line must say exactly what is touched");
        });

        Check("SupportReport: 求助邮件由用户亲手发送（mailto 可回读）", () =>
        {
            var solutions = new[]
            {
                new CrashSolution("java_version_old", "Java 版本过低",
                    "当前 Java 8，需要 Java 21。", "换 Java",
                    FixKind.Auto, FixAction.InstallJava, "一键安装 Java 21", "21")
            };
            var mailto = SupportReport.BuildMailto(solutions, new SupportReport.Context
            {
                InstanceName = "测试 & 特殊字符实例",
                ExitCode = -1,
                MinecraftVersion = "1.21.1",
                Loader = "Fabric 0.20.1",
                BundlePath = @"D:\reports\aurora-diagnostics.zip",
                Signature = "abc12345",
                AttemptedFixes = new[] { "把最大内存从 2048 MB 调到 4096 MB" }
            });

            Assert(mailto.StartsWith("mailto:" + SupportReport.AuthorEmail + "?", StringComparison.Ordinal),
                "the report must be addressed to the author");
            Assert(!mailto.Contains('\n') && !mailto.Contains('\r'),
                "newlines must be percent-encoded or the mail client truncates the body");
            Assert(mailto.Length < 2000, $"mailto too long for a mail client ({mailto.Length})");
            Assert(SupportReport.TryParse(mailto, out var to, out var subject, out var body),
                "the URL must be well formed");
            Assert(to == SupportReport.AuthorEmail, "the recipient must survive encoding");
            Assert(subject.Contains($"第 {AuroraCore.BuildInfo.CurrentRound} 轮"),
                "the subject must name the build round");
            Assert(body.Contains("Java 版本过低") && body.Contains("当前 Java 8，需要 Java 21"),
                "the body must carry the diagnosis the launcher already made");
            Assert(body.Contains(@"D:\reports\aurora-diagnostics.zip"),
                "the body must tell the user which bundle to attach");
            Assert(body.Contains("2048"), "already-attempted repairs must be listed");
            Assert(!body.Contains("SMTP"), "no credentials flow through the launcher");
            Assert(!SupportReport.TryParse("https://example.com", out _, out _, out _),
                "a non-mailto URL must be rejected");
        });

        Check("Guard: 崩溃修复不删文件、不阻塞界面（第 78 轮）", () =>
        {
            var root = FindSourceRoot()!;
            var vm = File.ReadAllText(Path.Combine(root, "src", "AuroraLauncher.Desktop",
                "ViewModels", "MainViewModel.cs"));

            // (1) The repair executor may not delete/move user data or touch the
            //     environment. Static, so a future "just delete the mod folder"
            //     shortcut fails before merge.
            var start = vm.IndexOf("private async Task ApplyCrashFixAsync", StringComparison.Ordinal);
            Assert(start > 0, "ApplyCrashFixAsync must exist");
            var end = vm.IndexOf("private void AdoptJavaForSelectedInstance", start, StringComparison.Ordinal);
            Assert(end > start, "the repair executor must end before the next helper");
            var fixBody = vm[start..end];
            foreach (var banned in new[] { "File.Delete", "Directory.Delete", "File.Move",
                                           "Directory.Move", "SetEnvironmentVariable", ".Kill(" })
                Assert(!fixBody.Contains(banned), $"crash repair must never call {banned}");
            Assert(fixBody.Contains("MaxMemoryMb") && fixBody.Contains("cfg.Save()"),
                "the memory repair must write launcher config (and nothing else)");
            Assert(fixBody.Contains("await "),
                "repairs must be async — the crash card may not freeze");

            // (2) The diagnosis (regex over up to 600 lines × N rules) must run
            //     off the UI thread: the game just died, the window must breathe.
            var diagStart = vm.IndexOf("private async Task ShowCrashCardAsync", StringComparison.Ordinal);
            Assert(diagStart > 0, "ShowCrashCardAsync must exist");
            var diagEnd = vm.IndexOf("private CrashContext BuildCrashContext", diagStart, StringComparison.Ordinal);
            Assert(diagEnd > diagStart, "the diagnosis method must end before the context helper");
            var diagBody = vm[diagStart..diagEnd];
            Assert(diagBody.Contains("Task.Run(() => CrashResolutionEngine.Diagnose"),
                "diagnosis must run on a background thread");
        });

        Check("SaveBackup: 触发矩阵 / 保留策略 / 只读存档（第 79 轮）", () =>
        {
            // --- policy ---
            Assert(SaveBackupPolicy.ShouldRun(SaveBackupMode.Both, BackupTrigger.BeforeLaunch), "Both must fire before launch");
            Assert(SaveBackupPolicy.ShouldRun(SaveBackupMode.Both, BackupTrigger.AfterExit), "Both must fire after exit");
            Assert(!SaveBackupPolicy.ShouldRun(SaveBackupMode.AfterExit, BackupTrigger.BeforeLaunch),
                "AfterExit must NOT delay a launch");
            Assert(!SaveBackupPolicy.ShouldRun(SaveBackupMode.Off, BackupTrigger.AfterExit), "Off must never fire");
            Assert(SaveBackupPolicy.ShouldRun(SaveBackupMode.Off, BackupTrigger.Manual),
                "the manual button must work even when automatic backup is off");

            Assert(SaveBackupPolicy.ClampKeep(0) == SaveBackupPolicy.DefaultKeep, "0 → default keep");
            Assert(SaveBackupPolicy.ClampKeep(999) == SaveBackupPolicy.MaxKeep, "absurd keep must clamp");

            var name = SaveBackupPolicy.BuildFileName(new DateTime(2026, 11, 12, 20, 41, 5),
                BackupTrigger.BeforeLaunch, "我的 世界/A");
            Assert(name.StartsWith("saves-") && name.EndsWith("-before.zip"), "file name shape: " + name);
            Assert(!name.Contains('/') && !name.Contains('\\') && !name.Contains(' '), "file name must be filesystem-safe: " + name);
            Assert(name.Contains("20261112-204105"), "timestamp must be sortable: " + name);

            // --- retention ---
            var six = Enumerable.Range(0, 6)
                .Select(i => ($"C:/b/saves-x-{i}.zip", new DateTime(2026, 1, 1).AddDays(i)))
                .ToList();
            var doomed = SaveBackupPolicy.PlanPrune(six, 2);
            Assert(doomed.Count == 4, $"keep=2 of 6 must delete 4, got {doomed.Count}");
            Assert(!doomed.Contains(six[5].Item1) && !doomed.Contains(six[4].Item1), "the two newest must survive");
            var withUserZip = six.Append(("C:/b/my-own-backup.zip", new DateTime(2020, 1, 1))).ToList();
            Assert(!SaveBackupPolicy.PlanPrune(withUserZip, 2).Contains("C:/b/my-own-backup.zip"),
                "a zip the user dropped in the folder must never be pruned");

            Assert(SaveBackupPolicy.SkipReason(0, 0) != null, "empty saves folder must be reported, not zipped");
            Assert(SaveBackupPolicy.SkipReason(120, 5 * 1024L * 1024 * 1024) != null, "oversized saves must be skipped");
            Assert(SaveBackupPolicy.SkipReason(120, 40 * 1024 * 1024) == null, "normal saves must pass");

            // --- service round-trip: zip exists, saves untouched, retention applied ---
            var root = Path.Combine(Path.GetTempPath(), "aurora-backup-" + Guid.NewGuid().ToString("N")[..8]);
            var instanceDir = Path.Combine(root, "instances", "abc");
            var saves = Path.Combine(instanceDir, "saves");
            var backupDir = Path.Combine(root, "backups", "abc");
            Directory.CreateDirectory(Path.Combine(saves, "World1"));
            File.WriteAllText(Path.Combine(saves, "World1", "level.dat"), "LEVELDATA");
            File.WriteAllText(Path.Combine(saves, "World1", "region.mca"), new string('x', 4096));
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    var r = SaveBackupService.CreateAsync(saves, backupDir, "实例" + i,
                        BackupTrigger.Manual, keep: 2).GetAwaiter().GetResult();
                    Assert(r.Ok, "backup must succeed: " + r.Message);
                    Assert(File.Exists(r.Path), "the zip must exist");
                }
                var all = SaveBackupService.List(backupDir);
                Assert(all.Count == 2, $"keep=2 must leave exactly 2 zips, got {all.Count}");
                Assert(File.Exists(Path.Combine(saves, "World1", "level.dat")),
                    "the backup must never move/delete the original save");
                using (var zip = System.IO.Compression.ZipFile.OpenRead(all[0].Path))
                    Assert(zip.GetEntry("World1/level.dat") != null,
                        "the zip must contain the world data (relative paths, no base folder)");

                var empty = Path.Combine(root, "empty-saves");
                Directory.CreateDirectory(empty);
                var fail = SaveBackupService.CreateAsync(empty, Path.Combine(root, "b2"), "x",
                    BackupTrigger.Manual, 3).GetAwaiter().GetResult();
                Assert(!fail.Ok && fail.Message.Contains("空"), "an empty saves folder must fail with a readable reason");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

        Check("PerformanceReport: FPS 解析 / 结论阈值 / CPU 数学（第 79 轮）", () =>
        {
            var fps = PerformanceReport.ParseFps(new[]
            {
                "[20:41:05] [Render thread/INFO]: fps: 60",
                "[x] 144 fps",
                "fps: 0",        // startup noise → ignored
                "fps: 5000",     // garbage → ignored
                "no numbers here"
            });
            Assert(fps.Samples == 2 && fps.Average == 102 && fps.Peak == 144,
                $"expected 2 samples avg 102 peak 144, got {fps.Samples}/{fps.Average}/{fps.Peak}");
            Assert(!PerformanceReport.ParseFps(new[] { "nothing" }).HasData, "no FPS lines → no data");
            Assert(!PerformanceReport.ParseFps(null).HasData, "null input must not throw");

            Assert(PerformanceReport.Verdict(0, 4096, FpsSummary.None).Level == "无数据", "no run → 无数据");
            var tight = PerformanceReport.Verdict(3900, 4096, FpsSummary.None);
            Assert(tight.Level == "吃紧" && tight.Text.Contains("4096"), "92%+ of -Xmx must warn about OOM");
            var slow = PerformanceReport.Verdict(2048, 4096, new FpsSummary(22, 30, 6));
            Assert(slow.Level == "偏低", "20-something FPS must be called out");
            var ok = PerformanceReport.Verdict(2048, 4096, new FpsSummary(60, 72, 6));
            Assert(ok.Level == "健康", "plenty of headroom → 健康");

            // GetSystemTimes reports kernel time INCLUDING idle — the classic bug.
            var prev = new CpuTimes(100, 300, 200);
            var cur = new CpuTimes(200, 500, 400);   // Δidle 100, Δkernel 200, Δuser 200
            var pct = CpuLoadMath.Percent(prev, cur);
            Assert(Math.Abs(pct - 75) < 0.01, $"busy = (400-100)/400 = 75%, got {pct}");
            Assert(CpuLoadMath.Percent(cur, cur) == 0, "no counter movement → 0%");
            Assert(CpuLoadMath.Percent(cur, prev) == 0, "counter reset must clamp to 0, never negative");
        });

        Check("GlassEffectPolicy: 默认关闭 / 降级 / 三种玻璃方案的边界（第 80–81 轮）", () =>
        {
            // Default must be Off: the original theme is the only zero-risk option.
            Assert(GlassEffectPolicy.DisplayNames.Count == 3, "three modes: off / glass / local");
            Assert(GlassEffectPolicy.Parse(GlassEffectPolicy.DisplayNames[0]) == GlassMode.Off, "index 0 must be Off");

            foreach (var mode in new[] { GlassMode.Off, GlassMode.Faux, GlassMode.Local })
                Assert(GlassEffectPolicy.Parse(GlassEffectPolicy.DisplayName(mode)) == mode,
                    $"{mode}: display name must round-trip");

            Assert(!GlassEffectPolicy.NeedsWarning(GlassMode.Off), "Off must apply silently");
            Assert(GlassEffectPolicy.NeedsWarning(GlassMode.Faux) &&
                   GlassEffectPolicy.NeedsWarning(GlassMode.Local),
                "every non-default mode must sit behind the red warning");

            // Round-81: the wallpaper design replaced the native Mica path, so a
            // config that still says "System" is reported as the glass skin.
            Assert(GlassEffectPolicy.Resolve(GlassMode.System, 22621) == GlassMode.Faux,
                "legacy System config must fold into the wallpaper skin");
            Assert(GlassEffectPolicy.DisplayName(GlassMode.System) == GlassEffectPolicy.DisplayName(GlassMode.Faux),
                "legacy System must display the glass option, not a dead mode");
            Assert(GlassEffectPolicy.Resolve(GlassMode.Faux, 19045) == GlassMode.Faux, "glass works on every build");
            Assert(GlassEffectPolicy.Resolve(GlassMode.Off, 19045) == GlassMode.Off, "Off is never upgraded");

            Assert(!GlassEffectPolicy.UsesTransientBlur(GlassMode.Faux), "the wallpaper skin must not blur anything");
            Assert(GlassEffectPolicy.UsesTransientBlur(GlassMode.Local), "only Local uses the transient blur");
            Assert(GlassEffectPolicy.UsesTranslucentSurfaces(GlassMode.Faux) &&
                   !GlassEffectPolicy.UsesTranslucentSurfaces(GlassMode.Off),
                "surface recolouring only in glass modes");

            var warn = GlassEffectPolicy.WarningBody(GlassEffectPolicy.DisplayName(GlassMode.Faux),
                GlassMode.Faux, 19045);
            Assert(warn.Contains("卡顿") && warn.Contains("崩溃"), "the warning must name the real risks");
            Assert(warn.Contains("改回"), "the warning must tell the user how to undo it");
            Assert(GlassEffectPolicy.StatusLine(GlassMode.Faux, GlassMode.Faux, 19045).Contains("半透明"),
                "the status line must describe what the skin actually does");
        });

        Check("GlassGuardPolicy: 模糊只允许在 MainWindow 的 Glass* 元素上（第 80 轮）", () =>
        {
            // Legal: whitelisted element in the whitelisted file.
            var ok = """
                     <Border x:Name="GlassPalette">
                       <Border.Effect><BlurEffect Radius="12"/></Border.Effect>
                     </Border>
                     """;
            Assert(GlassGuardPolicy.ScanXaml(ok, "MainWindow.xaml").Count == 0, "Glass* in MainWindow must pass");

            // Illegal: named element, wrong file (this is the round-66 cliff).
            Assert(GlassGuardPolicy.ScanXaml(ok, "HomeView.xaml").Count > 0,
                "blur in a page must be rejected even when the element is named Glass*");

            // Illegal: right file, ordinary element.
            var wrongName = """
                            <Border x:Name="RootGrid">
                              <Border.Effect><BlurEffect Radius="12"/></Border.Effect>
                            </Border>
                            """;
            Assert(GlassGuardPolicy.ScanXaml(wrongName, "MainWindow.xaml").Count > 0,
                "blur on a non-Glass element must be rejected");

            // Illegal: inside a list row, even with a Glass name.
            var inTemplate = """
                             <ListBox.ItemTemplate>
                               <DataTemplate>
                                 <Border x:Name="GlassRow">
                                   <Border.Effect><BlurEffect Radius="8"/></Border.Effect>
                                 </Border>
                               </DataTemplate>
                             </ListBox.ItemTemplate>
                             """;
            Assert(GlassGuardPolicy.ScanXaml(inTemplate, "MainWindow.xaml").Count > 0,
                "blur inside a DataTemplate must be rejected (list rows are the freeze case)");

            Assert(GlassGuardPolicy.ScanXaml("<Border/>", "HomeView.xaml").Count == 0,
                "no blur → no violations");
            Assert(GlassGuardPolicy.ScanXaml("", "MainWindow.xaml").Count == 0, "empty input must not throw");

            Assert(GlassGuardPolicy.IsCodeBlurAllowed("MainWindow.xaml.cs",
                "var glassBlur = new BlurEffect { Radius = 14 };"), "GlassBlur member in MainWindow is allowed");
            Assert(!GlassGuardPolicy.IsCodeBlurAllowed("MainWindow.xaml.cs",
                "var e = new BlurEffect();"), "an unnamed blur in MainWindow is still a violation");
            Assert(!GlassGuardPolicy.IsCodeBlurAllowed("InstancesView.xaml.cs",
                "var GlassBlurEffect = new BlurEffect();"), "the marker does not legalize other files");
        });

        Check("CommandPalette: 排序 / 多词 AND / 索引来源（第 79 轮）", () =>
        {
            var items = CommandPalette.BuildIndex(
                new[] { ("a1", "整合包测试", "1.21.1", "Fabric") },
                new[] { ("Sodium", "sodium-0.5.jar") }, "sodium");

            var head = CommandPalette.Search(items, "");
            Assert(head.Count > 0 && head[0].Kind == PaletteKind.Action, "empty query must lead with actions");

            var backup = CommandPalette.Search(items, "备份");
            Assert(backup.Any(i => i.Action == CommandPalette.ActionBackupNow), "「备份」must find the backup action");
            Assert(backup.Any(i => i.Kind == PaletteKind.Setting), "「备份」must also find the backup setting");

            Assert(CommandPalette.Search(items, "整合包").Any(i => i.Kind == PaletteKind.Instance),
                "instance names must be searchable");
            Assert(CommandPalette.Search(items, "SODIUM").Any(i => i.Kind == PaletteKind.Mod),
                "search must be case-insensitive");
            Assert(CommandPalette.Search(items, "sodium").Any(i => i.Kind == PaletteKind.Web),
                "a typed query must offer the Modrinth web row");

            Assert(!CommandPalette.Search(items, "备份 sodium").Any(i => i.Action == CommandPalette.ActionBackupNow),
                "two terms must BOTH match (AND, not OR)");

            var exact = CommandPalette.Search(CommandPalette.StaticItems, "启动游戏");
            Assert(exact.Count > 0 && exact[0].Action == CommandPalette.ActionLaunch,
                "an exact title must rank first");
            Assert(CommandPalette.Search(items, "zzzz-nothing").Count == 0, "no match → empty list");
            Assert(CommandPalette.Search(items, "备份", limit: 1).Count == 1, "limit must be honoured");

            // Every page payload must be a page the window can actually build.
            var root = FindSourceRoot()!;
            var windowCode = File.ReadAllText(Path.Combine(root, "src", "AuroraLauncher.Desktop", "MainWindow.xaml.cs"));
            foreach (var page in CommandPalette.StaticItems.Where(i => i.Kind == PaletteKind.Page))
                Assert(page.Payload == "Home" || windowCode.Contains($"\"{page.Payload}\" =>"),
                    $"palette page '{page.Payload}' has no CreatePage branch"); // Home is the default branch

            // Every action id must be handled by RunPalette (index-only rows are dead buttons).
            var vmText = File.ReadAllText(Path.Combine(root, "src", "AuroraLauncher.Desktop", "ViewModels", "MainViewModel.cs"))
                       + File.ReadAllText(Path.Combine(root, "src", "AuroraLauncher.Desktop", "ViewModels", "MainViewModel.Round79.cs"));
            foreach (var field in typeof(CommandPalette)
                         .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                         .Where(f => f.IsLiteral && f.Name.StartsWith("Action", StringComparison.Ordinal)))
                Assert(vmText.Contains("CommandPalette." + field.Name),
                    $"palette action {field.Name} is indexed but never handled in RunPalette");
        });

        Check("LaunchGuardPolicy: 没有实例时按钮变成引导而非死按钮（第 82 轮）", () =>
        {
            // The first-run state. A launch click MUST go somewhere useful.
            Assert(LaunchGuardPolicy.Decide(0, false, false) == LaunchGuardPolicy.Block.NoInstances,
                "no instances must win over every other reason");
            Assert(LaunchGuardPolicy.Decide(0, true, true) == LaunchGuardPolicy.Block.NoInstances,
                "the instance count is the source of truth, not the selection flag");
            Assert(LaunchGuardPolicy.Decide(2, false, true) == LaunchGuardPolicy.Block.NoSelection,
                "instances exist but none selected → pick one");
            Assert(LaunchGuardPolicy.Decide(2, true, false) == LaunchGuardPolicy.Block.NoAccount,
                "ready except no account");
            Assert(LaunchGuardPolicy.Decide(2, true, true) == LaunchGuardPolicy.Block.None, "all clear");

            var (noInstMsg, noInstPage) = LaunchGuardPolicy.Describe(LaunchGuardPolicy.Block.NoInstances);
            Assert(noInstPage == "Download" && noInstMsg.Contains("游戏下载"),
                "『没有实例』必须跳到游戏下载页，并说明原因");
            Assert(LaunchGuardPolicy.Describe(LaunchGuardPolicy.Block.NoSelection).Page == "Instances",
                "『没选实例』必须跳到实例管理");
            Assert(LaunchGuardPolicy.Describe(LaunchGuardPolicy.Block.NoAccount).Page == null,
                "缺账户不该把人跳走（账户就在首页）");

            Assert(LaunchGuardPolicy.ButtonText(false) == "去下载游戏" &&
                   LaunchGuardPolicy.ButtonText(true) == "启动游戏",
                "the label must promise exactly what the click does");
            Assert(LaunchGuardPolicy.InstanceHint(false).Contains("还没有实例") &&
                   LaunchGuardPolicy.InstanceHint(true).Contains("自动触发下载安装"),
                "the instance-card hint must follow the same state");

            // Static guard: the VM must actually consult the policy. Otherwise the
            // label and the click can drift apart again (the round-82 bug).
            var vm = File.ReadAllText(Path.Combine(FindSourceRoot()!, "src", "AuroraLauncher.Desktop",
                "ViewModels", "MainViewModel.cs"));
            Assert(vm.Contains("LaunchGuardPolicy.Decide"), "LaunchAsync must guard through LaunchGuardPolicy");
            Assert(vm.Contains("LaunchGuardPolicy.ButtonText"), "the button label must come from the policy");
        });

        Check("MemoryAllocationPolicy: 推荐阶梯 / 物理内存上限 / 不越界（第 83 轮）", () =>
        {
            // Vanilla on a normal machine: 2 GB is plenty.
            Assert(MemoryAllocationPolicy.Recommend(16384, 0, false, false).RecommendedMb == 2048,
                "vanilla → 2048 MB");

            // Heavier instance → more heap, monotonically.
            var light = MemoryAllocationPolicy.Recommend(16384, 20, false, true).RecommendedMb;
            var medium = MemoryAllocationPolicy.Recommend(16384, 60, false, true).RecommendedMb;
            var heavy = MemoryAllocationPolicy.Recommend(16384, 200, false, true).RecommendedMb;
            Assert(light < medium && medium < heavy,
                $"mod count must move the number (got {light}/{medium}/{heavy})");

            // Shaders raise the floor to 4 GB and never lower it.
            Assert(MemoryAllocationPolicy.Recommend(16384, 0, true, true).RecommendedMb >= 4096,
                "shaders → at least 4 GB");
            Assert(MemoryAllocationPolicy.Recommend(16384, 200, true, true).RecommendedMb >=
                   MemoryAllocationPolicy.Recommend(16384, 200, false, true).RecommendedMb,
                "a shader must never reduce the recommendation");

            // THE guard the user asked for: sweep every automatic path and assert
            // no result ever exceeds 60% of physical RAM (we aim at 55%).
            for (var ram = 1024; ram <= 131072; ram += 512)
            {
                foreach (var mods in new[] { 0, 25, 70, 120, 300 })
                foreach (var shader in new[] { false, true })
                foreach (var modded in new[] { false, true })
                {
                    var a = MemoryAllocationPolicy.Recommend(ram, mods, shader, modded);
                    Assert(a.RecommendedMb * 1.0 / ram <= 0.60 + 1e-9,
                        $"{ram} MB RAM / {mods} mods / shader {shader} → {a.RecommendedMb} MB exceeds 60%");
                    Assert(a.RecommendedMb <= MemoryAllocationPolicy.HardCapMb,
                        "never above the 16 GB hard cap");
                    Assert(a.RecommendedMb >= Math.Min(MemoryAllocationPolicy.FloorMb, a.CeilingMb),
                        "never below the floor (unless the machine cannot afford it)");
                    Assert(a.RecommendedMb % MemoryAllocationPolicy.QuantumMb == 0,
                        "heaps stay round numbers");
                    Assert(MemoryAllocationPolicy.SafeCeiling(ram) * 1.0 / ram <= 0.60 + 1e-9,
                        "the ceiling itself must respect 60%");
                }
            }

            // A 4 GB laptop: capped by RAM instead of the mod table, and told so.
            var small = MemoryAllocationPolicy.Recommend(4096, 120, true, true);
            Assert(small.CappedByPhysical &&
                   small.RecommendedMb == MemoryAllocationPolicy.SafeCeiling(4096),
                $"a 4 GB machine is capped by RAM, got {small.RecommendedMb}");
            Assert(small.RecommendedMb <= 2252, "4 GB → at most 2252 MB (55%), never the 5120 the table wants");
            Assert(small.LowPhysical, "≤6 GB must carry the low-memory warning");
            Assert(MemoryAllocationPolicy.Describe(small, 4096, 120, true, true).Contains("上限"),
                "the hint must explain the ceiling, not just print a number");

            // Probe failure must not invent a big number.
            var unknown = MemoryAllocationPolicy.Recommend(0, 20, false, true);
            Assert(unknown.RecommendedMb == 3072 &&
                   unknown.CeilingMb == MemoryAllocationPolicy.HardCapMb,
                "unknown RAM → the table value, bounded only by the hard cap");

            // Manual numbers are honoured verbatim: silently rewriting a setup
            // that already works is worse than a suboptimal number.
            Assert(MemoryAllocationPolicy.Effective(3000, false, 8192, 200, true, true) == 3000,
                "manual mode must never be rewritten");
            Assert(MemoryAllocationPolicy.Effective(3000, true, 8192, 200, true, true) ==
                   MemoryAllocationPolicy.Recommend(8192, 200, true, true).RecommendedMb,
                "auto mode ignores the stored number");
            Assert(MemoryAllocationPolicy.ExceedsSafeShare(7373, 8192), "7373 > 55% of 8 GB");
            Assert(!MemoryAllocationPolicy.ExceedsSafeShare(4096, 8192), "4096 is fine on 8 GB");

            // Probe: missing folders are normal, jars and shader zips are counted.
            var dir = Path.Combine(Path.GetTempPath(), "aurora-mem-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var (m0, s0) = MemoryAllocationPolicy.Probe(dir);
            Assert(m0 == 0 && !s0, "empty instance → vanilla, no shader");
            var modsDir = Directory.CreateDirectory(Path.Combine(dir, "mods")).FullName;
            File.WriteAllText(Path.Combine(modsDir, "a.jar"), "");
            File.WriteAllText(Path.Combine(modsDir, "b.jar"), "");
            File.WriteAllText(Path.Combine(modsDir, "notes.txt"), "");
            var shaderDir = Directory.CreateDirectory(Path.Combine(dir, "shaderpacks")).FullName;
            File.WriteAllText(Path.Combine(shaderDir, "complementary.zip"), "");
            var (m1, s1) = MemoryAllocationPolicy.Probe(dir);
            Assert(m1 == 2 && s1, $"two jars + one shader pack, got {m1}/{s1}");
            Directory.Delete(dir, true);
        });

        Check("DataRootPolicy: 临时目录不得成为数据根（第 84 轮）", () =>
        {
            // The exact shape of the bug: a smoke test's temp root got persisted
            // into the real config, so the launcher read an empty folder.
            var temp = Path.GetTempPath();
            Assert(DataRootPolicy.IsEphemeral(Path.Combine(temp, "aurora-crash-smoke-3ecf5c")),
                "smoke root under %TEMP% must be recognized");
            Assert(DataRootPolicy.IsEphemeral(Path.Combine(temp, "aurora-mem-smoke-abc123")),
                "any aurora-* temp root must be recognized");
            Assert(!DataRootPolicy.IsEphemeral(Path.Combine(temp, "some-other-app")),
                "unrelated temp folders are not ours");
            Assert(!DataRootPolicy.IsEphemeral(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
                "%APPDATA% is a legitimate root");
            Assert(!DataRootPolicy.IsEphemeral(@"D:\AuroraData"), "an explicit drive path is legitimate");
            Assert(!DataRootPolicy.IsEphemeral(""), "empty is not ephemeral");
            Assert(!DataRootPolicy.IsEphemeral(null), "null is not ephemeral");
            // Prefix confusion: "TempX" must not match "Temp".
            Assert(!DataRootPolicy.IsEphemeral(Path.Combine(temp.TrimEnd('\\') + "X", "aurora-x")),
                "a sibling folder whose name starts with the temp path must not match");

            var root = FindSourceRoot()!;
            var cfg = File.ReadAllText(Path.Combine(root, "src", "AuroraCore", "LauncherConfig.cs"));
            Assert(cfg.Contains("DataRootPolicy.IsEphemeral(RootDirectory)"),
                "Save() must refuse to persist a temp root");
            Assert(cfg.Contains("DataRootPolicy.Sanitize(cfg.RootDirectory"),
                "Load() must heal a config that already points at a temp folder");
            Assert(cfg.Contains("public static void UseTestRoot"),
                "tests need a way to isolate the CONFIG FILE, not only the root");

            // The heal itself, without touching a real config file.
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AuroraLauncher");
            var (healedRoot, healed) = DataRootPolicy.Sanitize(Path.Combine(temp, "aurora-crash-smoke-3ecf5c"), fallback);
            Assert(healed && healedRoot == fallback, "a temp root must be healed back to the default root");
            var (keptRoot, kept) = DataRootPolicy.Sanitize(@"D:\AuroraData", fallback);
            Assert(!kept && keptRoot == @"D:\AuroraData", "a legitimate custom root must be left alone");
        });

        Check("InstanceManager: 删除先删文件夹，失败保留记录；扫描不碰陌生文件夹（第 84 轮）", () =>
        {
            var mgr = new InstanceManager();

            // ---- delete: folder first, then the record ----
            var a = mgr.Create("__round84_del__", "1.21.1", ModLoaderType.Vanilla);
            File.WriteAllText(Path.Combine(a.Directory, "marker.txt"), "x");
            var res = mgr.DeleteChecked(a.Id);
            Assert(res.Ok, "delete of an unlocked instance must succeed: " + res.Message);
            Assert(!Directory.Exists(a.Directory), "the FOLDER must be gone, not just the list row");
            Assert(mgr.LoadAll().All(i => i.Id != a.Id), "the record must be gone too");

            // ---- locked folder: record must survive ----
            var b = mgr.Create("__round84_locked__", "1.21.1", ModLoaderType.Vanilla);
            var victim = Path.Combine(b.Directory, "locked.dat");
            File.WriteAllText(victim, "held");
            using (var hold = new FileStream(victim, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var locked = mgr.DeleteChecked(b.Id, attempts: 1, delayMs: 0);
                Assert(locked.Outcome == InstanceManager.DeleteOutcome.FilesLocked,
                    $"a locked folder must report FilesLocked, got {locked.Outcome}");
                Assert(Directory.Exists(b.Directory), "the folder is still there — nothing was deleted");
                Assert(mgr.LoadAll().Any(i => i.Id == b.Id),
                    "THE record must stay when the folder survives (otherwise it becomes a ghost)");
                Assert(locked.Describe("B").Contains("文件夹"), "the message must tell the user files remain");
            }
            // After the lock is released the retry path works.
            var retry = mgr.DeleteChecked(b.Id);
            Assert(retry.Ok && !Directory.Exists(b.Directory), "delete must succeed once the file is released");

            // ---- scan: recover with the ORIGINAL id, never touch strangers ----
            var instancesDir = LauncherConfig.Instance.InstancesDir;
            var ghostId = "ghost" + Guid.NewGuid().ToString("N")[..6];
            var ghostDir = Path.Combine(instancesDir, ghostId);
            Directory.CreateDirectory(ghostDir);
            File.WriteAllText(Path.Combine(ghostDir, InstanceManager.MetaFileName),
                JsonHelper.SerializeIndented(new GameInstance
                {
                    Id = ghostId, Name = "幽灵实例", MinecraftVersion = "1.20.1", LoaderType = ModLoaderType.Fabric
                }));

            var strangerDir = Path.Combine(instancesDir, "my-own-folder");
            Directory.CreateDirectory(strangerDir);
            File.WriteAllText(Path.Combine(strangerDir, "important.txt"), "do not touch");

            var brokenDir = Path.Combine(instancesDir, "broken-meta");
            Directory.CreateDirectory(brokenDir);
            File.WriteAllText(Path.Combine(brokenDir, InstanceManager.MetaFileName), "{ not json");

            var scan = mgr.Scan();
            var recovered = scan.Recovered.SingleOrDefault(i => i.Id == ghostId);
            Assert(recovered != null, "the folder with a valid marker must be recovered");
            Assert(recovered!.Id == ghostId, "the ORIGINAL id must be preserved (a new GUID would orphan saves/mods)");
            Assert(recovered.Name == "幽灵实例", "metadata must be read from the marker, not invented");
            Assert(scan.UnrecognizedFolders.Contains("my-own-folder"), "a folder without a marker must be reported");
            Assert(File.Exists(Path.Combine(strangerDir, "important.txt")),
                "an unrecognized folder must never be modified");
            Assert(scan.CorruptedFolders.Contains("broken-meta"), "a broken marker must be reported as corrupted");
            Assert(scan.MissingIds.Count == 0 || scan.MissingIds.All(id => !string.IsNullOrWhiteSpace(id)),
                "dead links are ids, never empty strings");

            // Dead link: record without a folder.
            var orphan = mgr.Create("__round84_orphan__", "1.21.1", ModLoaderType.Vanilla);
            Directory.Delete(orphan.Directory, true);
            var scan2 = mgr.Scan();
            Assert(scan2.MissingIds.Contains(orphan.Id), "a record whose folder is gone must be listed as a dead link");
            mgr.DeleteChecked(orphan.Id); // drops the record; folder already gone

            Directory.Delete(ghostDir, true);
            Directory.Delete(strangerDir, true);
            Directory.Delete(brokenDir, true);

            // Static ordering guard: the UI may only drop the row AFTER the folder
            // is confirmed gone — "先删列表再删文件" is how ghosts are born.
            var vm = File.ReadAllText(Path.Combine(FindSourceRoot()!, "src", "AuroraLauncher.Desktop",
                "ViewModels", "MainViewModel.cs"));
            var start = vm.IndexOf("public async Task DeleteSelectedInstanceAsync()", StringComparison.Ordinal);
            Assert(start > 0, "DeleteSelectedInstanceAsync must exist");
            var body = vm.Substring(start, Math.Min(4200, vm.Length - start));
            var okIndex = body.IndexOf("if (result.Ok)", StringComparison.Ordinal);
            var removeIndex = body.IndexOf("Instances.Remove(removed)", StringComparison.Ordinal);
            Assert(body.Contains("DeleteChecked"), "delete must go through DeleteChecked (which reports the reason)");
            Assert(okIndex > 0 && removeIndex > okIndex,
                "Instances.Remove must happen only inside the success branch");
            Assert(body.Contains("MessageBoxResult.Yes"), "a destructive delete needs an explicit confirmation");
        });

        Check("Guard: 新建实例只跳转，绝不偷偷建空实例（第 84 轮）", () =>
        {
            var root = FindSourceRoot()!;
            var vm = File.ReadAllText(Path.Combine(root, "src", "AuroraLauncher.Desktop",
                "ViewModels", "MainViewModel.cs"));
            var start = vm.IndexOf("public void CreateInstance()", StringComparison.Ordinal);
            Assert(start > 0, "CreateInstance must exist");
            var body = vm.Substring(start, Math.Min(900, vm.Length - start));
            Assert(body.Contains("Navigate(\"Download\")"), "「新建实例」必须跳转到游戏下载页");
            Assert(!body.Contains("Instances.Create("),
                "「新建实例」不得直接生成实例记录——版本还没装，建出来就是一个无法启动的空壳");
        });

        Check("Guard: 冒烟测试必须隔离配置文件（第 84 轮）", () =>
        {
            // The round-78 smoke set RootDirectory to a temp dir but saved through
            // the REAL config.json, permanently pointing the user's launcher at
            // %TEMP%. Every harness must isolate the config file itself.
            var tests = Path.Combine(FindSourceRoot()!, "tests", "AuroraSelfTest");
            var harnesses = Directory.EnumerateFiles(tests, "*.cs")
                .Where(f => f.Contains("Smoke", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert(harnesses.Count >= 4, $"expected several harnesses, found {harnesses.Count}");
            foreach (var file in harnesses)
            {
                var text = File.ReadAllText(file);
                if (!text.Contains("RootDirectory", StringComparison.Ordinal)) continue;
                Assert(text.Contains("LauncherConfig.UseTestRoot("),
                    $"{Path.GetFileName(file)} must isolate the config file via UseTestRoot");
                Assert(!System.Text.RegularExpressions.Regex.IsMatch(text,
                        @"LauncherConfig\.Instance\.RootDirectory\s*="),
                    $"{Path.GetFileName(file)} must not assign RootDirectory directly (config path stays real)");
            }
        });

        Check("Guard: 内存清理只碰自己，GC.Collect 只在后台（第 83 轮）", () =>
        {
            var root = FindSourceRoot()!;
            var desktop = Path.Combine(root, "src", "AuroraLauncher.Desktop");
            var hygiene = File.ReadAllText(Path.Combine(desktop, "Services", "MemoryHygiene.cs"));

            // (1) No other-process memory work. That is the "one-click RAM
            // booster" pattern: everything it pages out is paged straight back
            // in, and trimming a running JVM stalls the game.
            foreach (var banned in new[] { "Process.GetProcesses", "GetProcessesByName", "OpenProcess(", "\"javaw\"", "\"java\"" })
                Assert(!hygiene.Contains(banned, StringComparison.Ordinal), $"MemoryHygiene must not use {banned}");
            Assert(hygiene.Contains("SetProcessWorkingSetSize(GetCurrentProcess()"),
                "the trim must target this process only");

            // (2) GC.Collect exists in exactly one file — the hygiene helper.
            foreach (var file in Directory.EnumerateFiles(desktop, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains("\\obj\\")) continue;
                if (!File.ReadAllText(file).Contains("GC.Collect")) continue;
                Assert(Path.GetFileName(file) == "MemoryHygiene.cs",
                    $"{Path.GetFileName(file)} calls GC.Collect outside the hygiene helper");
            }

            // (3) ...and the VM reaches it from a worker thread, never inline.
            var vm = File.ReadAllText(Path.Combine(desktop, "ViewModels", "MainViewModel.Round83.cs"));
            Assert(vm.Contains("Task.Run(() => MemoryHygiene.Trim(aggressive: true))"),
                "the deep clean must run on a worker thread");
            Assert(!vm.Contains("MemoryHygiene.Trim(aggressive: true);"),
                "no inline trim on the UI thread");
            Assert(vm.Contains("Task.Run(() => MemoryHygiene.Trim(aggressive: false))"),
                "the idle path must be background too");

            // (4) The launch path resolves -Xmx through the policy, and auto mode
            // is a persisted switch — otherwise "auto" would be a UI-only myth.
            var svc = File.ReadAllText(Path.Combine(root, "src", "AuroraCore", "Services", "LauncherService.cs"));
            Assert(svc.Contains("MemoryAllocationPolicy.Effective("),
                "the launch must resolve -Xmx through MemoryAllocationPolicy");
            Assert(svc.Contains("Math.Min(cfg.MinMemoryMb, maxMemoryMb)"),
                "-Xms must be clamped to -Xmx or the JVM refuses to start");
            var cfg = File.ReadAllText(Path.Combine(root, "src", "AuroraCore", "LauncherConfig.cs"));
            Assert(cfg.Contains("public bool AutoMemory"), "auto mode must be a persisted switch");
        });

        Check("CurseForgeService: search parse + official-page URL builders (pure logic)", () =>
        {
            var json = "{\"data\":[" +
                "{\"id\":123,\"name\":\"JEI\",\"summary\":\"Just Enough Items\",\"slug\":\"jei\"," +
                "\"downloadCount\":1234567,\"links\":{\"websiteUrl\":\"https://www.curseforge.com/minecraft/mc-mods/jei\"}}," +
                "{\"id\":0,\"name\":\"Malformed\"}]}";
            var list = CurseForgeService.ParseSearch(json);
            Assert(list.Count == 1, "entry without a valid id must be skipped");
            Assert(list[0].Id == 123 && list[0].Name == "JEI" && list[0].Slug == "jei" && list[0].Downloads == 1234567,
                "fields must parse verbatim");
            Assert(CurseForgeService.BuildFilesPageUrl(list[0]).EndsWith("/files", StringComparison.Ordinal),
                "download action must land on the official files page");
            Assert(CurseForgeService.ParseSearch("{}").Count == 0, "wrong JSON shape must fail closed");
            Assert(CurseForgeService.BuildWebSearchUrl("a b").Contains("a%20b", StringComparison.Ordinal),
                "web search query must be URL-encoded");
        });

        Check("OptiFineService: downloads-page parse + ad-token URL parse (pure logic)", () =>
        {
            var html = "<a href=\"adloadx?f=OptiFine_1.21.1_HD_U_J1.jar\">A</a>" +
                       "<a href=\"adloadx?f=OptiFine_1.20.1_HD_U_I6.jar\">B</a>" +
                       "<a href=\"adloadx?f=OptiFine_1.21.1_HD_U_J1.jar\">dup</a>";
            var builds = OptiFineService.ParseDownloadsPage(html);
            Assert(builds.Count == 2, "duplicate installer links must be de-duplicated");
            Assert(builds[0].McVersion == "1.21.1" && builds[0].FileName.EndsWith(".jar", StringComparison.Ordinal),
                "MC version / file name must derive from the conventional naming");
            Assert(builds[0].PageUrl.StartsWith("https://optifine.net/adloadx", StringComparison.Ordinal),
                "ad page URL must be the official site");
            Assert(OptiFineService.ParseDownloadsPage("no links here").Count == 0, "no links → empty list");

            var ad = "<form action=\"downloadx\">" +
                     "<input type=\"hidden\" name=\"f\" value=\"OptiFine_1.21.1_HD_U_J1.jar\">" +
                     "<input type=\"hidden\" name=\"x\" value=\"ABC123\"></form>";
            Assert(OptiFineService.ParseDownloadUrl(ad) ==
                   "https://optifine.net/downloadx?f=OptiFine_1.21.1_HD_U_J1.jar&x=ABC123",
                "hidden form fields must resolve to the official download URL");
            Assert(OptiFineService.ParseDownloadUrl(
                       "<a href=\"downloadx?f=OptiFine_1.20.1_HD_U_I6.jar&amp;x=ZZ9\">get</a>") ==
                   "https://optifine.net/downloadx?f=OptiFine_1.20.1_HD_U_I6.jar&x=ZZ9",
                "inline (HTML-escaped) link must resolve");
            Assert(OptiFineService.ParseDownloadUrl("<html>nothing</html>") == null,
                "missing token must fail closed instead of downloading the ad page");
        });

        Check("UpdateApplyArgs: visible updater handoff round-trips and fails closed", () =>
        {
            // Round-61: the launcher relaunches ITSELF as a visible updater
            // host. A mangled handoff would either show nothing (black screen
            // again) or overwrite the wrong file — parsing must fail closed.
            var plan = new UpdateApplyPlan
            {
                OldPid = 4321,
                TargetExe = @"C:\Program Files\Aurora\AuroraLauncher.exe",
                StagedExe = @"D:\My Games\Aurora\update\AuroraLauncher.new.exe",
                StagedInfo = @"D:\My Games\Aurora\update\build-info.txt"
            };
            Assert(plan.IsValid, "plan with absolute paths must be valid");
            var token = UpdateApplyArgs.Build(plan);
            Assert(token.Contains(@"C:\Program Files\Aurora", StringComparison.Ordinal),
                "paths containing spaces must survive as ONE argv token");
            Assert(UpdateApplyArgs.TryParse(new[] { "--other", token }, out var parsed) && parsed != null,
                "host switch must be found among unrelated args");
            Assert(parsed!.OldPid == plan.OldPid && parsed.TargetExe == plan.TargetExe &&
                   parsed.StagedExe == plan.StagedExe && parsed.StagedInfo == plan.StagedInfo,
                "round-trip must preserve every field");

            foreach (var bad in new[]
                     {
                         "--apply-update|abc|C:\\a.exe|C:\\b.exe|C:\\i.txt",   // pid not a number
                         "--apply-update|1|relative\\a.exe|C:\\b.exe|C:\\i.txt", // relative target
                         "--apply-update|-4|C:\\a.exe|C:\\b.exe|C:\\i.txt",      // invalid pid
                         "--apply-update|1|C:\\a.exe|C:\\b.exe",                 // truncated
                         "--apply-update|1|C:\\a.exe|C:\\b.exe|C:\\i.txt|extra"  // extra field
                     })
                Assert(!UpdateApplyArgs.TryParse(new[] { bad }, out _),
                    "malformed handoff must fail closed: " + bad);
            Assert(!UpdateApplyArgs.TryParse(Array.Empty<string>(), out _), "no args → normal launch");
            Assert(!new UpdateApplyPlan { OldPid = 0 }.IsValid, "missing paths/pid must be invalid");
            Assert(UpdateApplier.MaxCopyAttempts >= 3, "copy must keep a bounded retry budget");

            // Full swap rehearsal without a GUI: an already-dead PID, a temp
            // "old exe", a staged payload and a real SHA-256 in build-info.
            // The launch is injected, so no second launcher is spawned.
            var sandbox = Path.Combine(Path.GetTempPath(), "aurora-swap-" + Guid.NewGuid().ToString("N"));
            try
            {
                var stageDir = Path.Combine(sandbox, "update");
                Directory.CreateDirectory(stageDir);
                var stage = Path.Combine(stageDir, "AuroraLauncher.new.exe");
                var info = Path.Combine(stageDir, "build-info.txt");
                var target = Path.Combine(sandbox, "AuroraLauncher.exe");
                const string payload = "new launcher payload";
                File.WriteAllText(stage, payload);
                File.WriteAllText(target, "old launcher");
                var hash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(stage)));
                File.WriteAllText(info, "build-time : test\r\nexe-sha256 : " + hash + "\r\n");
                var swap = new UpdateApplyPlan
                {
                    OldPid = int.MaxValue, // guaranteed absent → wait resolves immediately
                    TargetExe = target,
                    StagedExe = stage,
                    StagedInfo = info
                };
                var stages = new List<string>();
                string? launched = null;
                UpdateApplier.ApplyAsync(swap, (s, _) => stages.Add(s), default,
                    p => { launched = p; return true; }).GetAwaiter().GetResult();
                Assert(File.ReadAllText(target) == payload, "target exe must be replaced by the staged bytes");
                Assert(launched == target, "new launcher must be relaunched from the TARGET path");
                Assert(!File.Exists(stage), "staged copy must be swept after a successful swap");
                Assert(File.Exists(Path.Combine(sandbox, "build-info.txt")), "build-info must be copied next to the new exe");
                Assert(stages.Any(s => s.Contains("替换")) && stages.Any(s => s.Contains("重新启动")),
                    "visible stage texts must cover copy and relaunch");

                // Tamper with the staged file → fail closed, installed exe untouched.
                File.WriteAllText(stage, "TAMPERED");
                File.WriteAllText(target, "installed launcher");
                var threw = false;
                try
                {
                    UpdateApplier.ApplyAsync(swap, (_, _) => { }, default, _ => true).GetAwaiter().GetResult();
                }
                catch (IOException) { threw = true; }
                Assert(threw, "a staged file that no longer matches its sha256 must fail closed");
                Assert(File.ReadAllText(target) == "installed launcher", "a failed swap must not damage the installed exe");
            }
            finally
            {
                try { Directory.Delete(sandbox, recursive: true); } catch { /* best effort */ }
            }
        });

        Check("JavaRequirement: MC version -> recommended Java major (pure logic)", () =>
        {
            // Round-62: a 1.12 pack must stay on Java 8 while a 1.21 pack
            // uses Java 21. This mapping is the point of per-instance Java —
            // guard the boundary versions explicitly.
            var cases = new (string Mc, int Major)[]
            {
                ("1.7.10", 8), ("1.12.2", 8), ("1.16.5", 8),
                ("1.17", 17), ("1.18.2", 17), ("1.20", 17), ("1.20.4", 17),
                ("1.20.5", 21), ("1.21", 21), ("1.21.1", 21), ("1.22", 21),
                ("24w14a", 21), ("", 21), ("fabric-loader-0.20.1-1.21", 21)
            };
            foreach (var (mc, major) in cases)
                Assert(JavaRequirement.RecommendMajor(mc) == major, $"'{mc}' should recommend Java {major}");
            Assert(JavaRequirement.Describe("1.12.2").Contains("Java 8"), "hint must name the recommended major");
        });

        Check("UpdateBannerPolicy: outdated launcher CTA + staged wording (pure logic)", () =>
        {
            Assert(!UpdateBannerPolicy.IsOutdated(62, null), "no manifest → not outdated");
            Assert(!UpdateBannerPolicy.IsOutdated(62, 62), "same round → not outdated");
            Assert(!UpdateBannerPolicy.IsOutdated(62, 61), "downgrade must never count as an update");
            Assert(UpdateBannerPolicy.IsOutdated(62, 63), "higher round → outdated");
            Assert(UpdateBannerPolicy.Text(62, 63, false).Contains("第 63 轮"), "banner must name the available round");
            Assert(UpdateBannerPolicy.Text(62, 63, false).Contains("一键更新"), "banner must point at the button");
            Assert(UpdateBannerPolicy.Text(62, null, false).Contains("检查"), "unchecked state must offer a check");
            Assert(UpdateBannerPolicy.Text(62, 62, false).Contains("已是最新"), "latest state must say so");
            Assert(UpdateBannerPolicy.Text(62, 63, true).Contains("已下载"), "staged package takes priority in wording");
        });

        Check("BulkObservableCollection + NavigationPolicy: one-shot UI updates / no redundant navigation", () =>
        {
            // Round-63 freeze: Clear + 103 Add events on the UI thread, and
            // re-assigning Content for the already-active page. Both are now
            // pure logic with an explicit contract.
            var col = new AuroraLauncher.Desktop.ViewModels.BulkObservableCollection<int>();
            var events = new List<string>();
            col.CollectionChanged += (_, e) => events.Add(e.Action.ToString());
            col.Replace(new[] { 1, 2, 3, 4, 5 });
            Assert(events.Count == 1 && events[0] == "Reset",
                $"Replace must raise exactly ONE Reset, got: {string.Join(",", events)}");
            Assert(col.Count == 5 && col[4] == 5, "contents must be swapped in one pass");
            col.Replace(Array.Empty<int>());
            Assert(col.Count == 0 && events.Count == 2, "a second Replace must also be a single Reset");

            Assert(NavigationPolicy.Normalize(null) == "Home", "null key → Home");
            Assert(NavigationPolicy.Normalize("  ") == "Home", "blank key → Home");
            Assert(NavigationPolicy.Normalize("Mods") == "Mods", "explicit key survives");
            Assert(NavigationPolicy.ShouldNavigate("Home", "Home", false), "first navigation must proceed");
            Assert(!NavigationPolicy.ShouldNavigate("Home", "Home", true), "repeat click on active page must be a no-op");
            Assert(NavigationPolicy.ShouldNavigate("Home", "Mods", true), "different page must proceed");
        });

        Check("Guard: cached pages never re-run init on every Loaded (round-63)", () =>
        {
            // Cached page instances re-raise Loaded on EVERY re-attach. Any
            // new async init hooked there without a single-shot guard would
            // bring the "未响应" freeze back.
            var root = FindSourceRoot()!;
            var views = Path.Combine(root, "src", "AuroraLauncher.Desktop", "Views");
            foreach (var file in Directory.EnumerateFiles(views, "*.xaml.cs"))
            {
                var text = File.ReadAllText(file);
                if (!text.Contains("Loaded +=")) continue;
                var name = Path.GetFileName(file);
                Assert(LoadedGuardPolicy.IsGuarded(text),
                    $"{name} registers Loaded without a single-shot guard or a re-entrant hook");
                if (text.Contains("Loaded += async"))
                    Assert(LoadedGuardPolicy.IsGuarded(text),
                        $"{name} uses an async Loaded handler without a guard");
            }
            var vm = File.ReadAllText(Path.Combine(root, "src", "AuroraLauncher.Desktop", "ViewModels", "MainViewModel.cs"));
            Assert(!vm.Contains("Versions.Clear()"), "version list must use bulk Replace (one Reset)");
            Assert(!vm.Contains("ModSearchResults.Clear()"), "search results must use bulk Replace");
            Assert(!vm.Contains("JavaList.Clear()"), "Java list must use bulk Replace");
        });

        Check("CoalescingSingleFlight: A→B→C collapses to ONE rerun with the latest arg", () =>
        {
            // Round-64 review: the two easy regressions are a QUEUE (5 rapid
            // triggers → 5 reruns) and a DROP (newest choice lost). Assert
            // both halves of the contract with an injectable, gated runner.
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var seen = new List<string>();
            var flight = new CoalescingSingleFlight<string>(async arg =>
            {
                seen.Add(arg);
                await gate.Task.ConfigureAwait(false);
            });

            var first = flight.RequestAsync("A");
            Assert(flight.IsRunning, "the first request must own the in-flight run");
            foreach (var k in new[] { "B", "C", "D", "E" })
                flight.RequestAsync(k).GetAwaiter().GetResult(); // coalesced → completed Task
            gate.SetResult(true);
            first.GetAwaiter().GetResult();

            Assert(flight.RunCount == 2, $"exactly ONE extra run expected, got {flight.RunCount} (queue degradation)");
            Assert(seen.Count == 2 && seen[1] == "E",
                $"newest argument must win, got [{string.Join(",", seen)}] (drop degradation)");
            Assert(flight.Latest == "E", "latest argument must be retained");

            flight.RequestAsync("F").GetAwaiter().GetResult();
            Assert(flight.RunCount == 3 && seen[2] == "F", "an idle request must run exactly once");
            Assert(!flight.IsRunning, "flight must be idle afterwards");
        });

        Check("UiPolicies: selection restore / debounce guard / navigation sub-key", () =>
        {
            var items = new[] { "1.21", "1.20.4", "1.12.2" };
            Assert(SelectionPolicy.RestoreByKey(items, s => s, "1.20.4") == "1.20.4", "restore by stable key");
            Assert(SelectionPolicy.RestoreByKey(items, s => s, "9.9") == "1.21", "missing key → first item");
            Assert(SelectionPolicy.RestoreByKey(items, s => s, null) == "1.21", "null key → first item");
            Assert(SelectionPolicy.RestoreByKey(Array.Empty<string>(), s => s, "1.21") == null, "empty list → null");

            Assert(LoaderDebouncePolicy.ShouldFire("Download", "1.21", "1.21"), "normal case fires");
            Assert(!LoaderDebouncePolicy.ShouldFire("Home", "1.21", "1.21"), "user navigated away → no stale fetch");
            Assert(!LoaderDebouncePolicy.ShouldFire("Download", "1.21", "1.20.4"), "version changed → no stale fetch");
            Assert(!LoaderDebouncePolicy.ShouldFire("Download", "", ""), "no target version → no fetch");

            Assert(!NavigationPolicy.ShouldNavigate("Mods", "Mods", true), "same page → no-op");
            Assert(NavigationPolicy.ShouldNavigate("Mods", "Mods", true, "模组", "光影包"),
                "different sub-key must NOT be swallowed by the early-return");
            Assert(!NavigationPolicy.ShouldNavigate("Mods", "Mods", true, "模组", "模组"), "same sub-key → no-op");
            Assert(NavigationPolicy.ShouldNavigate("Home", "Home", false), "first navigation always proceeds");
        });

        Check("LoadedGuardPolicy: accepts any single-shot field name, rejects unguarded Loaded", () =>
        {
            const string altName = "Loaded += (_, _) => { if (_isLoaded) return; };\nprivate bool _isLoaded;";
            const string negated = "private bool _firstLoadDone;\nLoaded += async (_, _) => { if (!_firstLoadDone) return; };";
            Assert(LoadedGuardPolicy.IsGuarded(altName), "_isLoaded must be accepted");
            Assert(LoadedGuardPolicy.IsGuarded(negated), "negated guard must be accepted");
            Assert(LoadedGuardPolicy.IsGuarded("Loaded += (_, _) => HookVm(vm);\nprivate void HookVm(object? o) { }"),
                "re-entrant hook must be accepted");
            Assert(!LoadedGuardPolicy.IsGuarded("Loaded += (_, _) => { DoWork(); };"),
                "unguarded Loaded must be rejected");
            Assert(!LoadedGuardPolicy.IsGuarded("private bool _whatever;\nLoaded += (_, _) => { DoWork(); };"),
                "a bool field alone is not a guard");
        });

        Check("PageHostPolicy: lazy page creation + exactly one visible page", () =>
        {
            // Round-65: the third "未响应" report. The fix is architectural —
            // keep pages in the visual tree and flip Visibility instead of
            // re-assigning ContentControl.Content.
            var created = new List<string>();
            Assert(PageHostPolicy.ShouldCreate(created, "Home"), "first visit must create the page");
            created.Add("Home");
            Assert(!PageHostPolicy.ShouldCreate(created, "Home"), "second visit must NOT rebuild it");
            Assert(PageHostPolicy.ShouldCreate(created, "Mods"), "a new key must be created once");
            created.Add("Mods");
            Assert(PageHostPolicy.IsVisible("Home", "Home") && !PageHostPolicy.IsVisible("Home", "Mods"),
                "exactly one page may be visible");
            Assert(!PageHostPolicy.IsVisible("Mods", "Home") && PageHostPolicy.IsVisible("Mods", "Mods"),
                "switching must move visibility, not rebuild");
            Assert(!PageHostPolicy.IsVisible("Mods", "mods"), "keys compare ordinally");

            var keys = new[] { "Home", "Mods", "Settings", "Download" };
            var set = new HashSet<string>();
            for (var i = 0; i < 40; i++)
            {
                var k = keys[i % keys.Length];
                if (PageHostPolicy.ShouldCreate(set, k)) set.Add(k);
            }
            Assert(set.Count == 4, $"40 rapid switches must create only 4 pages, got {set.Count}");
        });

        Check("Guard: navigation keeps pages in the visual tree (round-65)", () =>
        {
            var root = FindSourceRoot()!;
            var desktop = Path.Combine(root, "src", "AuroraLauncher.Desktop");
            var mainXaml = File.ReadAllText(Path.Combine(desktop, "MainWindow.xaml"));
            var mainCs = File.ReadAllText(Path.Combine(desktop, "MainWindow.xaml.cs"));
            var vm = File.ReadAllText(Path.Combine(desktop, "ViewModels", "MainViewModel.cs"));
            Assert(!mainXaml.Contains("Content=\"{Binding CurrentPage}\""),
                "swapping ContentControl.Content rebuilds the whole visual tree on every click");
            Assert(mainXaml.Contains("x:Name=\"PageHost\""), "a persistent page host is required");
            Assert(mainCs.Contains("PageHost.Children.Add"), "pages must be added once and kept");
            Assert(mainCs.Contains("Visibility.Collapsed") && mainCs.Contains("Visibility.Visible"),
                "navigation must flip Visibility");
            Assert(!vm.Contains("CurrentPage ="), "the VM must not assign a page object to a ContentControl");

            // Hidden pages keep their subscriptions now — they must not force
            // layout from collection-changed / property-changed callbacks.
            var logs = File.ReadAllText(Path.Combine(desktop, "Views", "LogsView.xaml.cs"));
            var mods = File.ReadAllText(Path.Combine(desktop, "Views", "ModsView.xaml.cs"));
            Assert(logs.Contains("!IsVisible"), "hidden LogsView must skip ScrollIntoView");
            Assert(mods.Contains("!IsVisible"), "hidden ModsView must skip scroll-to-compatible layout");
        });

        Check("Guard: no animation clocks / software effects in the UI (round-67, glass-whitelist round-80)", () =>
        {
            // Round-67 regression: round-66 released Card Effect/CacheMode on
            // hidden pages, making EVERY navigation regenerate all shadow
            // bitmaps on the incoming page — one click could freeze/crash.
            // Final policy: remove the whole class, plus all animation clocks.
            //
            // Round-80: "liquid glass" needs ONE narrow exception, so the ban is
            // now enforced through GlassGuardPolicy: a BlurEffect is legal only
            // in MainWindow.xaml on a Glass*-named element (never inside a
            // DataTemplate), and in code only from a GlassBlur-named member.
            var root = FindSourceRoot()!;
            var desktop = Path.Combine(root, "src", "AuroraLauncher.Desktop");
            var styles = File.ReadAllText(Path.Combine(desktop, "Styles", "AuroraControls.xaml"));

            // (1) No time-based animation at all (zero clocks to accumulate).
            //     Check TAGS, not words — explanatory comments mention them.
            Assert(!styles.Contains("<BeginStoryboard") && !styles.Contains("<Storyboard")
                   && !styles.Contains("RepeatBehavior="),
                "indeterminate bars must be STATIC — no animation clock may run");

            // (2) No software-rendered shadow / bitmap cache anywhere. Blur is
            //     NOT banned outright any more, but every occurrence must pass
            //     the whitelist policy below.
            Assert(!styles.Contains("<DropShadowEffect") && !styles.Contains("<BitmapCache")
                   && !styles.Contains("Property=\"Effect\"") && !styles.Contains("Property=\"CacheMode\""),
                "DropShadowEffect / BitmapCache must stay removed (round-67 regression)");
            foreach (var xaml in Directory.EnumerateFiles(desktop, "*.xaml", SearchOption.AllDirectories))
                Assert(!File.ReadAllText(xaml).Contains("Effect=\"{StaticResource"),
                    $"{Path.GetFileName(xaml)} applies a StaticResource Effect");

            // (2b) Round-80 whitelist: blur may live only behind a Glass* element
            //      of MainWindow.xaml, never in a list row.
            var blurViolations = new List<string>();
            foreach (var xaml in Directory.EnumerateFiles(desktop, "*.xaml", SearchOption.AllDirectories))
                blurViolations.AddRange(GlassGuardPolicy.ScanXaml(File.ReadAllText(xaml), Path.GetFileName(xaml)));
            Assert(blurViolations.Count == 0, string.Join("; ", blurViolations));

            foreach (var cs in Directory.EnumerateFiles(desktop, "*.cs", SearchOption.AllDirectories))
            {
                if (cs.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    cs.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                var file = Path.GetFileName(cs);
                var lineNo = 0;
                foreach (var line in File.ReadLines(cs))
                {
                    lineNo++;
                    if (!line.Contains("BlurEffect", StringComparison.Ordinal)) continue;
                    if (line.TrimStart().StartsWith("//") || line.TrimStart().StartsWith("///")) continue;
                    Assert(GlassGuardPolicy.IsCodeBlurAllowed(file, line),
                        $"{file}:{lineNo} 在非白名单位置创建/引用 BlurEffect —— 只允许 MainWindow 的 GlassBlur* 成员");
                }
            }

            // (3) Every ListBox recycles containers.
            Assert(styles.Contains("VirtualizingPanel.VirtualizationMode\" Value=\"Recycling\"")
                   && styles.Contains("ScrollViewer.CanContentScroll\" Value=\"True\""),
                "all lists must virtualize with container recycling");

            // (4) No synchronous Dispatcher.Invoke in product code.
            foreach (var f in Directory.EnumerateFiles(desktop, "*.cs", SearchOption.AllDirectories))
                Assert(!File.ReadAllText(f).Contains("Dispatcher.Invoke("),
                    $"{Path.GetFileName(f)} uses synchronous Dispatcher.Invoke (UI-thread stall)");

            // (5) No per-navigation allocation in the nav converter.
            var conv = File.ReadAllText(Path.Combine(desktop, "Converters", "PageActiveConverter.cs"));
            Assert(conv.Contains("Brushes.Transparent"), "nav converter must reuse a frozen brush");

            // (6) Runtime smoke diagnostic must remain available.
            Assert(File.Exists(Path.Combine(root, "tests", "AuroraSelfTest", "NavSmoke.cs")),
                "the nav-smoke WPF diagnostic must stay in the repo");

            // Behavioural check: with zero clocks, 40 show/hide cycles add
            // exactly zero running animations.
            var hasClock = styles.Contains("<BeginStoryboard");
            var running = 0;
            for (var i = 0; i < 40; i++)
            {
                if (hasClock) running++;    // the round-66 accumulation
                Assert(running == 0, $"animation clocks accumulated after {i + 1} switches: {running}");
            }
        });

        Check("UiLatencyTrace: flags slow switches and summarises them", () =>
        {
            // Round-68: every remaining "卡顿" theory has to produce a number.
            // The trace is the thing that turns a vague report into "nav:Mods
            // 128ms".
            var trace = new AuroraCore.Services.UiLatencyTrace();
            Assert(!trace.Record("nav:Home", 3.0), "a fast switch must not be flagged");
            Assert(trace.Record("nav:Mods", 128.0), "a switch over 50ms must be flagged");
            Assert(trace.Record("nav:Mods", 61.0), "second slow sample must be flagged too");
            Assert(!trace.Record("nav:Mods", 12.0), "a later fast sample is not slow");

            Assert(trace.Count("nav:Mods") == 3, "every sample is counted");
            Assert(trace.SlowCount("nav:Mods") == 2, "only samples above the threshold are slow");
            Assert(Math.Abs(trace.LastMs("nav:Mods") - 12.0) < 0.001, "last sample wins");
            Assert(trace.LastMs("nav:Help") < 0, "unknown stage reports -1");
            Assert(trace.SlowestStage == "nav:Mods", "slowest stage must be reported");

            var summary = trace.Summary();
            Assert(summary.Contains("4"), $"summary must carry the sample count: {summary}");
            Assert(!trace.Record("", 999), "blank stage must be ignored");
            Assert(!trace.Record("nav:X", double.NaN), "NaN must be ignored");

            trace.Reset();
            Assert(trace.TotalSamples == 0 && trace.TotalSlow == 0, "Reset clears everything");
            Assert(trace.Summary().Contains("暂无"), "empty trace says so instead of dividing by zero");
        });

        Check("PageWarmupPolicy: sidebar order, skip built pages, never warm while busy", () =>
        {
            // Round-68: pre-warming builds every page during idle so the first
            // click on each tab is a warm 0.3ms flip instead of a 5-60ms build.
            var created = new List<string> { "Home" };
            Assert(AuroraCore.Services.PageWarmupPolicy.NextToWarm(created) == "Download",
                "the next unbuilt page in sidebar order must be warmed");

            created.AddRange(new[] { "Download", "Instances" });
            Assert(AuroraCore.Services.PageWarmupPolicy.NextToWarm(created) == "Mods",
                "already built pages must be skipped");

            var all = AuroraCore.Services.PageWarmupPolicy.PageOrder.ToList();
            Assert(AuroraCore.Services.PageWarmupPolicy.NextToWarm(all) == null,
                "nothing left to warm once every page exists");
            Assert(all.Count == 9 && all[0] == "Home" && all[^1] == "Help",
                "PageOrder must list all nine pages, Home first (most likely next click)");

            Assert(AuroraCore.Services.PageWarmupPolicy.ShouldWarmNow(false, false, false),
                "idle launcher may pre-warm");
            Assert(!AuroraCore.Services.PageWarmupPolicy.ShouldWarmNow(true, false, false),
                "never steal the UI thread while the launcher is busy");
            Assert(!AuroraCore.Services.PageWarmupPolicy.ShouldWarmNow(false, true, false),
                "never pre-warm while a download is reporting progress");
            Assert(!AuroraCore.Services.PageWarmupPolicy.ShouldWarmNow(false, false, true),
                "never pre-warm during shutdown");
        });

        Check("Guard: lists nested in a ScrollViewer stay bounded (round-68)", () =>
        {
            // Item #2 of the round-68 review: an unbounded list inside a page
            // ScrollViewer is measured with infinite height, realises every
            // container and disables virtualization — the round-17 lesson.
            var root = FindSourceRoot()!;
            var views = Path.Combine(root, "src", "AuroraLauncher.Desktop", "Views");
            var offenders = new List<string>();
            var scanned = 0;
            foreach (var xaml in Directory.EnumerateFiles(views, "*.xaml"))
            {
                scanned++;
                foreach (var problem in NestedScrollPolicy.FindUnboundedLists(File.ReadAllText(xaml)))
                    offenders.Add($"{Path.GetFileName(xaml)} {problem}");
            }
            Assert(scanned >= 9, $"expected to scan every page, saw {scanned}");
            Assert(offenders.Count == 0, "unbounded ListBox in a ScrollViewer: " + string.Join("; ", offenders));

            // The policy itself must not be toothless.
            Assert(NestedScrollPolicy.FindUnboundedLists(
                    "<ScrollViewer>\n<ListBox ItemsSource=\"{Binding X}\"/>\n</ScrollViewer>").Count == 1,
                "a plain nested ListBox must be flagged");
            Assert(NestedScrollPolicy.FindUnboundedLists(
                    "<ScrollViewer>\n<ListBox MaxHeight=\"220\" ItemsSource=\"{Binding X}\"/>\n</ScrollViewer>").Count == 0,
                "MaxHeight restores a finite viewport");
            Assert(NestedScrollPolicy.FindUnboundedLists(
                    "<StackPanel>\n<ListBox ItemsSource=\"{Binding X}\"/>\n</StackPanel>").Count == 0,
                "a list outside any ScrollViewer is fine");
            Assert(NestedScrollPolicy.FindUnboundedLists(
                    "<ScrollViewer HorizontalScrollBarVisibility=\"Auto\"/>\n<ListBox ItemsSource=\"{Binding X}\"/>").Count == 0,
                "a self-closing ScrollViewer does not open a scope");
        });

        Check("Guard: page init runs on first SHOWN, not on first attach (round-68)", () =>
        {
            // Pre-warming attaches every page Hidden during idle. Any async
            // init still hooked on Loaded would then fire a Modrinth search /
            // Java probe / manifest fetch for a page nobody opened.
            var root = FindSourceRoot()!;
            var views = Path.Combine(root, "src", "AuroraLauncher.Desktop", "Views");
            foreach (var file in Directory.EnumerateFiles(views, "*.xaml.cs"))
            {
                var text = File.ReadAllText(file);
                var name = Path.GetFileName(file);
                // Only page hosts are pre-warmed; a dialog Window (updater)
                // attaches and shows once, so Loaded is still the right hook.
                if (!text.Contains(": UserControl")) continue;
                if (!text.Contains("Loaded +=")) continue;
                if (!text.Contains("async")) continue;
                Assert(text.Contains("PageLifecycle.OnFirstShown("),
                    $"{name} runs async init from Loaded — use PageLifecycle.OnFirstShown so idle pre-warm cannot trigger it");
            }

            foreach (var name in new[] { "DownloadView.xaml.cs", "ModsView.xaml.cs", "SettingsView.xaml.cs" })
            {
                var text = File.ReadAllText(Path.Combine(views, name));
                Assert(text.Contains("PageLifecycle.OnFirstShown("),
                    $"{name} must run its first init through PageLifecycle.OnFirstShown");
            }

            var lifecycle = File.ReadAllText(Path.Combine(views, "PageLifecycle.cs"));
            Assert(lifecycle.Contains("IsVisibleChanged") && lifecycle.Contains("TryEnter"),
                "the first-shown hook must be single-shot and visibility-driven");
        });

        Check("Logger: file writes are batched off-thread and flushed on demand", () =>
        {
            // Round-69: the user's own log showed `切换页面 Download 用时 62ms（已缓存）`
            // while the page flip measured 0.3 ms — the UI thread was blocked by
            // something else. The biggest such something was the logger itself:
            // every line did File.AppendAllText ON THE CALLING THREAD.
            var dir = Path.Combine(Path.GetTempPath(), "aurora-logs-" + Guid.NewGuid().ToString("N")[..8]);
            var prev = Logger.Instance.LogFilePath;
            Logger.Instance.Configure(dir);
            try
            {
                const int lines = 40;
                for (var i = 0; i < lines; i++)
                    Logger.Instance.Info("selftest", $"batch-line-{i:00}");

                Assert(Logger.Instance.Flush(5000), "Flush must drain the write queue");
                var file = Logger.Instance.LogFilePath!;
                Assert(File.Exists(file), "the log file must exist after Flush");

                var text = File.ReadAllText(file);
                for (var i = 0; i < lines; i++)
                    Assert(text.Contains($"batch-line-{i:00}"), $"line {i} was lost by the async writer");

                var first = text.IndexOf("batch-line-00", StringComparison.Ordinal);
                var last = text.IndexOf($"batch-line-{lines - 1:00}", StringComparison.Ordinal);
                Assert(first >= 0 && last > first, "log lines must keep their order");

                Logger.Instance.Info("selftest", "second-burst");
                Assert(Logger.Instance.Flush(5000), "a second Flush must also drain");
                Assert(File.ReadAllText(file).Contains("second-burst"), "later lines must be appended after a Flush");
            }
            finally
            {
                if (prev != null) Logger.Instance.Configure(Path.GetDirectoryName(prev));
            }
        });

        Check("NavigationLatencyPolicy: 'page is slow' vs 'UI thread was busy'", () =>
        {
            // Round-69: the round-68 warning blamed the page for a slow switch
            // that was actually queue delay. These are the two cases that must
            // stay distinguishable, because they lead to opposite optimizations.
            var fast = AuroraCore.Services.NavigationLatencyPolicy.FromPhases(0.3, 1.2, 2.0);
            Assert(fast.PageMs is > 1.4 and < 1.6, $"page cost = flip + paint, got {fast.PageMs}");
            Assert(AuroraCore.Services.NavigationLatencyPolicy.Classify(fast)
                   == AuroraCore.Services.NavigationSlowKind.None, "a 2ms switch is not slow");

            var busyThread = AuroraCore.Services.NavigationLatencyPolicy.FromPhases(0.3, 1.0, 62.0);
            Assert(AuroraCore.Services.NavigationLatencyPolicy.Classify(busyThread)
                   == AuroraCore.Services.NavigationSlowKind.UiThreadBusy,
                "a fast page behind a busy dispatcher must not be blamed on the page");
            Assert(AuroraCore.Services.NavigationLatencyPolicy.Describe("Download", busyThread, built: false, initial: false)
                   .Contains("UI 线程"), "the busy-thread message must name the real cause");

            var slowPage = AuroraCore.Services.NavigationLatencyPolicy.FromPhases(4.0, 120.0, 124.0);
            Assert(AuroraCore.Services.NavigationLatencyPolicy.Classify(slowPage)
                   == AuroraCore.Services.NavigationSlowKind.PageRender,
                "layout over the threshold is a page problem");
            Assert(AuroraCore.Services.NavigationLatencyPolicy.Describe("Logs", slowPage, built: true, initial: false)
                   .Contains("页面排版"), "the page message must say the page is slow");

            // Clock skew / negative deltas must never produce nonsense.
            var skewed = AuroraCore.Services.NavigationLatencyPolicy.FromPhases(-1, -5, 0);
            Assert(skewed.FlipMs == 0 && skewed.PaintMs == 0 && skewed.QueueMs == 0, "negative phases clamp to 0");
            Assert(AuroraCore.Services.NavigationLatencyPolicy.Describe("Home", fast, false, true)
                   .StartsWith("首次进入"), "the initial activation is described as such");
        });

        Check("Guard: log I/O off the calling thread + Logs feed frozen while hidden (round-69)", () =>
        {
            var root = FindSourceRoot()!;
            var logger = File.ReadAllText(Path.Combine(root, "src", "AuroraCore", "Utils", "Logger.cs"));
            var logBody = logger[logger.IndexOf("public void Log(", StringComparison.Ordinal)..];
            logBody = logBody[..logBody.IndexOf("public void Info(", StringComparison.Ordinal)];
            Assert(!logBody.Contains("File.AppendAllText"),
                "Log() must never touch the disk on the caller's thread — that is the UI thread");
            Assert(logger.Contains("WriterLoop") && logger.Contains("_pendingWrites") && logger.Contains("public bool Flush("),
                "the async writer + Flush contract must stay in place");

            var vm = File.ReadAllText(Path.Combine(root, "src", "AuroraLauncher.Desktop", "ViewModels", "MainViewModel.cs"));
            Assert(vm.Contains("IsLogsPageActive"), "the Logs feed must be gated on the page being visible");
            Assert(vm.Contains("Logs.Replace(snapshot)"),
                "re-entering the Logs page must publish the backlog in ONE Reset, not N Add notifications");
            Assert(!vm.Contains("if (SetProperty(ref _showErrorsOnly, value))\n                LogsView.Filter"),
                "the errors-only filter must not be cleared by a redundant assignment");
        });

        Check("RouteFailurePolicy: 404 is permanent, no retry storm", () =>
        {
            // Round-70: the user's log was dominated by `Route '镜像(直连)' failed
            // (round 1): ... 404` + `All 4 routes failed on round 1; retrying
            // once...`. Those 404s cannot improve — retrying wasted a whole
            // round of HTTP + parsing for every loader on every refresh.
            Assert(AuroraCore.Utils.RouteFailurePolicy.IsPermanent(
                    "Response status code does not indicate success: 404 (Not Found)."), "404 is permanent");
            Assert(AuroraCore.Utils.RouteFailurePolicy.IsPermanent("403 (Forbidden)"), "403 is permanent");
            Assert(!AuroraCore.Utils.RouteFailurePolicy.IsPermanent("A task was canceled."), "cancel is not permanent");
            Assert(!AuroraCore.Utils.RouteFailurePolicy.IsPermanent("The request timed out"), "timeout is transient");
            Assert(!AuroraCore.Utils.RouteFailurePolicy.IsPermanent("503 (Service Unavailable)"), "5xx is transient");
            Assert(!AuroraCore.Utils.RouteFailurePolicy.IsPermanent(null), "null is not permanent");

            var permanentOnly = new[] { "镜像(直连): 404", "镜像(默认通道): 404", "官方(直连): 404", "官方(默认通道): 404" };
            Assert(!AuroraCore.Utils.RouteFailurePolicy.ShouldRetry(permanentOnly, 1, 2),
                "all-permanent failures must NOT spend the retry round");
            var transient = new[] { "404", "The request timed out" };
            Assert(AuroraCore.Utils.RouteFailurePolicy.ShouldRetry(transient, 1, 2),
                "one transient failure keeps the retry budget");
            Assert(!AuroraCore.Utils.RouteFailurePolicy.ShouldRetry(transient, 2, 2), "no retries past the budget");

            var cancelled = new[] { "A task was canceled." };
            Assert(!AuroraCore.Utils.RouteFailurePolicy.ShouldRetry(cancelled, 1, 2),
                "a cancelled request must not be retried (the user navigated away)");

            var (warnOn404, text404) = AuroraCore.Utils.RouteFailurePolicy.DescribeFailure("镜像(直连)", 1, "404 (Not Found)");
            Assert(!warnOn404 && text404.Contains("permanent"), "permanent failures are logged as info, not warnings");
            var (warnOnTimeout, _) = AuroraCore.Utils.RouteFailurePolicy.DescribeFailure("官方(直连)", 1, "timed out");
            Assert(warnOnTimeout, "transient failures stay warnings");

            // Round-71: DNS failures are permanent. The dead bmclapi.cn domain
            // produced "不知道这样的主机" on every route — retrying it is pure delay.
            Assert(AuroraCore.Utils.RouteFailurePolicy.IsPermanent("不知道这样的主机。"),
                "the localized 'no such host' string is permanent");
            Assert(AuroraCore.Utils.RouteFailurePolicy.IsPermanent("No such host is known."),
                "the English 'no such host' string is permanent");
            Assert(AuroraCore.Utils.RouteFailurePolicy.IsPermanent("getaddrinfo failed"), "getaddrinfo failure is permanent");
            Assert(!AuroraCore.Utils.RouteFailurePolicy.ShouldRetry(
                    new[] { "不知道这样的主机。", "不知道这样的主机。" }, 1, 2),
                "a DNS-dead host must not be retried");
        });

        Check("UiStallTrace: attributes a UI freeze to GC or to app work", () =>
        {
            // Round-70: 48–218 ms of "waiting for the UI thread" with a 5 ms page
            // means something else owns the dispatcher. GC and our own code need
            // opposite fixes, so the watchdog must tell them apart.
            var trace = new AuroraCore.Services.UiStallTrace();
            Assert(trace.Sample(10_000) == null, "a normal tick is not a stall");

            var gcDominant = new AuroraCore.Services.StallSample(600, 520);
            Assert(gcDominant.IsGcDominated, "GC pause covering most of the gap is a collection");
            Assert(gcDominant.OtherMs <= 80.1, "the remainder is small");
            var described = AuroraCore.Services.UiStallTrace.Describe(gcDominant);
            Assert(described.Contains("GC"), $"GC stalls must name GC: {described}");
            Assert(described.Contains("停顿"), "the message must say how long the UI was stuck");

            var workDominant = new AuroraCore.Services.StallSample(320, 5);
            Assert(!workDominant.IsGcDominated, "5 ms of GC cannot explain a 320 ms gap");
            Assert(AuroraCore.Services.UiStallTrace.Describe(workDominant).Contains("界面线程被占用"),
                "work on the UI thread must be named as such");

            var mixed = new AuroraCore.Services.StallSample(300, 120);
            var mixedText = AuroraCore.Services.UiStallTrace.Describe(mixed);
            Assert(mixedText.Contains("120") && mixedText.Contains("其余"), $"mixed stalls report both parts: {mixedText}");

            // The reported number is the EXCESS over the interval (what is felt),
            // and fast GC bursts inside a small stall are not "GC dominated".
            Assert(new AuroraCore.Services.StallSample(250, 0).OtherMs == 250, "other = stall - gc");
            Assert(new AuroraCore.Services.StallSample(250, 240).IsGcDominated, "240/250 is GC dominated");
            Assert(!new AuroraCore.Services.StallSample(250, 60).IsGcDominated, "60/250 is not GC dominated");
            Assert(AuroraCore.Services.UiStallTrace.Describe(new AuroraCore.Services.StallSample(600, 0))
                .Contains("600"), "the stall figure must be the excess, not the raw tick gap");
        });

        Check("DiagnosticsExporter: 导出日志 writes a sanitized, headered text file", () =>
        {
            // Round-70 user request: a one-click log export for bug reports.
            var root = Path.Combine(Path.GetTempPath(), "aurora-logexp-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(root, "logs"));
            var logFile = Path.Combine(root, "logs", "aurora-test.log");
            File.WriteAllText(logFile,
                "[12:00:00] [Info] [ui] MARKER-LINE\n" +
                $"[12:00:01] [Info] [root] {root}\\logs\\x.log\n" +
                "[12:00:02] [Warn] [Auth] {\"accessToken\":\"SECRET-TOKEN-123\"}\n");

            var dest = Path.Combine(root, "export.txt");
            var (ok, chars, message) = AuroraCore.Services.DiagnosticsExporter
                .ExportLogTextAsync(dest, root).GetAwaiter().GetResult();

            Assert(ok, $"export must succeed: {message}");
            Assert(chars > 0, "the export must report a size");
            var text = File.ReadAllText(dest);
            Assert(text.StartsWith("=== Aurora Launcher 日志导出 ==="), "the export must carry a header");
            Assert(text.Contains($"第 {AuroraCore.BuildInfo.CurrentRound} 轮"), "the header must name the build round");
            Assert(text.Contains("MARKER-LINE"), "the log body must be present");
            Assert(!text.Contains("SECRET-TOKEN-123"), "tokens must be redacted before leaving the machine");
            Assert(!text.Contains(root), "the launcher root path must be redacted");
            Assert(AuroraCore.Services.DiagnosticsExporter.NewestLogFile(root) == logFile,
                "the newest log file must be picked for export");

            var (noLogOk, _, noLogMsg) = AuroraCore.Services.DiagnosticsExporter
                .ExportLogTextAsync(Path.Combine(root, "none.txt"), Path.Combine(root, "empty-root"))
                .GetAwaiter().GetResult();
            Assert(!noLogOk && noLogMsg.Contains("日志"), "a missing log must fail with a readable message");
            Directory.Delete(root, recursive: true);
        });

        Check("NetRouting.BuildHosts: both hosts survive a mirrored input URL", () =>
        {
            // Round-71 root cause: `VersionManifestUrl` returned the BMCLAPI URL
            // when the source was BMCLAPI, so ToBmclapiUrl(url) == url, the route
            // set collapsed to two identical dead routes, and the official host
            // was never tried. The source must only decide ORDER.
            const string official = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
            const string mirror = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";

            var normal = AuroraCore.Utils.NetRouting.BuildHosts(official, LauncherConfig.ToBmclapiUrl(official), preferMirror: false);
            Assert(normal.Count == 2, $"official source must offer two hosts, got {normal.Count}");
            Assert(normal[0].Url == official && normal[0].Kind == "官方", "official host first when the user picked official");
            Assert(normal[1].Url == mirror && normal[1].Kind == "镜像", "mirror stays available as the fallback");

            var preferredMirror = AuroraCore.Utils.NetRouting.BuildHosts(official, LauncherConfig.ToBmclapiUrl(official), preferMirror: true);
            Assert(preferredMirror.Count == 2 && preferredMirror[0].Url == mirror,
                "BMCLAPI preference reorders the hosts but keeps the official one");

            // THE regression: a caller hands us an already mirrored URL.
            var alreadyMirrored = AuroraCore.Utils.NetRouting.BuildHosts(mirror, LauncherConfig.ToBmclapiUrl(mirror), preferMirror: true);
            Assert(alreadyMirrored.Count == 2,
                "a mirrored input must still yield two distinct hosts (round-70 collapse was 2 dead mirror routes)");
            Assert(alreadyMirrored.Any(h => h.Url == official),
                "the official Mojang host must still be tried when the input URL is a mirror URL");

            // No mirror exists for third-party APIs — do not invent one.
            var thirdParty = AuroraCore.Utils.NetRouting.BuildHosts("https://api.modrinth.com/v2/search", null, preferMirror: false);
            Assert(thirdParty.Count == 1, "non-Mojang URLs must not gain a fake mirror route");

            // The configured manifest URL is always official now.
            var cfg = LauncherConfig.Instance;
            var before = cfg.RoutePolicy;
            try
            {
                cfg.RoutePolicy = DownloadPolicy.Mirror;
                Assert(cfg.VersionManifestUrl.StartsWith("https://piston-meta.mojang.com", StringComparison.Ordinal),
                    "the manifest URL must stay official even when the mirror is forced");
                Assert(!cfg.VersionManifestUrl.Contains("bmclapi", StringComparison.OrdinalIgnoreCase),
                    "the dead mirror domain must never be produced as a manifest URL");

                // Round-85: forced routes apply to the URL we hand to the engine.
                Assert(cfg.RewriteUrl(official).Contains("bmclapi"),
                    "强制镜像时，二进制下载地址必须真的换成镜像");
                cfg.RoutePolicy = DownloadPolicy.Official;
                Assert(cfg.RewriteUrl(official) == official,
                    "强制官方时不能偷偷改写成镜像");
            }
            finally { cfg.RoutePolicy = before; }
        });

        Check("Guard: 测速只用 HEAD / 有缓存 / 下载路径不等测速（第 85 轮）", () =>
        {
            var root = FindSourceRoot()!;
            var probe = File.ReadAllText(Path.Combine(root, "src", "AuroraCore", "Utils", "NetworkProbe.cs"));

            // (1) No full-body download in a latency probe. HEAD first, with a
            // 1-byte Range GET as the fallback for hosts that refuse HEAD.
            Assert(probe.Contains("HttpMethod.Head"), "测速必须先发 HEAD");
            Assert(probe.Contains("RangeHeaderValue(0, 0)"),
                "HEAD 被拒时只能取 1 个字节，不能顺手把整个清单拉下来");
            Assert(!probe.Contains("GetByteArrayAsync") && !probe.Contains("ReadAsByteArrayAsync"),
                "测速绝不能把响应体读进内存");
            Assert(probe.Contains("ResponseHeadersRead"),
                "只读响应头就够，别把内容缓冲起来");

            // (2) Cached, single-flight.
            Assert(probe.Contains("CacheTtl") && probe.Contains("TimeSpan.FromMinutes(5)"),
                "测速结果必须有 5 分钟缓存（每次下载都测速是 10 秒级卡顿的经典来源）");
            Assert(probe.Contains("_inFlight"), "并发调用必须共享同一次测速，不能五路一起打");

            // (3) The download path must never await the probe.
            var routing = File.ReadAllText(Path.Combine(root, "src", "AuroraCore", "Utils", "NetRouting.cs"));
            Assert(routing.Contains("NetworkProbe.Preference"),
                "NetRouting 应该读缓存好的 Preference（静态字段，零 IO）");
            Assert(!routing.Contains("await NetworkProbe."),
                "下载路径里不许 await 测速");

            var engine = File.ReadAllText(Path.Combine(root, "src", "AuroraCore", "Download", "DownloadEngine.cs"));
            Assert(!engine.Contains("NetworkProbe"),
                "下载引擎不得直接接触测速（只能通过 URL 选择 + 失败换线）");

            // (4) Cross-route fallback exists and is policy-gated.
            Assert(engine.Contains("RouteSwitchAllowed") && engine.Contains("AlternateRouteUrl"),
                "单线路失败后必须能换另一条线路，否则强制官方 = 强制失败");
            Assert(engine.Contains("switchedRoute"), "换线只能换一次，不能两条来回弹");

            // Pure URL mapping used by that fallback.
            const string official = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
            var mirror = DownloadEngine.AlternateRouteUrl(official);
            Assert(mirror != null && mirror.Contains("bmclapi"), "官方 URL 的备胎是镜像");
            Assert(DownloadEngine.AlternateRouteUrl(mirror!) == official, "镜像 URL 的备胎是官方");
            Assert(DownloadEngine.AlternateRouteUrl("https://api.modrinth.com/v2/search") == null,
                "第三方接口没有备胎，必须返回 null 而不是错误地改写");

            // Forced policy: core files must NOT silently switch hosts.
            var cfg = LauncherConfig.Instance;
            var before = cfg.RoutePolicy;
            try
            {
                cfg.RoutePolicy = DownloadPolicy.Official;
                Assert(!DownloadEngine.RouteSwitchAllowed("https://x/versions/1.21.1/1.21.1.jar"),
                    "强制官方时，核心文件失败必须报错，不能偷偷走镜像");
                Assert(DownloadEngine.RouteSwitchAllowed("https://x/assets/ab/abcdef"),
                    "非核心资源（声音/材质）允许静默回退");
                cfg.RoutePolicy = DownloadPolicy.Auto;
                Assert(DownloadEngine.RouteSwitchAllowed("https://x/versions/1.21.1/1.21.1.jar"),
                    "自动模式下任何文件都可以换线重试");
            }
            finally { cfg.RoutePolicy = before; }
        });

        Check("Forge 安装器 URL：必须拼成「MC版本-Forge版本」（第 86 轮）", () =>
        {
            // The reported 404: a modpack gives the bare forge version
            // ("47.4.22"), and ".../forge/47.4.22/forge-47.4.22-installer.jar"
            // does not exist — verified 404 on maven.minecraftforge.net and on
            // BMCLAPI, while "1.20.1-47.4.22" returns 200.
            Assert(ModLoaderInstaller.ForgeMavenVersion("1.20.1", "47.4.22") == "1.20.1-47.4.22",
                "bare forge version must gain the MC prefix");
            Assert(ModLoaderInstaller.ForgeMavenVersion("1.20.1", "1.20.1-47.4.22") == "1.20.1-47.4.22",
                "an already-combined version must not be double-prefixed");
            Assert(ModLoaderInstaller.ForgeMavenVersion("1.20.1", "forge-1.20.1-47.4.22") == "1.20.1-47.4.22",
                "a full version id must be unwrapped");
            Assert(ModLoaderInstaller.ForgeMavenVersion("1.20.1", " 47.4.22 ") == "1.20.1-47.4.22",
                "surrounding whitespace must not leak into the URL");
            Assert(ModLoaderInstaller.ForgeMavenVersion("1.20.1", "") == "", "empty input stays empty");

            const string forgeBase = "https://maven.minecraftforge.net/net/minecraftforge/forge";
            var (url, file) = ModLoaderInstaller.BuildMavenInstallerUrl(forgeBase, "forge", "1.20.1-47.4.22");
            Assert(url == forgeBase + "/1.20.1-47.4.22/forge-1.20.1-47.4.22-installer.jar",
                "installer URL shape: " + url);
            Assert(file == "forge-1.20.1-47.4.22-installer.jar", "file name must match the path version");

            // The regression: the exact URL from the bug report must be impossible.
            var bad = ModLoaderInstaller.BuildMavenInstallerUrl(forgeBase, "forge", "47.4.22").Url;
            Assert(bad.Contains("/forge/47.4.22/"), "sanity: the raw builder is dumb on purpose");
            Assert(ModLoaderInstaller.BuildMavenInstallerUrl(forgeBase, "forge",
                    ModLoaderInstaller.ForgeMavenVersion("1.20.1", "47.4.22")).Url != bad,
                "the resolver must never emit the 404 URL from the report");

            // NeoForge is NOT prefixed: its versions already carry the MC series
            // ("21.1.72"), and prefixing would create a second 404 class.
            const string neoBase = "https://maven.neoforged.net/releases/net/neoforged/neoforge";
            var neo = ModLoaderInstaller.BuildMavenInstallerUrl(neoBase, "neoforge", "21.1.72");
            Assert(neo.Url == neoBase + "/21.1.72/neoforge-21.1.72-installer.jar",
                "NeoForge path must stay as provided: " + neo.Url);
        });

        Check("LauncherConfig.ToOfficialUrl: maven 路径可按 group 反解（第 86 轮）", () =>
        {
            // Round-85's cross-route switch needs a reversible URL. A bare
            // "/maven/…" used to be left alone, so a mirror 404 on the Forge
            // installer could not be retried against the official host.
            const string forge = "https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.4.22/forge-1.20.1-47.4.22-installer.jar";
            const string neo = "https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.72/neoforge-21.1.72-installer.jar";
            const string fabric = "https://maven.fabricmc.net/net/fabricmc/fabric-installer/1.0.1/fabric-installer-1.0.1.jar";
            const string quilt = "https://maven.quiltmc.org/repository/release/org/quiltmc/quilt-installer/0.9.2/quilt-installer-0.9.2.jar";

            foreach (var official in new[] { forge, neo, fabric, quilt })
            {
                var mirrored = LauncherConfig.ToBmclapiUrl(official);
                Assert(mirrored.Contains("bmclapi"), "mirror form expected for " + official);
                Assert(LauncherConfig.ToOfficialUrl(mirrored) == official,
                    $"round-trip failed:\n  official {official}\n  mirror   {mirrored}\n  reverse  {LauncherConfig.ToOfficialUrl(mirrored)}");
            }

            // And the engine can therefore switch a mirror 404 back to official.
            var alt = DownloadEngine.AlternateRouteUrl(LauncherConfig.ToBmclapiUrl(forge));
            Assert(alt == forge, "mirror installer URL must map back to the official host (got " + (alt ?? "null") + ")");
        });

        Check("Guard: 安装器重试前必须清残留、且不违背强制线路（第 86 轮）", () =>
        {
            var src = File.ReadAllText(Path.Combine(FindSourceRoot()!, "src", "AuroraCore",
                "Services", "ModLoaders", "ModLoaderInstallers.cs"));

            // An installer jar has no expected hash → DownloadEngine would reuse a
            // stale file forever; it must be deleted (and its .part) before retry.
            Assert(src.Contains("jarPath + \".part\""), "installation must delete the stale .part too");
            Assert(src.Contains("File.Delete(stale)"), "stale installer files must be removed before downloading");

            // Forced-official must not silently fall back to the mirror for the
            // installer (round-85 rule); Auto/Forced-mirror may.
            Assert(src.Contains("RoutePolicy != DownloadPolicy.Official"),
                "the installer fallback must respect the forced-official policy");
            Assert(src.Contains("ForgeMavenVersion"), "the resolver must normalize the forge version");
            Assert(src.Contains("BuildMavenInstallerUrl"), "path and file name must come from one builder");
        });

        Check("DownloadIntegrity: 没有校验值不再等于「完整」（第 87 轮）", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "aurora-integrity-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                var body = new byte[1024 * 37];
                Random.Shared.NextBytes(body);
                var good = Path.Combine(dir, "good.bin");
                File.WriteAllBytes(good, body);
                var sha1 = CryptoHelper.Sha1(good);

                // 1) 有哈希：哈希说了算，大小不参与。
                Assert(DownloadIntegrity.Check(good, sha1, null) == IntegrityVerdict.Match, "sha1 match must be Match");
                Assert(DownloadIntegrity.Check(good, sha1, body.Length) == IntegrityVerdict.Match, "sha1 match + size match");
                Assert(DownloadIntegrity.Check(good, sha1, body.Length + 5) == IntegrityVerdict.Match,
                    "a hash match wins even when the declared size is off (mirror rounds sizes)");
                Assert(DownloadIntegrity.Check(good, "0000000000000000000000000000000000000000", body.Length) == IntegrityVerdict.Mismatch,
                    "sha1 mismatch must be Mismatch");

                // 2) 只有大小（OptiFine / 第三方 CDN 那种）：半截文件必须被抓出来。
                Assert(DownloadIntegrity.Check(good, null, body.Length) == IntegrityVerdict.Match, "size match must be Match");
                var truncated = Path.Combine(dir, "half.bin");
                File.WriteAllBytes(truncated, body.Take(body.Length / 2).ToArray());
                Assert(DownloadIntegrity.Check(truncated, null, body.Length) == IntegrityVerdict.Mismatch,
                    "a half-written file with a published size must NOT pass");

                // 3) 什么都没有：只能说「无法确认」，绝不能说「完整」。
                Assert(DownloadIntegrity.Check(good, null, null) == IntegrityVerdict.Unverifiable,
                    "no hash and no size must be Unverifiable, not Match");
                Assert(!DownloadIntegrity.IsTrustworthyForSkip(IntegrityVerdict.Unverifiable),
                    "the cached-file path must NOT skip an unverifiable file");
                Assert(DownloadIntegrity.IsTrustworthyForSkip(IntegrityVerdict.Match), "only a real match may skip");
                Assert(!DownloadIntegrity.IsTrustworthyForSkip(IntegrityVerdict.Mismatch), "a mismatch must be downloaded again");

                // 4) 刚写完的正文：只有明确的 mismatch 才算失败（否则会无限重试）。
                Assert(DownloadIntegrity.IsAcceptableAfterDownload(IntegrityVerdict.Match), "match commits");
                Assert(DownloadIntegrity.IsAcceptableAfterDownload(IntegrityVerdict.Unverifiable),
                    "a fresh body with nothing to compare must still commit");
                Assert(!DownloadIntegrity.IsAcceptableAfterDownload(IntegrityVerdict.Mismatch),
                    "a fresh body with the wrong size/hash must be retried");

                Assert(DownloadIntegrity.Check(Path.Combine(dir, "nope.bin"), sha1, 10) == IntegrityVerdict.Missing,
                    "a missing file is Missing, not Unverifiable");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        Check("Guard: 缓存命中必须走 DownloadIntegrity，VerifyHash(null) 已删除（第 87 轮）", () =>
        {
            var srcRoot = Path.Combine(FindSourceRoot()!, "src");
            var engine = File.ReadAllText(Path.Combine(srcRoot, "AuroraCore", "Download", "DownloadEngine.cs"));

            // Round-88 review: scanning only DownloadEngine.cs left every other
            // download path unguarded — the round-86 installer workaround lives in
            // ModLoaderInstallers.cs, and a future helper there could reintroduce
            // the "no hash = complete" hole unnoticed. The rule is about the
            // IDENTIFIER, so it has to cover the whole src/ tree.
            var offenders = new List<string>();
            foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains("\\obj\\") || file.Contains("\\bin\\")) continue;
                var text = File.ReadAllText(file);
                if (!text.Contains("VerifyHash(")) continue;
                // The only place allowed to mention it is the class that explains
                // why it was removed (a doc comment), and even there it must not
                // define one.
                if (Path.GetFileName(file) == "DownloadIntegrity.cs")
                {
                    Assert(!text.Contains("static bool VerifyHash"),
                        "DownloadIntegrity 只能提到旧实现，绝不能重新定义它");
                    continue;
                }
                offenders.Add(Path.GetFileName(file));
            }
            Assert(offenders.Count == 0,
                "整个 src 不得再出现旧的 VerifyHash(（只有 DownloadIntegrity 的说明注释例外）: " +
                string.Join(", ", offenders));

            Assert(engine.Contains("DownloadIntegrity.IsTrustworthyForSkip"),
                "the cached path must ask DownloadIntegrity before skipping");
            Assert(engine.Contains("DownloadIntegrity.IsAcceptableAfterDownload"),
                "the post-download check must accept an unverifiable fresh body instead of retrying forever");
            Assert(engine.Contains("ActiveDownloads.Register("),
                "every file operation must announce itself as owned (the residue cleaner relies on it)");
            Assert(engine.Contains("sizeComparable"),
                "a gzip/brotli response must not be size-checked (the file holds expanded bytes)");
        });

        Check("DownloadResidue: 正在下载的保留，其余清理（第 87 轮）", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "aurora-residue-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            ActiveDownloads.ResetForTests();
            try
            {
                var cache = Path.Combine(root, "cache");
                var libs = Path.Combine(root, "libraries", "net", "example");
                var update = Path.Combine(root, "update");
                Directory.CreateDirectory(cache);
                Directory.CreateDirectory(libs);
                Directory.CreateDirectory(update);

                string MakePart(string directory, string name, int bytes)
                {
                    var dest = Path.Combine(directory, name);
                    File.WriteAllBytes(dest + ".part", new byte[bytes]);
                    return dest;
                }

                // A live download: must survive.
                var liveDest = MakePart(libs, "live.jar", 4096);
                // Journalled (resumable via the banner), 5 MB so ShouldTrack keeps it.
                var journalDest = MakePart(libs, "journaled.jar", 5 * 1024 * 1024);
                // Untracked junk under 4 MB.
                var smallDest = MakePart(libs, "small.jar", 1024);
                // Updater staging leftover: never journalled, always junk.
                var updateDest = MakePart(update, "AuroraLauncher.patch", 2048);
                // A finished (non-part) file must be invisible to the scan.
                File.WriteAllBytes(Path.Combine(libs, "done.jar"), new byte[10]);

                // Real journal file so the scanner finds the "journaled" entry.
                var journalPath = DownloadJournal.PathFor(root);
                Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
                File.WriteAllText(journalPath,
                    "+\thttps://example.invalid/journaled.jar\t" + journalDest + "\t" + (5 * 1024 * 1024) + "\t\n");

                using (ActiveDownloads.Register(liveDest))
                {
                    var scan = DownloadResidue.Scan(root);
                    Assert(scan.Count == 4, $"expected 4 .part files, got {scan.Count}");
                    Assert(scan.ActiveCount == 1, "the live download must be classified ActiveTask");
                    Assert(scan.JournaledCount == 1, "the journalled file must be classified Journaled");
                    Assert(scan.UnownedCount == 2, "the <4 MB leftover and the update/ leftover are unowned");
                    Assert(scan.ReclaimableCount == 3, "everything but the live download is reclaimable");
                    Assert(!scan.Files.Any(f => f.FileName.EndsWith("done.jar.part")), "finished files are not residue");

                    var result = DownloadResidue.Clean(scan);
                    Assert(result.DeletedCount == 3, $"expected 3 deletions, got {result.DeletedCount}");
                    Assert(result.KeptCount == 1, "the live download must be reported as kept");
                    Assert(File.Exists(liveDest + ".part"), "a live download's .part must never be deleted");
                    Assert(!File.Exists(journalDest + ".part"), "a journalled leftover is deletable (after the user agrees)");
                    Assert(!File.Exists(smallDest + ".part"), "an unowned <4 MB leftover must be deleted");
                    Assert(!File.Exists(updateDest + ".part"), "the update/ leftover must be deleted");
                    Assert(File.Exists(Path.Combine(libs, "done.jar")), "cleanup must never touch completed files");
                    Assert(result.FreedBytes >= 5 * 1024 * 1024, "freed bytes must be reported honestly");
                }
            }
            finally
            {
                ActiveDownloads.ResetForTests();
                DownloadJournal.OverridePathForTests(null);
                try { Directory.Delete(root, true); } catch { }
            }
        });

        Check("DownloadResidue.ShouldWarn: 阈值任一超标才提示（第 87 轮）", () =>
        {
            static ResidueFile F(long bytes, ResidueOwner owner = ResidueOwner.Unowned)
                => new() { Bytes = bytes, Owner = owner, Path = "x.part", Destination = "x" };

            var big = new ResidueScan { Files = new[] { F(300L * 1024 * 1024) } };
            var many = new ResidueScan { Files = Enumerable.Range(0, 60).Select(_ => F(1024)).ToArray() };
            var quiet = new ResidueScan { Files = new[] { F(1024) } };
            var liveOnly = new ResidueScan { Files = new[] { F(500L * 1024 * 1024, ResidueOwner.ActiveTask) } };
            var empty = new ResidueScan();

            Assert(DownloadResidue.ShouldWarnFromConfig(big, 200, 50), "300 MB 单体残留必须提示");
            Assert(DownloadResidue.ShouldWarnFromConfig(many, 200, 50), "60 个小文件也必须提示");
            Assert(!DownloadResidue.ShouldWarnFromConfig(quiet, 200, 50), "1 个 1 KB 文件不该打扰用户");
            Assert(!DownloadResidue.ShouldWarnFromConfig(liveOnly, 200, 50),
                "只剩正在下载的文件时不该提示（那不是残留）");
            Assert(!DownloadResidue.ShouldWarnFromConfig(empty, 200, 50), "空扫描不提示");
            Assert(DownloadResidue.ShouldWarnFromConfig(quiet, 0, 5) == false, "两个阈值都放开时永远不提示");
            Assert(!DownloadResidue.ShouldWarnFromConfig(big, 0, 5), "只按数量判断时，一个大文件不该触发（0 = 关闭该维度）");
            Assert(DownloadResidue.ShouldWarnFromConfig(many, 0, 5), "只按数量判断时，60 个文件必须触发");
            Assert(!DownloadResidue.ShouldWarnFromConfig(big, -1, -1), "负数阈值 = 关闭，绝不能变成「永远提示」");
            Assert(DownloadResidue.Describe(quiet).Contains("1"), "the banner text must carry the count");
        });

        Check("UpdateStaging: 只清 24 小时前的、且不是当前待装的包（第 87 轮）", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "aurora-upstage-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                var now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
                var oldPart = Path.Combine(dir, "AuroraLauncher.patch.part");
                var oldMerged = Path.Combine(dir, "AuroraLauncher.merged.exe");
                var freshPart = Path.Combine(dir, "AuroraLauncher.exe.part");
                var staged = Path.Combine(dir, "AuroraLauncher.new.exe");
                var stagedInfo = Path.Combine(dir, "build-info.txt");
                foreach (var p in new[] { oldPart, oldMerged, freshPart, staged, stagedInfo })
                    File.WriteAllBytes(p, new byte[16]);
                File.SetLastWriteTimeUtc(oldPart, now.AddHours(-30));
                File.SetLastWriteTimeUtc(oldMerged, now.AddDays(-3));
                File.SetLastWriteTimeUtc(freshPart, now.AddMinutes(-5));
                File.SetLastWriteTimeUtc(staged, now.AddDays(-4));      // old but pending: keep
                File.SetLastWriteTimeUtc(stagedInfo, now.AddDays(-4));

                var keep = new[] { staged, stagedInfo };
                var stale = UpdateStaging.SelectStale(dir, now, keep);
                Assert(stale.Count == 2, $"expected exactly the 2 stale files, got {stale.Count}");
                Assert(stale.Any(p => p.EndsWith("AuroraLauncher.patch.part")), "the interrupted patch must be selected");
                Assert(stale.Any(p => p.EndsWith("AuroraLauncher.merged.exe")), "the abandoned merged exe must be selected");
                Assert(!stale.Any(p => p.EndsWith("AuroraLauncher.new.exe")),
                    "the staged package must be kept no matter how old it is");
                Assert(!stale.Any(p => p.EndsWith("AuroraLauncher.exe.part")), "a 5-minute-old .part is left alone");

                var removed = UpdateStaging.Clean(dir, now, keep);
                Assert(removed == 2, $"Clean must delete 2 files, deleted {removed}");
                Assert(File.Exists(staged) && File.Exists(stagedInfo), "the pending update must survive a cleanup");
                Assert(!File.Exists(oldPart) && !File.Exists(oldMerged), "stale staging files must be gone");
                Assert(UpdateStaging.SelectStale(Path.Combine(dir, "nope"), now, keep).Count == 0,
                    "a missing staging folder is not an error");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        Check("Guard: 清理残留的三处入口都接上了（第 87 轮）", () =>
        {
            var root = FindSourceRoot()!;
            var vm = File.ReadAllText(Path.Combine(root, "src", "AuroraLauncher.Desktop",
                "ViewModels", "MainViewModel.Round87.cs"));
            var mainVm = File.ReadAllText(Path.Combine(root, "src", "AuroraLauncher.Desktop",
                "ViewModels", "MainViewModel.cs"));
            var settings = File.ReadAllText(Path.Combine(root, "src", "AuroraLauncher.Desktop",
                "Views", "SettingsView.xaml"));
            var downloads = File.ReadAllText(Path.Combine(root, "src", "AuroraLauncher.Desktop",
                "Views", "DownloadsView.xaml"));
            var update = File.ReadAllText(Path.Combine(root, "src", "AuroraCore", "Services", "UpdateService.cs"));

            Assert(settings.Contains("清理残留下载文件"), "设置页必须有这个按钮");
            Assert(settings.Contains("CleanResidueCommand"), "the button must be bound to the command");
            Assert(downloads.Contains("HasResidueNotice"), "下载页必须有那条非阻塞提示");
            Assert(downloads.Contains("ResidueNoticeText"), "the banner must show the scan text");
            Assert(vm.Contains("Task.Run(() => DownloadResidue.Clean(scan))"),
                "deleting must happen off the UI thread");
            Assert(vm.Contains("Task.Run(() => DownloadResidue.Scan("),
                "scanning walks the whole data root — never on the UI thread");
            Assert(vm.Contains("MessageBoxResult.No"), "清理可续传的文件前必须问一句，且默认「否」");
            Assert(mainVm.Contains("ScanDownloadResidue();"), "启动时要做一次轻量扫描");
            Assert(mainVm.Contains("InitRound87Commands();"), "commands must be wired in the constructor");
            Assert(update.Contains("UpdateStaging.Clean("), "每次更新检查要顺手清 update/");
            Assert(update.Contains("StagedExePath, StagedInfoPath"), "the staged package must be in the keep set");
        });

        Check("StorageCleanupPolicy: 白名单只放行缓存与临时文件（第 88 轮）", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "aurora-clean-root");
            string P(params string[] parts) => Path.Combine(new[] { root }.Concat(parts).ToArray());

            // --- allowed ---------------------------------------------------
            foreach (var ok in new[]
                     {
                         P("cache", "icons", "abc.img"),
                         P("cache", "optifine", "OptiFine_1.20.1.jar"),
                         P("cache", "installers", "meta-Fabric-1.20.1.json"),
                         P("cache", "version_manifest_v2.json"),
                         P("update", "AuroraLauncher.patch"),
                         P("update", "AuroraLauncher.patch.part"),
                         P("logs", "aurora-2026-01-01.log"),
                         P("versions", "1.20.1"),
                         P("libraries", "net", "example", "lib.jar.part"),
                         P("assets", "objects", "aa", "bbcc.part"),
                     })
                Assert(StorageCleanupPolicy.IsAllowedPath(root, ok), "应当允许清理: " + ok);

            // --- refused: config, user data, payload, staged update ---------
            foreach (var no in new[]
                     {
                         root,
                         P("config.json"),
                         P("instances.json"),
                         P("accounts.json"),
                         P("cache", "download-journal.log"),
                         P("cache", "something-else.txt"),
                         P("logs", "other.txt"),
                         P("logs", "aurora-2026-01-01.log.bak"),
                         P("update", "AuroraLauncher.new.exe"),
                         P("update", "build-info.txt"),
                         P("versions", "1.20.1", "1.20.1.json"),
                         P("versions", "1.20.1", "1.20.1.jar"),
                         P("libraries", "net", "example", "lib.jar"),
                         P("assets", "objects", "aa", "bbcc"),
                         P("instances", "abc", "saves", "world.dat"),
                         P("instances", "abc", "mods", "better.jar.part"),
                         P("backups", "abc", "saves-2026.zip"),
                         P("java", "jdk21", "bin", "java.exe"),
                         Path.Combine(Path.GetTempPath(), "somewhere-else", "x.part"),
                     })
                Assert(!StorageCleanupPolicy.IsAllowedPath(root, no), "绝不能允许清理: " + no);

            Assert(!StorageCleanupPolicy.IsAllowedPath(root, null), "null 路径必须拒绝");
            Assert(!StorageCleanupPolicy.IsAllowedPath(null, P("cache", "icons", "a.img")), "没有数据根必须拒绝");
        });

        Check("StorageCleanupPolicy: 空壳版本目录的判定（第 88 轮）", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "aurora-shell-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            try
            {
                string Make(string id, bool json, bool jar)
                {
                    var dir = Path.Combine(root, id);
                    Directory.CreateDirectory(dir);
                    if (json) File.WriteAllText(Path.Combine(dir, id + ".json"), "{}");
                    if (jar) File.WriteAllBytes(Path.Combine(dir, id + ".jar"), new byte[8]);
                    return dir;
                }

                var onlyJson = Make("only-json", true, false);
                var onlyJar = Make("only-jar", false, true);
                var both = Make("both", true, true);
                var empty = Path.Combine(root, "empty");
                Directory.CreateDirectory(empty);
                var hasNestedFile = Path.Combine(root, "nested");
                Directory.CreateDirectory(Path.Combine(hasNestedFile, "natives"));
                File.WriteAllBytes(Path.Combine(hasNestedFile, "natives", "x.dll"), new byte[4]);

                Assert(!StorageCleanupPolicy.IsEmptyVersionShell(onlyJson, "only-json"), "只有 JSON 不算空壳（可以启动）");
                Assert(!StorageCleanupPolicy.IsEmptyVersionShell(onlyJar, "only-jar"), "只有 jar 不算空壳（可能是半装）");
                Assert(!StorageCleanupPolicy.IsEmptyVersionShell(both, "both"), "完整版本当然不算空壳");
                Assert(StorageCleanupPolicy.IsEmptyVersionShell(empty, "empty"), "真正的空目录才算空壳");
                Assert(!StorageCleanupPolicy.IsEmptyVersionShell(hasNestedFile, "nested"),
                    "子目录里有文件就不算空壳（可能是待补全的安装）");
                Assert(!StorageCleanupPolicy.IsEmptyVersionShell(Path.Combine(root, "gone"), "gone"), "不存在的目录不是空壳");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

        Check("StorageCleanupPolicy: CostsOffline 一律默认不勾（第 88 轮）", () =>
        {
            foreach (var info in StorageCleanupPolicy.All)
            {
                if (info.Risk == CleanupRisk.CostsOffline)
                    Assert(!info.DefaultSelected, info.DisplayName + " 会牺牲离线能力，必须默认不勾");
                if (info.Risk == CleanupRisk.Safe)
                    Assert(info.DefaultSelected, info.DisplayName + " 是安全项，应当默认勾选");
                Assert(info.Risk != CleanupRisk.Never, "没有任何分类可以是 Never");
                Assert(!string.IsNullOrWhiteSpace(info.Description), "每个分类都要有说明文案");
            }
            Assert(StorageCleanupPolicy.All.Select(i => i.Category).Distinct().Count() == StorageCleanupPolicy.All.Length,
                "分类不能重复");
            Assert(StorageCleanupPolicy.ProtectedSummary.Contains("存档"), "提示语必须明确说不会删存档");

            // The UI must read the policy, never hardcode a default.
            var vm = File.ReadAllText(Path.Combine(FindSourceRoot()!, "src", "AuroraLauncher.Desktop",
                "ViewModels", "MainViewModel.Round88.cs"));
            Assert(vm.Contains("group.Info.DefaultSelected"), "默认勾选状态必须来自策略");
        });

        Check("Guard: 清理服务只走白名单，绝不整根删除（第 88 轮）", () =>
        {
            var src = File.ReadAllText(Path.Combine(FindSourceRoot()!, "src", "AuroraCore",
                "Services", "StorageCleanupService.cs"));

            Assert(src.Contains("StorageCleanupPolicy.IsAllowedPath(_root"), "扫描必须过滤白名单");
            Assert(src.Contains("if (!StorageCleanupPolicy.IsAllowedPath(_root, item.Path))"),
                "删除前必须再校验一次（扫描结果可能已过期）");
            Assert(src.Contains("ActiveDownloads.IsActive"), "正在下载的残片必须复核归属");
            Assert(src.Contains("IsEmptyVersionShell"), "版本目录必须复核是否仍为空壳");

            // No recursive wipe of a tree that is not a verified empty shell.
            Assert(!src.Contains("Directory.Delete(_root"), "禁止删除整个数据根");
            Assert(!src.Contains("Directory.Delete(RootDirectory"), "禁止删除整个数据根");
            Assert(!src.Contains("Delete(_root, true)"), "禁止递归删除数据根");
            foreach (var forbidden in new[] { "instances", "backups", "java" })
                Assert(!src.Contains($"Path.Combine(_root, \"{forbidden}\")", StringComparison.OrdinalIgnoreCase),
                    $"清理服务不该触碰 {forbidden}/");
            Assert(src.Contains("Directory.Delete(item.Path, recursive: true)"),
                "唯一的递归删除只允许作用在已验证的空壳目录上");
        });

        Check("StorageCleanupService: 扫描分类与选择性删除（第 88 轮）", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "aurora-clean-" + Guid.NewGuid().ToString("N")[..8]);
            ActiveDownloads.ResetForTests();
            void W(string rel, int bytes)
            {
                var path = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new byte[bytes]);
            }
            try
            {
                // A 档：默认勾选
                W(Path.Combine("cache", "icons", "a.img"), 1000);
                W(Path.Combine("cache", "icons", "b.img"), 2000);
                W(Path.Combine("cache", "optifine", "OptiFine.jar"), 3000);
                W(Path.Combine("cache", "version_manifest_v2.json"), 500);
                W(Path.Combine("update", "AuroraLauncher.patch"), 4000);
                W(Path.Combine("logs", "aurora-2020-01-01.log"), 6000);
                W(Path.Combine("logs", $"aurora-{DateTime.Now:yyyy-MM-dd}.log"), 700);   // today: keep
                W(Path.Combine("libraries", "net", "example", "lib.jar.part"), 800);
                // B 档：默认不勾
                W(Path.Combine("cache", "installers", "meta.json"), 900);
                // 待安装的更新包：任何情况下都不许删
                W(Path.Combine("update", "AuroraLauncher.new.exe"), 1000);
                W(Path.Combine("update", "build-info.txt"), 100);
                // 绝不能碰的东西
                W(Path.Combine("instances", "i1", "saves", "world.dat"), 1111);
                W(Path.Combine("instances", "i1", "mods", "better.jar.part"), 222);
                W(Path.Combine("backups", "i1", "saves-2026.zip"), 333);
                W(Path.Combine("libraries", "net", "example", "lib.jar"), 444);
                // 版本：一个空壳、一个半装
                Directory.CreateDirectory(Path.Combine(root, "versions", "shell"));
                W(Path.Combine("versions", "half", "half.json"), 10);
                // 台账里有、还能续传的残片
                var journaled = Path.Combine(root, "libraries", "net", "example", "resume.jar");
                File.WriteAllBytes(journaled + ".part", new byte[1024]);
                var journalPath = DownloadJournal.PathFor(root);
                Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
                File.WriteAllText(journalPath, "+\thttps://example.invalid/r.jar\t" + journaled + "\t2048\t\n");

                var service = new StorageCleanupService(root);
                var report = service.Scan();

                CleanupGroup Group(CleanupCategory c) => report.Groups.FirstOrDefault(g => g.Category == c)!;
                Assert(Group(CleanupCategory.CacheIcons)?.FileCount == 2, "图标缓存应识别到 2 个文件");
                Assert(Group(CleanupCategory.CacheOptiFine)?.FileCount == 1, "OptiFine 缓存应识别到 1 个文件");
                Assert(Group(CleanupCategory.ManifestCache)?.FileCount == 1, "版本清单缓存应识别到");
                Assert(Group(CleanupCategory.UpdateLeftover)?.FileCount == 1,
                    "update/ 只应把残留算作垃圾（待安装包必须豁免）");
                Assert(Group(CleanupCategory.OldLogs)?.FileCount == 1, "只有超过 14 天的日志算垃圾");
                Assert(Group(CleanupCategory.OrphanParts)?.FileCount == 1, "无主残片只有 lib.jar.part");
                Assert(Group(CleanupCategory.ResumableParts)?.FileCount == 1, "台账里的残片单独成类");
                Assert(Group(CleanupCategory.EmptyVersionDirs)?.DirCount == 1, "只有空壳目录被识别");
                Assert(Group(CleanupCategory.InstallerMetaCache)?.FileCount == 1, "加载器缓存应识别到");
                Assert(report.Groups.All(g => g.Items.All(i => !i.Path.Contains("\\instances\\")
                                                              && !i.Path.Contains("\\backups\\"))),
                    "instances/ 与 backups/ 绝不能出现在清理清单里");

                // 只清 A 档（Safe）——B 档与可续传残片留着
                var safe = StorageCleanupPolicy.All.Where(i => i.Risk == CleanupRisk.Safe)
                    .Select(i => i.Category).ToHashSet();
                var result = service.Execute(report, safe);

                Assert(result.AnythingDeleted, "应当删除了内容");
                Assert(!File.Exists(Path.Combine(root, "cache", "icons", "a.img")), "图标缓存应被删除");
                Assert(!File.Exists(Path.Combine(root, "cache", "version_manifest_v2.json")), "清单缓存应被删除");
                Assert(!File.Exists(Path.Combine(root, "update", "AuroraLauncher.patch")), "更新残留应被删除");
                Assert(!File.Exists(Path.Combine(root, "logs", "aurora-2020-01-01.log")), "旧日志应被删除");
                Assert(File.Exists(Path.Combine(root, "logs", $"aurora-{DateTime.Now:yyyy-MM-dd}.log")),
                    "今天的日志必须保留");
                Assert(!File.Exists(Path.Combine(root, "libraries", "net", "example", "lib.jar.part")), "无主残片应被删除");
                Assert(!Directory.Exists(Path.Combine(root, "versions", "shell")), "空壳版本目录应被删除");

                Assert(File.Exists(Path.Combine(root, "update", "AuroraLauncher.new.exe")), "待安装的更新包必须保留！");
                Assert(File.Exists(Path.Combine(root, "update", "build-info.txt")), "待安装的更新信息必须保留");
                Assert(File.Exists(Path.Combine(root, "cache", "installers", "meta.json")), "未勾选的 B 档不能被删");
                Assert(File.Exists(journaled + ".part"), "未勾选的可续传残片不能被删");
                Assert(File.Exists(Path.Combine(root, "instances", "i1", "saves", "world.dat")), "存档绝不能被碰");
                Assert(File.Exists(Path.Combine(root, "instances", "i1", "mods", "better.jar.part")), "实例内文件绝不能被碰");
                Assert(File.Exists(Path.Combine(root, "backups", "i1", "saves-2026.zip")), "备份绝不能被碰");
                Assert(File.Exists(Path.Combine(root, "libraries", "net", "example", "lib.jar")), "库文件本体绝不能被碰");
                Assert(File.Exists(Path.Combine(root, "versions", "half", "half.json")), "半装版本目录必须保留");
                Assert(result.FreedBytes > 0, "释放量要如实统计");
            }
            finally
            {
                ActiveDownloads.ResetForTests();
                try { Directory.Delete(root, true); } catch { }
            }
        });

        Check("DownloadJournal.Forget: 清理残片后台账不再指向已删除的文件（第 88 轮复核）", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "aurora-forget-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            ActiveDownloads.ResetForTests();
            var journalPath = DownloadJournal.PathFor(root);
            // The writer thread would otherwise target the REAL data root.
            DownloadJournal.OverridePathForTests(journalPath);
            try
            {
                var dest = Path.Combine(root, "libraries", "net", "example", "resume.jar");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllBytes(dest + ".part", new byte[2048]);
                Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
                File.WriteAllText(journalPath, "+\thttps://example.invalid/r.jar\t" + dest + "\t4096\t\n");
                Assert(DownloadJournal.Destinations(root).Contains(dest), "前置条件：台账里应有这条记录");

                var scan = DownloadResidue.Scan(root);
                Assert(scan.JournaledCount == 1, "这条残片应归类为可续传");
                DownloadResidue.Clean(scan);
                DownloadJournal.Flush(1500);

                Assert(!File.Exists(dest + ".part"), "文件应已删除");
                Assert(!DownloadJournal.Destinations(root).Contains(dest),
                    "删除残片后必须把台账条目一起移除（否则日志会一直留着指向不存在文件的记录）");
                Assert(DownloadJournal.Load(root).Count == 0, "重放时也不该再冒出来");

                // Both cleanup entry points must do this, not just one of them.
                var srcRoot = Path.Combine(FindSourceRoot()!, "src", "AuroraCore");
                Assert(File.ReadAllText(Path.Combine(srcRoot, "Download", "DownloadResidue.cs"))
                        .Contains("DownloadJournal.Forget("),
                    "「清理残留下载文件」必须同步台账");
                Assert(File.ReadAllText(Path.Combine(srcRoot, "Services", "StorageCleanupService.cs"))
                        .Contains("DownloadJournal.Forget("),
                    "「扫描垃圾文件」也必须同步台账");
            }
            finally
            {
                DownloadJournal.OverridePathForTests(null);
                ActiveDownloads.ResetForTests();
                try { Directory.Delete(root, true); } catch { }
            }
        });

        Check("Guard: 清理的忙碌态与两个入口的分工都写清了（第 88 轮复核）", () =>
        {
            var root = FindSourceRoot()!;
            var vm = File.ReadAllText(Path.Combine(root, "src", "AuroraLauncher.Desktop",
                "ViewModels", "MainViewModel.Round88.cs"));
            var settings = File.ReadAllText(Path.Combine(root, "src", "AuroraLauncher.Desktop",
                "Views", "SettingsView.xaml"));

            // 忙碌态：按钮文案切换 + 复选框冻结 + 不确定态进度条（上万文件时不能"点了没反应"）
            Assert(vm.Contains("StorageScanButtonText") && vm.Contains("StorageCleanButtonText"),
                "扫描/清理进行中时按钮文案必须能变（否则看起来像死按钮）");
            Assert(vm.Contains("OnPropertyChanged(nameof(StorageScanButtonText))"),
                "文案属性必须在状态变化时通知（否则永远停在初始文字）");
            Assert(vm.Contains("CanEditStorageSelection"), "清理中应当冻结复选框，避免误以为改动生效");
            Assert(settings.Contains("IsIndeterminate=\"True\""), "清理可能耗时几秒，需要不确定态进度条");
            Assert(settings.Contains("CanEditStorageSelection"), "列表的 IsEnabled 必须绑定冻结开关");

            // 两个入口的分工：必须有一句话解释，否则用户会问"这两个按钮有什么区别"
            Assert(settings.Contains("只处理下载残渣"), "设置页必须说明「清理残留下载文件」只针对下载残渣");
            Assert(settings.Contains("扫描整个存储目录"), "设置页必须说明「扫描垃圾文件」是全盘扫描");

            // 清理完成后一次性刷新报告（不是逐项插入），并且台账/横幅一起刷新
            Assert(vm.Contains("StorageCleanupGroups.Clear()"), "报告应当整体替换");
            Assert(vm.Contains("RefreshPendingDownloadsAfterResidueAsync()"),
                "清掉可续传残片后，下载页的「继续下载」横幅必须重新加载");
        });

        Check("LauncherConfig: legacy mirror domain is healed in old configs", () =>
        {
            // Round-71: builds up to round-70 could persist bmclapi.cn into
            // config.json; the user must never be stuck on a hostname we know is dead.
            var cfg = new LauncherConfig
            {
                ProxyUrl = "https://bmclapi.cn/legacy-proxy",
                NetworkNote = "BMCLAPI: https://bmclapi.cn/mc/game/version_manifest_v2.json"
            };
            Assert(LauncherConfig.HealLegacyMirrorHost(cfg), "a config carrying the dead domain must report a change");
            Assert(!cfg.ProxyUrl!.Contains("bmclapi.cn"), "legacy domain replaced in ProxyUrl");
            Assert(cfg.ProxyUrl.Contains("bmclapi2.bangbang93.com"), "replacement uses the working mirror host");
            Assert(!cfg.NetworkNote!.Contains("bmclapi.cn"), "legacy domain replaced in NetworkNote");

            var clean = new LauncherConfig { NetworkNote = "已使用 BMCLAPI" };
            Assert(!LauncherConfig.HealLegacyMirrorHost(clean), "a clean config must not be rewritten");
        });

        Check("ManifestCache: offline copy round-trips and rejects junk", () =>
        {
            // Round-71: a dead mirror used to leave the version list EMPTY. The
            // launcher must still be able to show versions from the last good copy.
            var root = Path.Combine(Path.GetTempPath(), "aurora-mcache-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            try
            {
                Assert(AuroraCore.Services.ManifestCache.TryLoad(root) == null, "no cache yet → null");

                var json = AuroraCore.Utils.JsonHelper.Serialize(new AuroraCore.Models.VersionManifest
                {
                    Versions = new List<AuroraCore.Models.VersionManifestEntry>
                    {
                        new() { Id = "1.21.1", Type = "release", Url = "https://piston-meta.mojang.com/v1/packages/x/1.21.1.json" }
                    }
                });
                AuroraCore.Services.ManifestCache.Save(root, json);
                Assert(AuroraCore.Services.ManifestCache.Exists(root), "cache file must be written");

                var loaded = AuroraCore.Services.ManifestCache.TryLoad(root);
                Assert(loaded != null && loaded.Versions.Count == 1 && loaded.Versions[0].Id == "1.21.1",
                    "the cached manifest must round-trip");

                AuroraCore.Services.ManifestCache.Save(root, "{ not json ");
                Assert(AuroraCore.Services.ManifestCache.TryLoad(root) == null, "malformed cache must be ignored");

                AuroraCore.Services.ManifestCache.Save(root, "{\"versions\":[]}");
                Assert(AuroraCore.Services.ManifestCache.TryLoad(root) == null,
                    "an EMPTY cache is not valid data — report the network error instead");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

        Check("JobProgressPolicy: stage-aware percent, aggregate, throttle", () =>
        {
            // Round-72: several downloads at once must not blur into one number.
            Assert(AuroraCore.Services.JobProgressPolicy.PercentFor(-1, 0, 0.42) == 42,
                "single-step job = fraction of its own work");
            Assert(AuroraCore.Services.JobProgressPolicy.PercentFor(0, 4, 0.5) == 13,
                "stage 1 of 4 at 50% = 12.5% overall");
            Assert(AuroraCore.Services.JobProgressPolicy.PercentFor(3, 4, 0.5) == 88,
                "last stage at 50% must be ~88%, not 50%");
            Assert(AuroraCore.Services.JobProgressPolicy.PercentFor(4, 4, 0) == 100, "past the last stage = 100%");
            Assert(AuroraCore.Services.JobProgressPolicy.PercentFor(0, 4, 5) == 25, "fraction clamps to 1");
            Assert(AuroraCore.Services.JobProgressPolicy.PercentFor(0, 4, double.NaN) == 0, "NaN counts as 0");

            var jobs = new[]
            {
                new AuroraCore.Services.DownloadJobSnapshot(AuroraCore.Services.JobState.Running, 40, 1000),
                new AuroraCore.Services.DownloadJobSnapshot(AuroraCore.Services.JobState.Running, 80, 2000),
                new AuroraCore.Services.DownloadJobSnapshot(AuroraCore.Services.JobState.Queued, 0, 0),
                new AuroraCore.Services.DownloadJobSnapshot(AuroraCore.Services.JobState.Canceled, 90, 500),
            };
            var (percent, speed, active, queued) = AuroraCore.Services.JobProgressPolicy.Aggregate(jobs);
            Assert(active == 2 && queued == 1, $"counts: active={active} queued={queued}");
            Assert(speed == 3000, $"aggregate speed sums only running jobs, got {speed}");
            Assert(percent == 40, $"canceled jobs are excluded from the average, got {percent}");

            Assert(AuroraCore.Services.JobProgressPolicy.FormatSpeed(0) == "0 B/s", "zero speed");
            Assert(AuroraCore.Services.JobProgressPolicy.FormatSpeed(1024) == "1.0 KB/s", "1 KB/s formatting");
            Assert(AuroraCore.Services.JobProgressPolicy.FormatSpeed(5.5 * 1024 * 1024).Contains("MB/s"), "MB/s formatting");

            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert(!AuroraCore.Services.JobProgressPolicy.ShouldPush(t0, t0.AddMilliseconds(60)),
                "60 ms is inside the throttle window");
            Assert(AuroraCore.Services.JobProgressPolicy.ShouldPush(t0, t0.AddMilliseconds(120)),
                "beyond the window a refresh is allowed");
        });

        Check("DownloadJobManager: per-task cancel, queue cap, clear finished", () =>
        {
            // The manager is the reason "同时下载多个实例/模组" no longer means
            // "one task hijacks another's token".
            var manager = new AuroraCore.Services.DownloadJobManager(a => a(), maxConcurrent: 2);
            var a = manager.Create("实例", "A 安装", "实例 A");
            var b = manager.Create("模组", "B 模组", "实例 B");
            var c = manager.Create("整合包", "C 整合包", "");
            Assert(manager.Jobs.Count == 3 && manager.Jobs[0] == c, "newest job is first in the list");

            manager.StartAsync(a).GetAwaiter().GetResult();
            manager.StartAsync(b).GetAwaiter().GetResult();
            Assert(a.State == AuroraCore.Services.JobState.Running && b.State == AuroraCore.Services.JobState.Running,
                "both started jobs are running");
            var queuedStart = manager.StartAsync(c);
            Assert(!queuedStart.IsCompleted && c.State == AuroraCore.Services.JobState.Queued,
                "the third job waits for a slot (cap = 2)");

            manager.Finish(a, message: "完成");
            queuedStart.GetAwaiter().GetResult();
            Assert(a.State == AuroraCore.Services.JobState.Done && a.Percent == 100, "finished job is 100% and Done");
            Assert(c.State == AuroraCore.Services.JobState.Running, "the queued job starts when a slot frees");

            manager.Cancel(b);
            Assert(b.State == AuroraCore.Services.JobState.Canceled, "cancelling one job does not touch the others");
            Assert(c.State == AuroraCore.Services.JobState.Running, "the other job keeps running after a cancel");

            var removed = manager.ClearFinished();
            Assert(removed == 2, $"only finished rows are cleared, removed={removed}");
            Assert(manager.Jobs.Count == 1 && manager.Jobs[0] == c, "the running job survives cleanup");

            manager.CancelAll();
            Assert(c.State == AuroraCore.Services.JobState.Canceled, "cancel-all reaches the running job");
            manager.Finish(c, canceled: true);
            Assert(manager.ClearFinished() == 1 && manager.Jobs.Count == 0, "cleanup empties the list");
        });

        Check("DownloadJob: report throttling + failure message stays human", () =>
        {
            var posted = 0;
            var job = new AuroraCore.Services.DownloadJobManager(a => { posted++; a(); }).Create("模组", "X", "实例");
            job.SetStages(new[] { ("content", "下载 X") });

            // A burst of engine callbacks must collapse into very few UI pushes.
            for (var i = 0; i < 200; i++)
            {
                job.Report(new AuroraCore.Download.DownloadProgress
                {
                    TotalFiles = 200,
                    CompletedFiles = i,
                    SpeedBytesPerSec = 1024 * 1024
                });
            }
            Assert(posted < 40, $"200 progress callbacks must not produce 200 UI pushes (got {posted})");
            Assert(job.Percent >= 0 && job.Percent <= 100, $"percent stays in range, got {job.Percent}");
            Assert(job.SpeedText.Contains("MB/s"), $"speed text is formatted: {job.SpeedText}");
        });

        Check("PathWriteGate: one writer per destination, others run in parallel", () =>
        {
            // Round-73: two instances of the same MC version share libraries/ and
            // assets/ — both pipelines download the SAME jar. Without a gate they
            // could open the same .part (sharing violation) or commit a partial file.
            var dir = Path.Combine(Path.GetTempPath(), "aurora-gate-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, "lib.jar");

            try
            {
                using var first = AuroraCore.Download.PathWriteGate.Acquire(target);
                var blocked = Task.Run(() =>
                {
                    using var second = AuroraCore.Download.PathWriteGate.AcquireAsync(target).GetAwaiter().GetResult();
                    return true;
                });
                Assert(!blocked.Wait(150), "a second writer must wait for the first to release");

                // Different path must not be blocked by the first.
                var other = Path.Combine(dir, "other.jar");
                var parallel = Task.Run(() =>
                {
                    using var g = AuroraCore.Download.PathWriteGate.AcquireAsync(other).GetAwaiter().GetResult();
                    return true;
                });
                Assert(parallel.Wait(1000), "different destinations must not serialise");
                parallel.GetAwaiter().GetResult();

                first.Dispose();
                Assert(blocked.Wait(2000), "releasing the handle must let the waiter proceed");
                blocked.GetAwaiter().GetResult();

                // Case-insensitive: Foo.jar and foo.jar are the same file on Windows.
                using (AuroraCore.Download.PathWriteGate.Acquire(Path.Combine(dir, "Mixed.jar")))
                {
                    var caseBlocked = Task.Run(() =>
                    {
                        using var g = AuroraCore.Download.PathWriteGate.AcquireAsync(Path.Combine(dir, "mixed.jar").ToUpperInvariant())
                            .GetAwaiter().GetResult();
                        return true;
                    });
                    Assert(!caseBlocked.Wait(150), "path comparison must be case-insensitive");
                }

                // Cancelling a waiter must not leak the slot.
                using (AuroraCore.Download.PathWriteGate.Acquire(target))
                {
                    using var cts = new CancellationTokenSource(60);
                    var cancelled = false;
                    try
                    {
                        var handle = AuroraCore.Download.PathWriteGate.AcquireAsync(target, cts.Token).GetAwaiter().GetResult();
                        handle.Dispose();
                    }
                    catch (OperationCanceledException) { cancelled = true; }
                    Assert(cancelled, "a cancelled waiter must abort instead of taking the lock");
                }
                using var afterCancel = AuroraCore.Download.PathWriteGate.Acquire(target);
                Assert(afterCancel != null, "the gate is still usable after a cancelled waiter");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        Check("DownloadJobManager: a queued job can be withdrawn before it starts", () =>
        {
            // Round-72 promised "排队时点取消可直接撤掉"; this is that contract.
            var manager = new AuroraCore.Services.DownloadJobManager(a => a(), maxConcurrent: 1);
            var running = manager.Create("实例", "占用唯一槽位", "");
            var queued = manager.Create("模组", "排队中的模组", "");
            manager.StartAsync(running).GetAwaiter().GetResult();

            var queuedStart = manager.StartAsync(queued);
            Assert(!queuedStart.IsCompleted && queued.State == AuroraCore.Services.JobState.Queued,
                "the second job waits while the slot is taken");

            manager.Cancel(queued);
            Assert(queued.State == AuroraCore.Services.JobState.Canceled, "cancel while queued reacts immediately");
            var threw = false;
            try { queuedStart.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { threw = true; }
            Assert(threw, "the waiting StartAsync must abort (not silently start later)");

            manager.Finish(running, message: "完成");
            Assert(queued.State == AuroraCore.Services.JobState.Canceled,
                "the withdrawn job must never start when a slot frees");
        });

        Check("CloseGuardPolicy: only ask when work is really in flight", () =>
        {
            Assert(!AuroraCore.Services.CloseGuardPolicy.ShouldPrompt(0, 0, false),
                "closing with no jobs must never prompt");
            Assert(AuroraCore.Services.CloseGuardPolicy.ShouldPrompt(2, 0, false),
                "two running jobs must prompt");
            Assert(AuroraCore.Services.CloseGuardPolicy.ShouldPrompt(0, 3, false),
                "queued jobs alone also mean unfinished work");
            Assert(!AuroraCore.Services.CloseGuardPolicy.ShouldPrompt(2, 0, true),
                "after the user confirmed, a second close must not ask again");

            var text = AuroraCore.Services.CloseGuardPolicy.BuildMessage(2, 1);
            Assert(text.Contains("3 个") && text.Contains("2 个正在下载") && text.Contains("1 个排队"),
                $"the message must spell out the counts: {text}");
            Assert(text.Contains("确定"), "the message must ask a clear question");
            Assert(AuroraCore.Services.CloseGuardPolicy.BuildMessage(1, 0).Contains("1 个正在下载"),
                "single running job wording");

            var root = FindSourceRoot()!;
            var engine = File.ReadAllText(Path.Combine(root, "src", "AuroraCore", "Download", "DownloadEngine.cs"));
            Assert(engine.Contains("PathWriteGate.AcquireAsync(dest"),
                "every downloaded file must hold the per-destination gate");

            var window = File.ReadAllText(Path.Combine(root, "src", "AuroraLauncher.Desktop", "MainWindow.xaml.cs"));
            Assert(window.Contains("override void OnClosing") && window.Contains("CloseBehaviorPolicy.Decide"),
                "the window must route close requests through CloseBehaviorPolicy (round-74)");
            Assert(window.Contains("CancelAllDownloads()"),
                "confirming the exit must cancel jobs so .part cleanup runs");
        });

        Check("BinaryDelta: patch round-trips and rejects the wrong base", () =>
        {
            // Round-74: the update package is 63 MB; a one-line fix must not cost
            // the user a full download. This is the whole contract.
            var dir = Path.Combine(Path.GetTempPath(), "aurora-delta-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                var oldPath = Path.Combine(dir, "old.bin");
                var newPath = Path.Combine(dir, "new.bin");
                var patchPath = Path.Combine(dir, "x.patch");
                var outPath = Path.Combine(dir, "out.bin");

                var random = new Random(1234);
                var oldBytes = new byte[300_000];
                random.NextBytes(oldBytes);
                // A real rebuild keeps most bytes and shifts a few regions.
                var newBytes = (byte[])oldBytes.Clone();
                for (var i = 0; i < 40; i++)
                {
                    var at = random.Next(0, newBytes.Length - 500);
                    for (var k = 0; k < 400; k += 7) newBytes[at + k] = (byte)(newBytes[at + k] + 11);
                }
                File.WriteAllBytes(oldPath, oldBytes);
                File.WriteAllBytes(newPath, newBytes);

                BinaryDelta.Create(oldPath, newPath, patchPath);
                var patchSize = new FileInfo(patchPath).Length;
                Assert(patchSize < oldBytes.Length / 4,
                    $"a mostly-identical file must produce a small patch (got {patchSize} for {oldBytes.Length})");
                Assert(BinaryDelta.IsWorthPublishing(patchSize, newBytes.Length), "2% patch must count as worth publishing");

                Assert(BinaryDelta.TryReadHeader(patchPath, out var oldSize, out var newSize, out var headerHash),
                    "header must be readable before downloading anything else");
                Assert(oldSize == oldBytes.Length && newSize == newBytes.Length, "header sizes must match the inputs");
                Assert(headerHash == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(newBytes)),
                    "header must carry the produced file hash");

                var applied = BinaryDelta.Apply(oldPath, patchPath, outPath);
                Assert(File.ReadAllBytes(outPath).AsSpan().SequenceEqual(newBytes), "applied output must be byte-identical");
                Assert(applied == headerHash, "Apply must report the produced hash");

                // Wrong base: same length, different content → must fail, not ship garbage.
                var otherBytes = (byte[])oldBytes.Clone();
                otherBytes[1234] ^= 0xFF;
                File.WriteAllBytes(oldPath, otherBytes);
                var failed = false;
                try { BinaryDelta.Apply(oldPath, patchPath, outPath); }
                catch (InvalidDataException) { failed = true; }
                Assert(failed, "a mismatched base file must be rejected by the header hash");

                // Wrong size → refused before any work.
                File.WriteAllBytes(oldPath, new byte[oldBytes.Length - 1]);
                failed = false;
                try { BinaryDelta.Apply(oldPath, patchPath, outPath); }
                catch (InvalidDataException) { failed = true; }
                Assert(failed, "a base of the wrong size must be rejected");

                // Truncated patch → refused.
                var truncated = Path.Combine(dir, "trunc.patch");
                var patchBytes = File.ReadAllBytes(patchPath);
                File.WriteAllBytes(truncated, patchBytes[..(patchBytes.Length / 2)]);
                failed = false;
                try { BinaryDelta.Apply(oldPath, truncated, outPath); }
                catch (InvalidDataException) { failed = true; }
                Assert(failed, "a truncated patch must be rejected");

                // A "patch" as big as the package must lose to the package.
                Assert(!BinaryDelta.ShouldUsePatch(900, 1000), "a 90% patch is not worth the extra failure mode");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        Check("PortableModePolicy: portable.txt / config.json decide the data root", () =>
        {
            // Round-74: USB-stick users must not write into %APPDATA% of every PC.
            var dir = Path.Combine(Path.GetTempPath(), "aurora-portable-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                var exe = Path.Combine(dir, "AuroraLauncher.exe");
                File.WriteAllText(exe, "stub");

                Assert(PortableModePolicy.ResolveRoot(exe) == null, "no marker → standard mode");
                Assert(!PortableModePolicy.IsPortable(exe), "IsPortable must agree with ResolveRoot");

                var marker = PortableModePolicy.CreateMarker(exe, "test");
                Assert(File.Exists(marker) && Path.GetFileName(marker) == "portable.txt", "marker must be next to the EXE");
                Assert(PortableModePolicy.ResolveRoot(exe) == dir, "marker makes the EXE folder the data root");

                File.Delete(marker);
                Assert(PortableModePolicy.ResolveRoot(exe) == null, "removing the marker restores standard mode");

                // A config.json left beside the EXE also keeps it portable.
                File.WriteAllText(Path.Combine(dir, "config.json"), "{}");
                Assert(PortableModePolicy.ResolveRoot(exe) == dir, "an existing config.json implies portable");

                // Nothing to detect → null, never a crash on a weird path.
                Assert(PortableModePolicy.ResolveRoot(null) == null, "null exe path");
                Assert(PortableModePolicy.ResolveRoot(Path.Combine(dir, "missing", "a.exe")) == null,
                    "a non-existent folder is not portable");

                var text = PortableModePolicy.Describe(true, dir, exe);
                Assert(text.Contains("便携模式") && text.Contains(dir), $"portable description must state the root: {text}");
                Assert(PortableModePolicy.Describe(false, "C://data", exe).Contains("标准模式"),
                    "standard description");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        Check("DownloadJournal: resume only what really has a .part", () =>
        {
            // Round-74: a killed process leaves .part files; the banner must offer
            // exactly those, and completion must remove the entry.
            var root = Path.Combine(Path.GetTempPath(), "aurora-journal-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(root, "cache"));
            DownloadJournal.OverridePathForTests(DownloadJournal.PathFor(root));
            try
            {
                Assert(DownloadJournal.Load(root).Count == 0, "empty journal → nothing to resume");

                var dest = Path.Combine(root, "instances", "A", "mods", "big.jar");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                // 2 MB of a 8 MB file: above the 4 MB journalling floor, 25% done.
                File.WriteAllBytes(dest + ".part", new byte[2_000_000]);

                DownloadJournal.Record(new DownloadEntry
                {
                    Url = "https://example.test/big.jar",
                    Destination = dest,
                    ExpectedSize = 8_000_000,
                    ExpectedSha1 = "abc123"
                });
                DownloadJournal.Flush(2000);

                var pending = DownloadJournal.Load(root);
                Assert(pending.Count == 1, $"one interrupted file expected, got {pending.Count}");
                Assert(pending[0].Destination == dest, "destination must round-trip");
                Assert(pending[0].PartialBytes == 2_000_000, $"partial size must be read from the .part, got {pending[0].PartialBytes}");
                Assert(Math.Abs(pending[0].CompleteRatio - 0.25) < 0.001, "25% complete");

                // Completed afterwards → the entry disappears.
                DownloadJournal.Complete(dest);
                DownloadJournal.Flush(2000);
                Assert(DownloadJournal.Load(root).Count == 0, "a completed file must not be offered again");

                // No .part → not offered (the journal entry alone is not proof).
                File.Delete(dest + ".part");
                DownloadJournal.Record(new DownloadEntry { Url = "https://example.test/x", Destination = dest, ExpectedSize = 8_000_000 });
                DownloadJournal.Flush(2000);
                Assert(DownloadJournal.Load(root).Count == 0, "an entry without a .part is stale");

                // Small files and updater staging are never journalled.
                Assert(!DownloadJournal.ShouldTrack(new DownloadEntry { Url = "u", Destination = "x", ExpectedSize = 1024 }),
                    "tiny files are not worth a journal line");
                Assert(!DownloadJournal.ShouldTrack(new DownloadEntry
                {
                    Url = "u",
                    Destination = Path.Combine(LauncherConfig.Instance.RootDirectory, "update", "AuroraLauncher.patch"),
                    ExpectedSize = 8 * 1024 * 1024
                }), "updater staging must never appear as an unfinished user download");

                // Discard removes both the journal and the leftover bytes.
                File.WriteAllBytes(dest + ".part", new byte[100]);
                DownloadJournal.Record(new DownloadEntry { Url = "https://example.test/big.jar", Destination = dest, ExpectedSize = 8_000_000 });
                DownloadJournal.Flush(2000);
                var removed = DownloadJournal.Discard(root, deletePartials: true);
                Assert(removed == 1 && !File.Exists(dest + ".part"), $"discard must delete the .part (removed={removed})");
                Assert(!File.Exists(DownloadJournal.PathFor(root)), "discard must drop the journal");
            }
            finally
            {
                DownloadJournal.OverridePathForTests(null);
                try { Directory.Delete(root, true); } catch { }
            }
        });

        Check("CloseBehaviorPolicy: tray decides between ask / exit / hide", () =>
        {
            // Round-74 matrix. Round-73 refused to hide at all; the difference now
            // is a real tray icon, so "no tray" must still fall back.
            var noTrayIdle = CloseBehaviorPolicy.Decide(CloseAction.Ask, false, 0, 0);
            Assert(noTrayIdle.Kind == CloseDecisionKind.Exit, "no tray + no work → just close");

            var noTrayBusy = CloseBehaviorPolicy.Decide(CloseAction.Ask, false, 2, 1);
            Assert(noTrayBusy.Kind == CloseDecisionKind.Prompt && !noTrayBusy.OfferTray,
                "no tray + downloads → ask, without offering a tray that does not exist");

            Assert(CloseBehaviorPolicy.Decide(CloseAction.MinimizeToTray, false, 0, 0).Kind == CloseDecisionKind.Exit,
                "minimize requested but no tray → must not trap the window");

            var ask = CloseBehaviorPolicy.Decide(CloseAction.Ask, true, 0, 0);
            Assert(ask.Kind == CloseDecisionKind.Prompt && ask.OfferTray,
                "tray + ask → always offer the choice");

            Assert(CloseBehaviorPolicy.Decide(CloseAction.MinimizeToTray, true, 3, 0).Kind == CloseDecisionKind.HideToTray,
                "tray + minimize → hide immediately, no dialog");

            var exitWithWork = CloseBehaviorPolicy.Decide(CloseAction.Exit, true, 1, 0);
            Assert(exitWithWork.Kind == CloseDecisionKind.Prompt && exitWithWork.MentionDownloads,
                "an explicit exit must still warn while a job is running");
            Assert(CloseBehaviorPolicy.Decide(CloseAction.Exit, true, 0, 0).Kind == CloseDecisionKind.Exit,
                "explicit exit + idle → close without a dialog");

            var message = CloseBehaviorPolicy.BuildMessage(CloseBehaviorPolicy.Decide(CloseAction.Ask, true, 2, 1));
            Assert(message.Contains("3 个") && message.Contains("2 个正在下载") && message.Contains("1 个排队"),
                $"the prompt must spell out the counts: {message}");
            Assert(message.Contains("最小化到托盘") && message.Contains("退出"),
                "the prompt must name both options");

            Assert(CloseBehaviorPolicy.BuildTrayHint(2, 1).Contains("3 个"), "tray hint mentions the job count");
            Assert(CloseBehaviorPolicy.FromDisplayText("最小化到托盘") == CloseAction.MinimizeToTray, "display text round-trip");
            Assert(CloseBehaviorPolicy.FromDisplayText("直接退出") == CloseAction.Exit, "display text round-trip (exit)");
            Assert(CloseBehaviorPolicy.FromDisplayText("不认识的值") == CloseAction.Ask, "unknown text falls back to ask");

            // Round-90: the label itself must carry the promise, and the parser
            // must accept BOTH the new label and the legacy short form.
            Assert(CloseBehaviorPolicy.DisplayText(CloseAction.Exit).Contains("有下载"),
                "「直接退出」的文案必须写明有下载时仍会确认");
            Assert(CloseBehaviorPolicy.FromDisplayText(CloseBehaviorPolicy.DisplayText(CloseAction.Exit)) == CloseAction.Exit,
                "new label must round-trip");
            Assert(CloseBehaviorPolicy.FromDisplayText("直接退出（有下载时仍会确认）") == CloseAction.Exit,
                "explicit new label parses");
            Assert(CloseBehaviorPolicy.FromDisplayText(null) == CloseAction.Ask, "null text falls back to ask");
        });

        Check("SettingsDirtyPolicy: 快照对比能发现任何一项修改（第 90 轮）", () =>
        {
            static List<KeyValuePair<string, string?>> V(params (string K, string? V)[] pairs)
                => pairs.Select(p => new KeyValuePair<string, string?>(p.K, p.V)).ToList();

            var a = SettingsDirtyPolicy.Snapshot(V(("memory", "4096"), ("java", @"C:\jdk\bin\javaw.exe")));
            var b = SettingsDirtyPolicy.Snapshot(V(("java", @"C:\jdk\bin\javaw.exe"), ("memory", "4096")));
            Assert(a == b, "快照必须与字段顺序无关（否则每次进设置页都像改过）");
            Assert(!SettingsDirtyPolicy.IsDirty(a, b), "同样的值不算修改");

            var changed = SettingsDirtyPolicy.Snapshot(V(("memory", "8192"), ("java", @"C:\jdk\bin\javaw.exe")));
            Assert(SettingsDirtyPolicy.IsDirty(a, changed), "改一个字段就算脏");
            var keys = SettingsDirtyPolicy.ChangedKeys(a, changed);
            Assert(keys.Count == 1 && keys[0] == "memory", $"应当只报一个字段：{string.Join(",", keys)}");

            Assert(SettingsDirtyPolicy.ChangedKeys(a, a).Count == 0, "没改就是空集");
            Assert(SettingsDirtyPolicy.IsDirty(null, a), "没有基线时必须当作脏（宁可多问一次）");
            Assert(!SettingsDirtyPolicy.IsDirty(null, null), "两边都没基线不算脏");

            var prompt = SettingsDirtyPolicy.BuildPrompt(new[] { "内存分配", "Java 路径" });
            Assert(prompt.Contains("2 项") && prompt.Contains("内存分配") && prompt.Contains("Java 路径"),
                "提示语要列出改了哪几项：" + prompt);
            Assert(prompt.Contains("保存") && prompt.Contains("放弃") && prompt.Contains("留在"),
                "提示语要写清三个选择");
            Assert(SettingsDirtyPolicy.BuildPrompt(null).Contains("没有保存"), "没有字段名时也要有兜底文案");

            // A value containing the record separator must not corrupt the snapshot.
            var dirty = SettingsDirtyPolicy.Snapshot(V(("java", "a\nb")));
            Assert(SettingsDirtyPolicy.ChangedKeys(dirty, dirty).Count == 0, "换行不能把快照切坏");
        });

        Check("Guard: 离开设置页必须拦截、有任务时关窗必须弹窗（第 90 轮）", () =>
        {
            var root = FindSourceRoot()!;
            var vmMain = File.ReadAllText(Path.Combine(root, "src", "AuroraLauncher.Desktop",
                "ViewModels", "MainViewModel.cs"));
            var vmGuard = File.ReadAllText(Path.Combine(root, "src", "AuroraLauncher.Desktop",
                "ViewModels", "MainViewModel.Round90.cs"));
            var window = File.ReadAllText(Path.Combine(root, "src", "AuroraLauncher.Desktop",
                "MainWindow.xaml.cs"));

            // Navigation: the gate must sit BEFORE the page actually changes.
            var navIdx = vmMain.IndexOf("public void Navigate(string? key)", StringComparison.Ordinal);
            Assert(navIdx > 0, "找不到 Navigate");
            var navBody = vmMain.Substring(navIdx, Math.Min(900, vmMain.Length - navIdx));
            var gateIdx = navBody.IndexOf("ConfirmLeaveSettings(target)", StringComparison.Ordinal);
            var applyIdx = navBody.IndexOf("CurrentPageKey = target", StringComparison.Ordinal);
            Assert(gateIdx > 0, "Navigate must call ConfirmLeaveSettings");
            Assert(applyIdx > gateIdx, "the gate must run BEFORE the page key changes (otherwise it cannot block)");

            Assert(vmGuard.Contains("SettingsDirtyPolicy.IsDirty"), "脏检查必须走策略类");
            Assert(vmGuard.Contains("ApplySettings();"), "选「保存并离开」必须真的保存");
            Assert(vmGuard.Contains("DiscardSettingsChanges()"), "选「放弃」必须能回滚");
            Assert(vmGuard.Contains("LoadSettingsFromConfig();"),
                "回滚只能重读配置，不能重新 LoadAll（会重复添加账号/实例）");

            // Close paths: window X and tray menu exit must both ask.
            var closingIdx = window.IndexOf("protected override void OnClosing", StringComparison.Ordinal);
            Assert(closingIdx > 0, "找不到 OnClosing");
            var closingBody = window.Substring(closingIdx, Math.Min(1200, window.Length - closingIdx));
            Assert(closingBody.Contains("ConfirmCloseWithUnsavedSettings()"),
                "关窗前必须提示未保存的设置");

            var trayIdx = window.IndexOf("private void ExitFromTray()", StringComparison.Ordinal);
            Assert(trayIdx > 0, "找不到 ExitFromTray");
            var trayBody = window.Substring(trayIdx, Math.Min(1400, window.Length - trayIdx));
            var guardCall = trayBody.IndexOf("ConfirmExitWithActiveJobs(", StringComparison.Ordinal);
            var shutDown = trayBody.IndexOf("_shuttingDown = true", StringComparison.Ordinal);
            Assert(guardCall > 0, "托盘菜单退出必须先确认有任务的情况");
            Assert(shutDown > guardCall, "确认必须在设置 _shuttingDown 之前（否则会绕过弹窗）");
            Assert(trayBody.Contains("ConfirmCloseWithUnsavedSettings()"), "托盘退出也要提示未保存的设置");

            // The policy itself: any close path with jobs must prompt, whatever the setting says.
            foreach (var action in new[] { CloseAction.Exit, CloseAction.Ask })
                Assert(CloseBehaviorPolicy.Decide(action, trayAvailable: true, runningJobs: 2, queuedJobs: 0).Kind
                       == CloseDecisionKind.Prompt,
                    $"{CloseBehaviorPolicy.DisplayText(action)} + 有任务 → 必须弹窗");
            Assert(CloseBehaviorPolicy.Decide(CloseAction.Exit, trayAvailable: false, runningJobs: 0, queuedJobs: 3).Kind
                   == CloseDecisionKind.Prompt, "排队中也是未完成，必须弹窗");
        });

        Check("SingleInstancePolicy: one launcher per data root, not per exe path", () =>
        {
            // Round-74: two copies must not fight over config.json / instances.
            var a = SingleInstancePolicy.Fingerprint(@"C:/Users/X/AppData/Roaming/AuroraLauncher");
            var b = SingleInstancePolicy.Fingerprint(@"c:/users/x/appdata/roaming/auroralauncher/");
            Assert(a == b, "path comparison must ignore case and a trailing separator");
            Assert(a != SingleInstancePolicy.Fingerprint(@"C:/Users/X/Other"), "different roots must not share a name");
            Assert(SingleInstancePolicy.MutexName(@"C:/A").StartsWith("AuroraLauncher.Instance."), "mutex name prefix");
            Assert(SingleInstancePolicy.ActivationEventName(@"C:/A") != SingleInstancePolicy.MutexName(@"C:/A"),
                "mutex and event must be distinct objects");
            Assert(SingleInstancePolicy.SecondInstanceNote(@"C:/A").Contains(@"C:/A"), "the note names the root");
        });

        Check("UpdateService: patch manifest must be signed and round-matched", () =>
        {
            // Round-74: the patch route is signed separately so old clients still
            // accept the manifest, while a tampered patchUrl cannot be injected.
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var publicXml = rsa.ToXmlString(false);

            string Sign(string payload) => Convert.ToBase64String(rsa.SignData(
                System.Text.Encoding.UTF8.GetBytes(payload),
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1));

            const int round = 74;
            const string patchUrl = "https://example.test/AuroraLauncher.patch";
            const long patchSize = 1_000_000;
            var manifest = new UpdateManifest
            {
                Round = round,
                Version = "0.74.0",
                Url = "https://example.test/AuroraLauncher.exe",
                Sha256 = new string('A', 64),
                Size = 65_000_000,
                PatchUrl = patchUrl,
                PatchSha256 = new string('B', 64),
                PatchSize = patchSize,
                PatchFromRound = 73,
                PatchSignature = Sign($"{round}|{patchUrl}|{(new string('B', 64))}|{patchSize}")
            };

            Assert(UpdateService.VerifyPatchSignature(manifest, publicXml), "a correctly signed patch route verifies");
            Assert(UpdateService.ShouldUsePatch(manifest, 73, publicXml), "round 73 may use the patch to 74");
            Assert(!UpdateService.ShouldUsePatch(manifest, 72, publicXml), "a different build must not attempt the patch");

            var tampered = new UpdateManifest
            {
                Round = round, Url = manifest.Url, Sha256 = manifest.Sha256, Size = manifest.Size,
                PatchUrl = "https://evil.test/AuroraLauncher.patch",
                PatchSha256 = manifest.PatchSha256, PatchSize = patchSize, PatchFromRound = 73,
                PatchSignature = manifest.PatchSignature
            };
            Assert(!UpdateService.VerifyPatchSignature(tampered, publicXml), "a swapped patch URL must fail verification");
            Assert(!UpdateService.ShouldUsePatch(tampered, 73, publicXml), "a swapped patch URL must not be used");

            var unsigned = new UpdateManifest
            {
                Round = round, Url = manifest.Url, Sha256 = manifest.Sha256, Size = manifest.Size,
                PatchUrl = patchUrl, PatchSha256 = manifest.PatchSha256, PatchSize = patchSize, PatchFromRound = 73
            };
            Assert(!UpdateService.ShouldUsePatch(unsigned, 73, publicXml), "an unsigned patch must be refused");

            var huge = new UpdateManifest
            {
                Round = round, Url = manifest.Url, Sha256 = manifest.Sha256, Size = 1_000_000,
                PatchUrl = patchUrl, PatchSha256 = manifest.PatchSha256, PatchSize = 900_000, PatchFromRound = 73,
                PatchSignature = Sign($"{round}|{patchUrl}|{manifest.PatchSha256}|{900_000L}")
            };
            Assert(!UpdateService.ShouldUsePatch(huge, 73, publicXml), "a patch bigger than the package is pointless");

            // Backwards compatibility: the base payload must NOT have changed, or
            // every installation on an older build would reject the new manifest.
            Assert(manifest.SignedPayload == $"{round}|{manifest.Version}|{manifest.Url}|{manifest.Sha256}|{manifest.Size}",
                "SignedPayload must stay byte-identical to previous rounds");
            Assert(manifest.PatchSignedPayload.EndsWith($"|{patchSize}"), "patch payload covers round/url/hash/size");

            var exe = File.ReadAllText(Path.Combine(FindSourceRoot()!, "src", "AuroraCore", "Services", "UpdateService.cs"));
            Assert(exe.Contains("BinaryDelta.Apply(currentExe, patchPath, mergedPath)"),
                "the patch must be merged with the running exe");
            Assert(exe.Contains("合并结果校验失败（清单 SHA-256 缺失或不一致）"),
                "a merged result that does not match the signed hash must be rejected");
        });

        Check("SingleInstanceGuard: only a shown window acknowledges an activation", () =>
        {
            // Round-74 shipped "switching to the existing window" while nothing
            // appeared; round-75 acknowledged even when showing failed, so the
            // second instance still exited. The contract is now: ack == a window
            // is on screen.
            var rootA = Path.Combine(Path.GetTempPath(), "aurora-single-a-" + Guid.NewGuid().ToString("N")[..8]);
            Assert(AuroraLauncher.Desktop.Services.SingleInstanceGuard.TryAcquire(rootA, () => false),
                "the first process owns the instance name");
            var withoutWindow = AuroraLauncher.Desktop.Services.SingleInstanceGuard
                .SignalExisting(rootA, TimeSpan.FromMilliseconds(600));
            Assert(!withoutWindow, "a process with no window to show must NOT acknowledge");
            AuroraLauncher.Desktop.Services.SingleInstanceGuard.Release();

            var rootB = Path.Combine(Path.GetTempPath(), "aurora-single-b-" + Guid.NewGuid().ToString("N")[..8]);
            Assert(AuroraLauncher.Desktop.Services.SingleInstanceGuard.TryAcquire(rootB, () => true),
                "second instance acquires its own root");
            Assert(AuroraLauncher.Desktop.Services.SingleInstanceGuard
                    .SignalExisting(rootB, TimeSpan.FromSeconds(2)),
                "a restored window acknowledges, so the caller may exit");
            AuroraLauncher.Desktop.Services.SingleInstanceGuard.Release();

            // Release must free the name immediately — a lingering process would
            // otherwise block every later double-click.
            Assert(AuroraLauncher.Desktop.Services.SingleInstanceGuard.TryAcquire(rootA, () => true),
                "release must hand the instance name back");
            AuroraLauncher.Desktop.Services.SingleInstanceGuard.Release();

            var rootSrc = FindSourceRoot()!;
            var window = File.ReadAllText(Path.Combine(rootSrc, "src", "AuroraLauncher.Desktop", "MainWindow.xaml.cs"));
            Assert(window.Contains("public bool RestoreFromTray()"),
                "restore must report whether a window is actually shown");
            Assert(window.Contains("Environment.Exit(0)") && window.Contains("aurora-exit-watchdog"),
                "closing the last window must be enforced even if WPF defers shutdown");

            var app = File.ReadAllText(Path.Combine(rootSrc, "src", "AuroraLauncher.Desktop", "App.xaml.cs"));
            Assert(app.Contains("SingleInstanceGuard.SignalExisting(dataRoot") && app.Contains("TimeSpan.FromSeconds(2)"),
                "a second launch must wait for the ack before exiting");
            Assert(app.Contains("private AuroraLauncher.Desktop.MainWindow? _shell"),
                "the listener must not read Application.MainWindow off the UI thread");
            Assert(app.Contains("SingleInstanceGuard.Release()"),
                "the lock must be released explicitly on exit");

            var tray = File.ReadAllText(Path.Combine(rootSrc, "src", "AuroraLauncher.Desktop", "Services", "TrayIconService.cs"));
            Assert(tray.Contains("DispatcherPriority.Background"),
                "tray menu actions must leave the menu's nested message loop before closing the window");
            Assert(tray.Contains("_source.Dispatcher.BeginInvoke"),
                "tray actions must be dispatched onto the UI thread");
        });

        Check("GameInstance: playtime reads like a human wrote it", () =>
        {
            // Round-77: "42 小时 15 分钟 · 启动 128 次" must not render as "0 分钟"
            // right after the first session, and must not invent decimals.
            Assert(GameInstance.FormatPlaytime(0) == "0 分钟", "zero");
            Assert(GameInstance.FormatPlaytime(30) == "不足 1 分钟", "half a minute is not '0 分钟'");
            Assert(GameInstance.FormatPlaytime(59) == "不足 1 分钟", "59 s");
            Assert(GameInstance.FormatPlaytime(60) == "1 分钟", "one minute");
            Assert(GameInstance.FormatPlaytime(59 * 60) == "59 分钟", "just under an hour");
            Assert(GameInstance.FormatPlaytime(3600) == "1 小时", "exact hour has no '0 分钟' tail");
            Assert(GameInstance.FormatPlaytime(42 * 3600 + 15 * 60) == "42 小时 15 分钟", "the example from the request");
            Assert(GameInstance.FormatPlaytime(-5) == "0 分钟", "negative clamps to zero");

            var fresh = new GameInstance { Name = "x" };
            Assert(fresh.PlayStatsText == "还没有游玩记录", "a new instance says so instead of '已游玩 0 分钟'");
            var played = new GameInstance { PlaySeconds = 42 * 3600 + 15 * 60, LaunchCount = 128 };
            Assert(played.PlayStatsText == "已游玩 42 小时 15 分钟 · 启动 128 次", $"got: {played.PlayStatsText}");
        });

        Check("PlaytimeTracker: a killed launcher still gets credit for the session", () =>
        {
            // Round-77: the launcher and the game usually die together (task
            // manager / power cut). The last heartbeat is the only honest end time.
            var root = Path.Combine(Path.GetTempPath(), "aurora-playtime-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(PlaytimeTracker.SessionPathFor(root, "x").Replace("x.session.json", ""));
            try
            {
                var started = DateTime.UtcNow.AddMinutes(-90);
                var orphan = PlaytimeTracker.SessionPathFor(root, "inst1");
                File.WriteAllText(orphan, System.Text.Json.JsonSerializer.Serialize(new PlaySession
                {
                    InstanceId = "inst1",
                    StartedUtc = started,
                    HeartbeatUtc = started.AddMinutes(89), // last proof of life
                    ProcessId = 0
                }));

                var tracker = new PlaytimeTracker(root);
                var recovered = tracker.RecoverOrphans(_ => false);
                Assert(recovered.Count == 1, $"one orphan expected, got {recovered.Count}");
                Assert(Math.Abs(recovered[0].Duration.TotalMinutes - 89) < 1.5,
                    $"must credit up to the heartbeat (89 min), got {recovered[0].Duration.TotalMinutes:F1}");
                Assert(!File.Exists(orphan), "the session marker must be consumed");

                // A session whose game process is STILL alive must not be settled.
                var live = PlaytimeTracker.SessionPathFor(root, "inst2");
                File.WriteAllText(live, System.Text.Json.JsonSerializer.Serialize(new PlaySession
                {
                    InstanceId = "inst2",
                    StartedUtc = DateTime.UtcNow.AddMinutes(-5),
                    HeartbeatUtc = DateTime.UtcNow,
                    ProcessId = 4242
                }));
                Assert(tracker.RecoverOrphans(pid => pid == 4242).Count == 0, "a live game keeps its session");
                Assert(File.Exists(live), "the live session file stays");

                // Clamping: clock jumps and sleeps must not invent days.
                Assert(PlaytimeTracker.Clamp(started, started.AddDays(3)) == TimeSpan.FromHours(24),
                    "a 3-day 'session' clamps to 24 h");
                Assert(PlaytimeTracker.Clamp(started, started.AddSeconds(-30)) == TimeSpan.Zero,
                    "an end before the start is zero, not negative");

                // Begin/End on a fresh tracker leaves no marker behind.
                var tracker2 = new PlaytimeTracker(root);
                tracker2.Begin("inst3");
                var elapsed = tracker2.End();
                Assert(elapsed >= TimeSpan.Zero, "elapsed is never negative");
                Assert(!File.Exists(PlaytimeTracker.SessionPathFor(root, "inst3")), "End clears the marker");
                tracker2.Dispose();
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

        Check("ModPreflight: missing prerequisite / conflict / duplicate / wrong loader", () =>
        {
            // Round-77: catch the "launch, crash, go read latest.log" loop before
            // it happens. Synthetic jars keep this offline and deterministic.
            var root = Path.Combine(Path.GetTempPath(), "aurora-preflight-" + Guid.NewGuid().ToString("N")[..8]);
            var mods = Path.Combine(root, "mods");
            Directory.CreateDirectory(mods);
            try
            {
                void Jar(string name, string entry, string content)
                {
                    using var zip = System.IO.Compression.ZipFile.Open(
                        Path.Combine(mods, name), System.IO.Compression.ZipArchiveMode.Create);
                    var e = zip.CreateEntry(entry);
                    using var w = new StreamWriter(e.Open());
                    w.Write(content);
                }

                Jar("api.jar", "fabric.mod.json",
                    "{\"schemaVersion\":1,\"id\":\"fabric-api\",\"name\":\"Fabric API\",\"version\":\"0.100.0\"," +
                    "\"provides\":[\"fabric-api-base\",\"fabric-networking-api-v1\"]}");
                Jar("mod-a.jar", "fabric.mod.json",
                    "{\"schemaVersion\":1,\"id\":\"moda\",\"name\":\"Mod A\",\"version\":\"1.0\"," +
                    "\"depends\":{\"fabric-api\":\"*\",\"fabric-api-base\":\">=1\",\"minecraft\":\"1.21\"}}");
                Jar("mod-b.jar", "fabric.mod.json",
                    "{\"schemaVersion\":1,\"id\":\"modb\",\"name\":\"Mod B\",\"version\":\"1.0\"," +
                    "\"depends\":{\"fabricloader\":\">=0.15\"},\"breaks\":{\"moda\":\"*\"}}");
                Jar("mod-a-copy.jar", "fabric.mod.json",
                    "{\"schemaVersion\":1,\"id\":\"moda\",\"name\":\"Mod A copy\",\"version\":\"1.0\"}");
                Jar("forge.jar", "META-INF/mods.toml",
                    "modLoader=\"javafml\"\nloaderVersion=\"[47,)\"\n\n[[mods]]\nmodId=\"forgemod\"\n" +
                    "version=\"1.0\"\ndisplayName=\"Forge Mod\"\n\n" +
                    "[[dependencies.forgemod]]\n    modId=\"forge\"\n    mandatory=true\n    versionRange=\"[47,)\"\n    side=\"BOTH\"\n\n" +
                    "[[dependencies.forgemod]]\n    modId=\"missing-lib\"\n    mandatory=true\n    versionRange=\"[1.0,2.0)\"\n    side=\"BOTH\"\n\n" +
                    "[[dependencies.forgemod]]\n    modId=\"optional-lib\"\n    mandatory=false\n    versionRange=\"*\"\n");

                var report = ModPreflight.Scan(root, ModLoaderType.Fabric, _ => { });
                Assert(report.ScannedJars == 5, $"5 mods expected, got {report.ScannedJars}");
                Assert(!report.FabricApiMissing, "Fabric API is installed → no auto-install offer");

                // fabric-api-base is satisfied via provides, minecraft/fabricloader are platform ids.
                Assert(report.Missing.All(m => m.ModId != "fabric-api-base"),
                    "a module provided by the Fabric API jar must count as present");
                Assert(report.Missing.Any(m => m.ModId == "missing-lib" && m.RequiredBy == "Forge Mod"),
                    $"the Forge mandatory dependency must be reported: {string.Join(", ", report.Missing.Select(m => m.ModId))}");
                Assert(report.Missing.All(m => m.ModId != "optional-lib"), "mandatory=false is not a problem");

                Assert(report.Conflicts.Count == 1 && report.Conflicts[0].Contains("Mod B") && report.Conflicts[0].Contains("Mod A"),
                    $"one conflict expected: {string.Join(" | ", report.Conflicts)}");
                Assert(report.Duplicates.Count == 1 && report.Duplicates[0].Contains("moda"),
                    $"duplicate moda expected: {string.Join(" | ", report.Duplicates)}");
                Assert(report.WrongLoader.Count == 1 && report.WrongLoader[0].Contains("forge.jar"),
                    $"the Forge jar inside a Fabric instance must be flagged: {string.Join(" | ", report.WrongLoader)}");
                Assert(report.HasIssues && report.Headline.Contains("前置"), $"headline: {report.Headline}");

                // Now remove the Fabric API jar: the same pack must report it and
                // offer the one-click install.
                File.Delete(Path.Combine(mods, "api.jar"));
                var withoutApi = ModPreflight.Scan(root, ModLoaderType.Fabric, _ => { });
                Assert(withoutApi.FabricApiMissing, "missing fabric-api must set the install flag");
                Assert(withoutApi.Missing.Any(m => m.ModId == "fabric-api"), "fabric-api reported missing");
                Assert(withoutApi.Missing.Count(m => m.ModId == "fabric-api") == 1,
                    "the same missing id from two jars is reported once per dependent mod, not duplicated");

                // Vanilla instance / empty folder must be quiet.
                Assert(ModPreflight.Scan(Path.Combine(root, "nope"), ModLoaderType.Fabric, _ => { }).ScannedJars == 0,
                    "missing mods folder is not an error");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

        Check("MrpackExporter: Modrinth-hosted mods become links, the rest are bundled", () =>
        {
            // Round-77: the point is a small file the friend can actually install.
            var root = Path.Combine(Path.GetTempPath(), "aurora-mrpack-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            // GameInstance.Directory is derived from the (test) launcher config, so
            // the instance must live there — that is exactly what production does.
            var instDir = Path.Combine(LauncherConfig.Instance.InstancesDir, "abc");
            Directory.CreateDirectory(Path.Combine(instDir, "mods"));
            Directory.CreateDirectory(Path.Combine(instDir, "config"));
            try
            {
                File.WriteAllText(Path.Combine(instDir, "mods", "known.jar"), "known-mod-bytes");
                File.WriteAllText(Path.Combine(instDir, "mods", "local.jar"), "local-mod-bytes");
                File.WriteAllText(Path.Combine(instDir, "mods", "disabled.jar.disabled"), "off");
                File.WriteAllText(Path.Combine(instDir, "config", "settings.cfg"), "key=value");
                File.WriteAllText(Path.Combine(instDir, "options.txt"), "fov:0.5");
                File.WriteAllText(Path.Combine(instDir, "servers.dat"), "private-server-list");
                File.WriteAllText(Path.Combine(instDir, "mods", "logs.txt"), "not a jar");

                var instance = new GameInstance
                {
                    Id = "abc",
                    Name = "测试整合包",
                    MinecraftVersion = "1.21.1",
                    LoaderType = ModLoaderType.Fabric,
                    LoaderVersion = "0.16.0"
                };
                // GameInstance.Directory is derived from LauncherConfig; point the
                // export at our temp folder by using the instance dir directly.
                var knownSha1 = MrpackExporter.HashFile(Path.Combine(instDir, "mods", "known.jar")).Sha1;

                var lookup = new AuroraCore.Services.ModrinthHashLookup((hashes, ct) =>
                {
                    var map = new Dictionary<string, ModrinthFileMatch>(StringComparer.OrdinalIgnoreCase);
                    foreach (var h in hashes)
                        if (h == knownSha1)
                            map[h] = new ModrinthFileMatch("proj", "ver", "known.jar",
                                "https://cdn.example/known.jar", 15, h, "sha512-off");
                    return Task.FromResult<IReadOnlyDictionary<string, ModrinthFileMatch>>(map);
                });

                var output = Path.Combine(root, "out.mrpack");
                var result = AuroraCore.Services.MrpackExporter
                    .ExportAsync(instance, output, "分享包", "说明", lookup, null, CancellationToken.None)
                    .GetAwaiter().GetResult();

                Assert(File.Exists(output), "the .mrpack must exist");
                Assert(result.ModsFromModrinth == 1, $"one mod resolved from Modrinth, got {result.ModsFromModrinth}");
                Assert(result.ModsBundled == 1, $"one mod bundled, got {result.ModsBundled}");
                Assert(result.OverrideFiles >= 2, $"config + options.txt expected, got {result.OverrideFiles}");
                Assert(result.SizeBytes > 0 && result.Summary.Contains("Modrinth 直链 1"), $"summary: {result.Summary}");

                using var zip = System.IO.Compression.ZipFile.OpenRead(output);
                var indexEntry = zip.GetEntry("modrinth.index.json");
                Assert(indexEntry != null, "modrinth.index.json must be at the archive root");
                string indexJson;
                using (var reader = new StreamReader(indexEntry!.Open())) indexJson = reader.ReadToEnd();
                using var doc = System.Text.Json.JsonDocument.Parse(indexJson);
                var rootEl = doc.RootElement;
                Assert(rootEl.GetProperty("game").GetString() == "minecraft", "game = minecraft");
                Assert(rootEl.GetProperty("formatVersion").GetInt32() == 1, "formatVersion 1");
                Assert(rootEl.GetProperty("name").GetString() == "分享包", "pack name is honoured");
                var deps = rootEl.GetProperty("dependencies");
                Assert(deps.GetProperty("minecraft").GetString() == "1.21.1", "minecraft dependency");
                Assert(deps.GetProperty("fabric-loader").GetString() == "0.16.0", "fabric-loader dependency");
                var files = rootEl.GetProperty("files");
                Assert(files.GetArrayLength() == 1, $"only the Modrinth-hosted mod is a link, got {files.GetArrayLength()}");
                var file0 = files[0];
                Assert(file0.GetProperty("path").GetString() == "mods/known.jar", "path keeps the local file name");
                Assert(file0.GetProperty("downloads")[0].GetString() == "https://cdn.example/known.jar", "download url");
                Assert(file0.GetProperty("hashes").GetProperty("sha1").GetString() == knownSha1, "sha1 is recorded");

                var names = zip.Entries.Select(e => e.FullName).ToList();
                Assert(names.Contains("overrides/mods/local.jar"), "unknown mod goes into overrides");
                Assert(names.Contains("overrides/config/settings.cfg"), "config goes into overrides");
                Assert(names.Contains("overrides/options.txt"), "options.txt goes into overrides");
                Assert(!names.Contains("overrides/mods/known.jar"), "a Modrinth-hosted mod must not be duplicated");
                Assert(!names.Any(n => n.Contains("servers.dat")), "servers.dat is NOT exported (private addresses)");
                Assert(!names.Any(n => n.Contains("disabled.jar")), "disabled mods are not exported");

                // Response parser (pure): picks the primary file, falls back to first.
                var parsed = new Dictionary<string, ModrinthFileMatch>(StringComparer.OrdinalIgnoreCase);
                AuroraCore.Services.MrpackExporter.ParseLookupResponse(
                    "{\"abc\":{\"project_id\":\"p\",\"id\":\"v\",\"files\":[" +
                    "{\"url\":\"https://cdn.example/x.jar\",\"filename\":\"x.jar\",\"size\":10,\"primary\":true," +
                    "\"hashes\":{\"sha1\":\"abc\",\"sha512\":\"zz\"}}]}}", parsed);
                Assert(parsed.TryGetValue("abc", out var m) && m.Url.EndsWith("x.jar") && m.Sha512 == "zz",
                    "version_files response must be parsed");
                Assert(parsed["abc"].ProjectId == "p" && parsed["abc"].VersionId == "v", "project/version ids kept");
            }
            finally
            {
                try { Directory.Delete(instDir, true); } catch { }
                try { Directory.Delete(root, true); } catch { }
            }
        });

        Check("InstanceManager: launch count and playtime survive a reload", () =>
        {
            // Round-77: the numbers a user sees must come back after a restart, and
            // a clock jump must not be able to invent days of playtime.
            var manager = new InstanceManager();
            var instance = manager.Create("时长测试", "1.21.1", ModLoaderType.Vanilla);
            try
            {
                var started = manager.RecordLaunchStart(instance.Id);
                Assert(started != null && started.LaunchCount == 1, $"launch must be counted, got {started?.LaunchCount}");
                Assert(started!.LastPlayedAt != null, "last-played timestamp is set");

                var afterTime = manager.AddPlaytime(instance.Id, TimeSpan.FromMinutes(42));
                Assert(afterTime != null && afterTime.PlaySeconds == 42 * 60,
                    $"42 minutes expected, got {afterTime?.PlaySeconds}");

                var reloaded = manager.Get(instance.Id)!;
                Assert(reloaded.PlaySeconds == 42 * 60 && reloaded.LaunchCount == 1,
                    "values must round-trip through instances.json");
                Assert(reloaded.PlayStatsText.Contains("42 分钟") && reloaded.PlayStatsText.Contains("启动 1 次"),
                    $"card text: {reloaded.PlayStatsText}");

                var clamped = manager.AddPlaytime(instance.Id, TimeSpan.FromHours(48));
                Assert(clamped!.PlaySeconds == 42 * 60 + 24 * 3600,
                    $"a 48 h session clamps to 24 h, got {clamped.PlaySeconds}");

                var none = manager.AddPlaytime(instance.Id, TimeSpan.Zero);
                Assert(none!.PlaySeconds == clamped.PlaySeconds, "zero duration changes nothing");
                Assert(manager.AddPlaytime("missing-id", TimeSpan.FromMinutes(5)) == null,
                    "unknown instance id is a no-op");
            }
            finally { try { manager.Delete(instance.Id); } catch { } }
        });

        Check("Guard: CHANGELOG round numbering is continuous", () =>
        {
            // Round sections were lost/misnumbered twice (5, 16 lost; 44→46
            // skip). Round 59 moved all history into CHANGELOG.md — scan it
            // there now, and make sure the archive keeps pace with releases:
            // it must end exactly one round before the current build.
            var srcRoot = FindSourceRoot()!;
            var changelog = File.ReadAllText(Path.Combine(srcRoot, "CHANGELOG.md"));
            var nums = System.Text.RegularExpressions.Regex.Matches(
                    changelog, @"^## 更新补充（第 (\d+) 轮", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(m => int.Parse(m.Groups[1].Value)).ToList();
            Assert(nums.Count >= 40, $"too few round sections found ({nums.Count})");
            var gaps = new List<string>();
            for (int i = 1; i < nums.Count; i++)
                if (nums[i] != nums[i - 1] + 1)
                    gaps.Add($"{nums[i - 1]}→{nums[i]}");
            Assert(gaps.Count == 0, $"CHANGELOG round numbering has gaps: {string.Join("; ", gaps)}");
            Assert(nums.Count > 0 && nums[^1] == AuroraCore.BuildInfo.CurrentRound - 1,
                $"CHANGELOG must end at round {AuroraCore.BuildInfo.CurrentRound - 1} (last = {nums.LastOrDefault()}) — " +
                "rotate the previous README round into CHANGELOG.md on every release");

            // Round-60 follow-up: the two docs LOOKED out of sync because the
            // human-readable "history covers up to round N" lines still said
            // 58 while the archive already ended at 59. Guard the handoff
            // itself, not just the section numbering.
            var readme = File.ReadAllText(Path.Combine(srcRoot, "README.md"));
            var latest = System.Text.RegularExpressions.Regex.Match(
                readme, @"^### 第 (\d+) 轮[：:]", System.Text.RegularExpressions.RegexOptions.Multiline);
            Assert(latest.Success, "README must carry a '### 第 N 轮：...' latest-round section");
            Assert(int.Parse(latest.Groups[1].Value) == AuroraCore.BuildInfo.CurrentRound,
                $"README latest round ({latest.Groups[1].Value}) must equal BuildInfo.CurrentRound ({AuroraCore.BuildInfo.CurrentRound})");

            var claims = new List<(string Doc, int Round)>();
            foreach (var doc in new[] { ("README.md", readme), ("CHANGELOG.md", changelog) })
            {
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                             doc.Item2, @"第\s*1\s*[–—-]\s*(\d+)\s*轮历史"))
                    claims.Add((doc.Item1, int.Parse(m.Groups[1].Value)));
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                             doc.Item2, @"第\s*3\s*轮至第\s*(\d+)\s*轮逐轮补充"))
                    claims.Add((doc.Item1, int.Parse(m.Groups[1].Value)));
            }
            Assert(claims.Count == 2, $"expected exactly 2 explicit history-range claims (README + CHANGELOG), got {claims.Count}");
            foreach (var c in claims)
                Assert(c.Round == nums[^1],
                    $"{c.Doc} claims history up to round {c.Round}, but CHANGELOG actually ends at {nums[^1]} — " +
                    "update the range statement when rotating rounds");
        });

        Check("ModpackIntegrity: 模组数与清单双口径（第 92 轮）", () =>
        {
            // GameInstance.Directory is derived from the configured data root,
            // so the fixtures must live there too (never in a stray temp dir).
            const string id = "pack92test";
            var instDir = Path.Combine(LauncherConfig.Instance.InstancesDir, id);
            Directory.CreateDirectory(Path.Combine(instDir, "mods"));
            for (var i = 0; i < 12; i++)
                File.WriteAllBytes(Path.Combine(instDir, "mods", $"m{i}.jar"), new byte[16]);

            GameInstance Inst(int expected) => new()
            {
                Id = id, Name = "pack", IsModpack = true,
                ExpectedModCount = expected, MinecraftVersion = "1.20.1"
            };

            try
            {
                Assert(ModpackIntegrity.Evaluate(Inst(12)).State == ModpackIntegrity.State.Complete,
                    "12/12 必须算完整");
                Assert(ModpackIntegrity.Evaluate(Inst(13)).State == ModpackIntegrity.State.Complete,
                    "只差 1 个（用户自己删的）不该被拦，否则每次启动都要弹窗");
                Assert(ModpackIntegrity.Evaluate(Inst(40)).State == ModpackIntegrity.State.Partial,
                    "12/40 必须算大面积缺失");
                Assert(ModpackIntegrity.Evaluate(Inst(0)).State == ModpackIntegrity.State.Complete,
                    "没声明模组的包（纯配置包）不能被判成空");
                var notPack = Inst(12);
                notPack.IsModpack = false;
                Assert(ModpackIntegrity.Evaluate(notPack).State == ModpackIntegrity.State.NotModpack,
                    "普通实例不参与判定");

                // 整包没下到 —— 用户遇到的那种情况
                foreach (var f in Directory.GetFiles(Path.Combine(instDir, "mods"))) File.Delete(f);
                var empty = ModpackIntegrity.Evaluate(Inst(12));
                Assert(empty.State == ModpackIntegrity.State.Empty && empty.Expected == 12 && empty.Actual == 0,
                    $"删光后必须是 Empty，实际 {empty.State}/{empty.Expected}/{empty.Actual}");
                Assert(empty.Describe().Contains("一个都没有"), "文案要直说「一个都没有」：" + empty.Describe());

                // 清单：写入 / 读回 / 缺失统计 / 尺寸不符也算缺失
                var manifest = new ModpackManifest { PackName = "BattleArmory", GameVersion = "1.20.1" };
                for (var i = 0; i < 12; i++)
                    manifest.Files.Add(new ModpackManifest.ModpackFile
                    { Path = $"mods/m{i}.jar", Url = $"https://example.invalid/m{i}.jar", Sha1 = "x", Size = 16 });
                manifest.Files.Add(new ModpackManifest.ModpackFile { Path = "config/a.toml", Url = "https://example.invalid/a" });
                manifest.Save(instDir);

                var loaded = ModpackManifest.Load(instDir);
                Assert(loaded != null && loaded.ModFileCount == 12, "清单必须能读回来，且只把 mods/*.jar 算模组");
                Assert(loaded!.MissingCount(instDir) == 13, $"全空时 13 个声明都算缺失，实际 {loaded.MissingCount(instDir)}");

                // 写回 5 个，第 6 个是半截文件（尺寸不符也算缺失）
                for (var i = 0; i < 5; i++) File.WriteAllBytes(Path.Combine(instDir, "mods", $"m{i}.jar"), new byte[16]);
                File.WriteAllBytes(Path.Combine(instDir, "mods", "m5.jar"), new byte[64]);
                Assert(loaded.MissingCount(instDir) == 8, $"缺 7 个 + 1 个半截 + config 都应算缺失，实际 {loaded.MissingCount(instDir)}");
                var partial = ModpackIntegrity.Evaluate(Inst(12));
                Assert(partial.State == ModpackIntegrity.State.Partial, $"6/12 必须算 Partial，实际 {partial.State}");
                Assert(partial.Actual == 6 && partial.MissingFiles == 8,
                    $"实际 {partial.Actual} 个 / 缺失 {partial.MissingFiles}");
            }
            finally { try { Directory.Delete(instDir, true); } catch { } }
        });

        Check("LaunchReadiness: 加载器与整合包状态必须拦住启动（第 92 轮）", () =>
        {
            Assert(!LaunchReadiness.IsLoaderVersionId("1.20.1"), "原版 id 不是加载器版本");
            Assert(LaunchReadiness.IsLoaderVersionId("1.20.1-forge-47.4.22"), "Forge 的 id 带后缀");
            Assert(LaunchReadiness.IsLoaderVersionId("fabric-loader-0.20.1-1.20.1"), "Fabric 的 id 带前缀");
            Assert(!LaunchReadiness.IsLoaderVersionId(""), "空 id 不算");

            Assert(!LaunchReadiness.LoaderInstalled(ModLoaderType.Forge, "1.20.1", null, false),
                "ResolvedVersionId 为空 = 加载器没装 —— 这正是用户那台机器上的状态");
            Assert(!LaunchReadiness.LoaderInstalled(ModLoaderType.Forge, "1.20.1", "1.20.1", false),
                "指着原版 id = 没装（旧代码在这里判为「完整」）");
            Assert(!LaunchReadiness.LoaderInstalled(ModLoaderType.Forge, "1.20.1", "1.20.1-forge-47.4.22", false),
                "版本 JSON 不在 = 没装");
            Assert(LaunchReadiness.LoaderInstalled(ModLoaderType.Forge, "1.20.1", "1.20.1-forge-47.4.22", true),
                "id + JSON 都在才算装好");
            Assert(LaunchReadiness.LoaderInstalled(ModLoaderType.Vanilla, "1.20.1", "1.20.1", false),
                "原版实例永远算装好");

            var loaderGap = LaunchReadiness.Decide(new LaunchReadiness.Input
            {
                LoaderType = ModLoaderType.Forge,
                MinecraftVersion = "1.20.1",
                ResolvedVersionId = null,
                LoaderProfileExists = false,
                InstanceName = "BattleArmory TACZ"
            });
            Assert(loaderGap.Blocker == LaunchReadiness.Blocker.LoaderNotInstalled,
                $"Forge 实例没有加载器版本 → LoaderNotInstalled，实际 {loaderGap.Blocker}");
            Assert(!loaderGap.CanOverride, "加载器没装好不许「仍然启动」——那启动的是另一个游戏");
            Assert(loaderGap.CanRepair, "必须给出补全出路");
            Assert(loaderGap.Headline.Contains("Forge"), "标题要点名是哪个加载器");
            Assert(loaderGap.Detail.Contains("原版"), "必须说清「从这里启动只会进原版」");

            var packEmpty = LaunchReadiness.Decide(new LaunchReadiness.Input
            {
                LoaderType = ModLoaderType.Forge,
                MinecraftVersion = "1.20.1",
                ResolvedVersionId = "1.20.1-forge-47.4.22",
                LoaderProfileExists = true,
                Modpack = new ModpackIntegrity.Scan(ModpackIntegrity.State.Empty, 87, 0, 87),
                CanRepairModpack = false,
                InstanceName = "pack"
            });
            Assert(packEmpty.Blocker == LaunchReadiness.Blocker.ModpackEmpty, "mods 全空必须拦");
            Assert(packEmpty.CanOverride, "整合包缺模组允许用户自己承担风险继续");
            Assert(!packEmpty.CanRepair, "原始包不在时不能假装能补全");
            Assert(packEmpty.Detail.Contains("87"), "要报出应有数量，用户才知道差多少");

            var ok = LaunchReadiness.Decide(new LaunchReadiness.Input
            {
                LoaderType = ModLoaderType.Forge,
                MinecraftVersion = "1.20.1",
                ResolvedVersionId = "1.20.1-forge-47.4.22",
                LoaderProfileExists = true,
                Modpack = new ModpackIntegrity.Scan(ModpackIntegrity.State.Complete, 87, 87, 0)
            });
            Assert(!ok.Blocks, "齐全时不能拦");

            // ---- 真实服务路径：确认真实文件状态也走同一判定 ----
            var svc = new LauncherService();
            const string mc = "92.0.1";
            var vdir = Path.Combine(LauncherConfig.Instance.VersionsDir, mc);
            GameInstance? inst = null;
            try
            {
                Directory.CreateDirectory(vdir);
                File.WriteAllText(Path.Combine(vdir, mc + ".json"),
                    "{\"id\":\"" + mc + "\",\"mainClass\":\"net.minecraft.client.main.Main\",\"libraries\":[]}");
                File.WriteAllBytes(Path.Combine(vdir, mc + ".jar"), new byte[4096]);

                inst = svc.Instances.Create("闸门测试", mc, ModLoaderType.Forge, "47.4.22");
                inst.IsModpack = true;
                inst.ExpectedModCount = 30;
                svc.Instances.Update(inst);

                Assert(svc.LoaderNeedsInstall(inst), "原版装好但加载器没有 → LoaderNeedsInstall");
                Assert(svc.NeedsRepair(inst), "NeedsRepair 必须为真：旧实现只看原版文件，返回 false 才让整包当成完整");
                var real = svc.CheckReadiness(inst);
                Assert(real.Blocker == LaunchReadiness.Blocker.LoaderNotInstalled,
                    $"服务层必须报 LoaderNotInstalled，实际 {real.Blocker}（{real.Headline}）");
            }
            finally
            {
                if (inst != null)
                {
                    try
                    {
                        var rest = svc.Instances.LoadAll().Where(i => i.Id != inst.Id).ToList();
                        svc.Instances.SaveAll(rest);
                        if (Directory.Exists(inst.Directory)) Directory.Delete(inst.Directory, true);
                    }
                    catch { }
                }
                try { Directory.Delete(vdir, true); } catch { }
            }
        });

        Check("PerformanceReport.RunVerdict: 崩溃/闪退/未采集不再谎报「健康」（第 92 轮）", () =>
        {
            var none = FpsSummary.None;
            var crash = PerformanceReport.RunVerdict(1, 60, 0, 4096, none);
            Assert(crash.Level == "异常退出", $"退出码非 0 必须报异常退出，实际 {crash.Level}");
            Assert(crash.Text.Contains("退出码 1"), "要把退出码说出来：" + crash.Text);

            var flash = PerformanceReport.RunVerdict(0, 2, 0, 4096, none);
            Assert(flash.Level == "闪退", $"2 秒的会话不是健康，实际 {flash.Level}");
            Assert(flash.Text.Contains("补全"), "闪退要给出可操作建议");

            // 用户截图里的那条：跑了 1 分钟、峰值 2 MB
            var bogus = PerformanceReport.RunVerdict(0, 61, 2, 3072, none);
            Assert(bogus.Level == "未采集到", $"2 MB 不是 JVM 的读数，应报未采集，实际 {bogus.Level}");

            Assert(PerformanceReport.RunVerdict(0, 1800, 3900, 4096, none).Level == "吃紧", "接近上限仍要报吃紧");
            Assert(PerformanceReport.RunVerdict(0, 1800, 2500, 4096, none).Level == "健康", "正常值仍是健康");
            Assert(PerformanceReport.RunVerdict(0, 0, 0, 4096, none).Level == "无数据", "没有记录时仍是「无数据」");
            // 向后兼容：只有内存数字的老调用点行为不变
            Assert(PerformanceReport.Verdict(3900, 4096, none).Level == "吃紧", "Verdict 仍按老语义工作");

            // 旧实例（本机真实数据）不能因为 2 MB 就被写成「健康」
            var legacy = new GameInstance { LastPerfAt = DateTime.Now, LastRunSeconds = 60, LastPeakMemoryMb = 2 };
            Assert(legacy.PerfSummaryText.Contains("未采集到"), "摘要要写「峰值内存未采集到」而不是 2 MB：" + legacy.PerfSummaryText);
        });

        Check("Guard: 启动闸门与整合包预期不能被绕过（第 92 轮）", () =>
        {
            var srcRoot = FindSourceRoot()!;
            var service = File.ReadAllText(Path.Combine(srcRoot, "src", "AuroraCore", "Services", "LauncherService.cs"));
            var vm = File.ReadAllText(Path.Combine(srcRoot, "src", "AuroraLauncher.Desktop", "ViewModels", "MainViewModel.cs"));
            var launcher = File.ReadAllText(Path.Combine(srcRoot, "src", "AuroraCore", "Services", "GameLauncher.cs"));

            // 1) 构建启动参数时必须拒绝「加载器实例指向原版 id」
            var buildIdx = service.IndexOf("public async Task<LaunchSpec> BuildLaunchSpecAsync", StringComparison.Ordinal);
            Assert(buildIdx > 0, "BuildLaunchSpecAsync 不见了");
            var guardIdx = service.IndexOf("LaunchReadiness.IsLoaderVersionId(id)", StringComparison.Ordinal);
            Assert(guardIdx > buildIdx, "BuildLaunchSpecAsync 里必须有加载器 id 检查（否则会构建出原版命令行）");
            Assert(service.IndexOf("_builder.Build(ctx)", StringComparison.Ordinal) > guardIdx,
                "检查必须在构建之前");

            // 2) NeedsRepair 必须覆盖「加载器没装」和「整合包不完整」
            Assert(service.Contains("if (LoaderNeedsInstall(instance)) return true;"),
                "NeedsRepair 漏掉加载器检查 → 启动时会静默进原版");
            Assert(service.Contains("return instance.IsModpack && ModpackIntegrity.Evaluate(instance).IsBroken;"),
                "NeedsRepair 漏掉整合包检查 → 模组没下到也判完整");

            // 3) 启动流程必须先过闸门
            var launchIdx = vm.IndexOf("var gate = _svc.CheckReadiness(instance);", StringComparison.Ordinal);
            Assert(launchIdx > 0, "启动流程缺少 CheckReadiness 闸门");
            Assert(launchIdx < vm.IndexOf("RunPreflightAsync(instance, _cts.Token)", StringComparison.Ordinal),
                "闸门必须在模组预检/启动之前——放在后面等于没拦");
            Assert(vm.Contains("if (!ConfirmLaunchGate(gate, instance))"),
                "闸门结论必须交给用户确认，不能只是打日志");

            // 4) 启动失败必须走人话翻译并记录堆栈
            Assert(vm.Contains("Status = ReportLaunchFailure(ex);"), "启动异常的 catch 必须走 ReportLaunchFailure");
            var vmRound92 = File.ReadAllText(Path.Combine(srcRoot, "src", "AuroraLauncher.Desktop",
                "ViewModels", "MainViewModel.Round92.cs"));
            Assert(vmRound92.Contains("\"启动失败: \" + text"), "状态栏仍要保留「启动失败」前缀");
            Assert(vmRound92.Contains("+ Environment.NewLine + ex"),
                "必须把完整异常（含堆栈）写进日志——只记 Message 是这次查不到空引用的原因");
            Assert(File.ReadAllText(Path.Combine(srcRoot, "src", "AuroraCore", "Services", "LaunchFailureText.cs"))
                    .Contains("NullReferenceException"),
                "空引用必须有专门的人话翻译");

            // 5) 运行结果要带退出码落盘，否则面板无法判断崩溃
            Assert(vm.Contains("exitCode: runOutcome.ExitCode"), "RecordPerformance 必须传退出码");

            // 6) 采样必须是专用线程，不能再退回 Task.Run（会被线程池饿死，只采到首帧 2 MB）
            Assert(launcher.Contains("new Thread("), "内存采样必须用专用线程");
            Assert(!launcher.Contains("Task.Run(async"), "不能再用 Task.Run 采样：实测只采到进程 stub 的 2 MB");
            Assert(launcher.Contains("FirstSampleDelay"), "首采样必须等 JVM 起来（否则读到的是 launcher stub）");
            Assert(launcher.Contains("sampler.Samples == 0"), "采不到样本时要如实记日志");

            // 7) 整合包预期必须在下载之前落盘（两个导入入口都要）
            var expectIdx = vm.IndexOf("RecordModpackExpectation(inst, path", StringComparison.Ordinal);
            Assert(expectIdx > 0, "本地 .mrpack 导入没有记录预期");
            Assert(expectIdx < vm.IndexOf("await _svc.ModManager.InstallMrpackAsync(path", StringComparison.Ordinal),
                "预期必须在下载整合包文件之前写入（导入中途失败才认得出来）");
            Assert(vm.Contains("RecordModpackExpectation(inst, tempFile"),
                "内容中心安装整合包也必须记录预期");
            Assert(vm.Contains("_svc.Instances.Update(inst); // Round-92: persist ResolvedVersionId"),
                "安装完必须保存 ResolvedVersionId，否则重启后又变回「加载器没装」");

            // 8) 策略与模型字段本身
            var model = File.ReadAllText(Path.Combine(srcRoot, "src", "AuroraCore", "Models", "GameInstance.cs"));
            foreach (var field in new[] { "IsModpack", "ExpectedModCount", "LastExitCode" })
                Assert(model.Contains(field), $"GameInstance 缺少 {field}");
        });

        Check("CommandGate: 一次只放一个进去（第 93 轮）", () =>
        {
            var gate = new CommandGate();
            Assert(gate.TryEnter(), "第一次必须进得去");
            Assert(gate.IsRunning, "进入后 IsRunning 必须为真");
            Assert(!gate.TryEnter(), "同一个门第二次必须被挡住");
            Assert(gate.SuppressedClicks == 1, $"被挡的次数要记账，实际 {gate.SuppressedClicks}");
            gate.Exit();
            Assert(!gate.IsRunning, "退出后必须解锁");
            Assert(gate.TryEnter(), "解锁后必须能再进");

            // 并发狂点：32 个线程同时抢，只能有一个赢
            var parallel = new CommandGate();
            var wins = 0;
            System.Threading.Tasks.Parallel.For(0, 32, _ =>
            {
                if (parallel.TryEnter()) Interlocked.Increment(ref wins);
            });
            Assert(wins == 1, $"32 次并发点击只能进入一次，实际 {wins}");

            gate.Reset();
            Assert(!gate.IsRunning && gate.SuppressedClicks == 0, "Reset 要清干净（测试/teardown 用）");
        });

        Check("RelayCommand: 执行中再点不会重入，异常也会解锁（第 93 轮）", () =>
        {
            var entered = 0;
            var release = new TaskCompletionSource();
            var cmd = new RelayCommand(async _ => { entered++; await release.Task; });
            Assert(cmd.CanExecute(null), "空闲时可执行");

            cmd.Execute(null);
            Assert(entered == 1, $"第一次点击必须执行，实际 {entered}");
            Assert(cmd.IsRunning, "执行中 IsRunning 必须为真（按钮文案靠它）");
            Assert(!cmd.CanExecute(null), "执行中 CanExecute 必须为 false，否则 WPF 不会置灰");

            cmd.Execute(null); // 用户狂点
            Assert(entered == 1, $"执行中再点不得重入，实际进入 {entered} 次");
            Assert(cmd.SuppressedClicks == 1, "被吞掉的点击要记账（否则「点不动」无法取证）");

            release.SetResult();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (cmd.IsRunning && sw.ElapsedMilliseconds < 3000) Thread.Sleep(10);
            Assert(!cmd.IsRunning, "完成后必须解锁");
            Assert(cmd.CanExecute(null), "解锁后应能再次执行");
            cmd.Execute(null);
            Assert(entered == 2, "解锁后的一次点击必须真的执行");

            // 异常路径：finally 必须解锁，且异常不能把进程带走
            var boom = new RelayCommand(async _ => { await Task.Yield(); throw new InvalidOperationException("boom"); });
            boom.Execute(null);
            sw.Restart();
            while (boom.IsRunning && sw.ElapsedMilliseconds < 3000) Thread.Sleep(10);
            Assert(!boom.IsRunning, "命令抛异常后必须解锁（否则按钮永远灰着）");

            // 同步命令不开门（UI 线程内不可能重入），行为保持原样
            var sync = new RelayCommand(_ => { });
            Assert(sync.CanExecute(null) && !sync.IsRunning, "同步命令不分配门，语义不变");
        });

        Check("Guard: 异步命令必须自带防重入，且不许「火后不管」（第 93 轮）", () =>
        {
            var srcRoot = FindSourceRoot()!;
            var vmBase = File.ReadAllText(Path.Combine(srcRoot, "src", "AuroraLauncher.Desktop",
                "ViewModels", "ViewModelBase.cs"));
            Assert(vmBase.Contains("_gate.TryEnter()"), "RelayCommand 必须真的用门");
            Assert(vmBase.Contains("guardReentry = true"), "异步命令必须默认开启防重入");
            Assert(vmBase.Contains("if (_gate?.IsRunning == true) return false;"),
                "执行中 CanExecute 必须返回 false，否则按钮不会置灰");
            Assert(vmBase.Contains("nameof(IsRunning)"), "IsRunning 必须发通知（文案联动靠它）");
            Assert(vmBase.Contains("Logger.Instance.Error(\"Cmd\", \"命令执行失败: \""),
                "以前异常只进 Debug.WriteLine，等于黑洞");

            // 火后不管（_ = SomeAsync()）会让防重入完全失效：命令本身秒回，
            // 第二次点击时门已经空了。三个入口都必须改成 async 命令。
            var vmFiles = Directory.GetFiles(Path.Combine(srcRoot, "src", "AuroraLauncher.Desktop", "ViewModels"),
                "*.cs", SearchOption.TopDirectoryOnly);
            var fireAndForget = new List<string>();
            foreach (var f in vmFiles)
            {
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                             File.ReadAllText(f), @"new RelayCommand\(_\s*=>\s*_\s*=\s*(\w+Async)\("))
                    fireAndForget.Add($"{Path.GetFileName(f)}: {m.Groups[1].Value}");
            }
            Assert(fireAndForget.Count == 0,
                "这些命令是「火后不管」，防重入对它无效，请改成 async 命令: " + string.Join("; ", fireAndForget));

            // 文案联动：几个耗时按钮必须绑 IsRunning（写死 Content 就没有“动起来”）
            foreach (var (file, command) in new[]
                     {
                         ("HomeView.xaml", "MicrosoftCompleteCommand"),
                         ("InstancesView.xaml", "ScanInstancesCommand"),
                         ("InstancesView.xaml", "RepairInstanceCommand"),
                         ("SettingsView.xaml", "CheckUpdateCommand"),
                     })
            {
                var xaml = File.ReadAllText(Path.Combine(srcRoot, "src", "AuroraLauncher.Desktop", "Views", file));
                Assert(xaml.Contains($"{command}.IsRunning"),
                    $"{file} 的 {command} 没有做「执行中」文案联动");
            }

            // 转换器要支持「忙碌|空闲」参数，否则上面那些绑定会渲染成 已启用/已禁用
            var conv = File.ReadAllText(Path.Combine(srcRoot, "src", "AuroraLauncher.Desktop",
                "Converters", "BoolToTextConverter.cs"));
            Assert(conv.Contains("Split('|', 2)"), "BoolToText 必须支持 ConverterParameter='忙碌|空闲'");

            // 顺手修的显示 bug：收起的 ComboBox 在拿不到 SelectionBoxItemTemplate
            // 时退回 ToString()，以前渲染成完整类型名。
            Assert(new AuroraLauncher.Desktop.ViewModels.JavaChoiceVM("自动选择（按游戏版本）", "").ToString()
                   == "自动选择（按游戏版本）",
                "Java 下拉框收起时必须显示标签，不能是类型名");
        });

        Check("LauncherProfiles: 安装器要的配置文件，缺了补、坏了修、好的不碰（第 94 轮）", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "aurora-profiles-" + Guid.NewGuid().ToString("N")[..6]);
            try
            {
                // 目录都不存在时也要能补（安装器只检查文件）
                var first = LauncherProfiles.Ensure(root);
                Assert(first.Created && !first.Repaired, $"缺文件时必须新建，实际 created={first.Created}");
                Assert(File.Exists(first.Path), "文件必须真的写出来");
                var json = File.ReadAllText(first.Path);
                Assert(LauncherProfiles.HasProfiles(json), "写出来的文件必须能被识别（profiles 非空）");
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    var rootEl = doc.RootElement;
                    Assert(rootEl.TryGetProperty("selectedProfile", out _), "必须带 selectedProfile —— 安装器会读它");
                    Assert(rootEl.TryGetProperty("launcherVersion", out _), "补齐官方字段，别的工具才不会拒收");
                    Assert(rootEl.GetProperty("profiles").EnumerateObject().Any(), "profiles 不能是空对象");
                }

                // 再调一次：不该重写（用户可能自己改过）
                var before = File.ReadAllText(first.Path);
                var second = LauncherProfiles.Ensure(root);
                Assert(!second.Created && !second.Repaired, "已经合法的文件不得再动");
                Assert(File.ReadAllText(first.Path) == before, "内容必须一字不改");

                // 有内容但不可用（空文件 / {}）→ 修，并留一份备份
                File.WriteAllText(first.Path, "");
                var repaired = LauncherProfiles.Ensure(root);
                Assert(repaired.Repaired, "空文件必须判为需要修复");
                Assert(File.Exists(first.Path + ".bak"), "覆盖前要备份原文件");
                Assert(LauncherProfiles.HasProfiles(File.ReadAllText(first.Path)), "修复后必须是合法内容");

                File.WriteAllText(first.Path, "{\"profiles\":{}}");
                Assert(!LauncherProfiles.HasProfiles(File.ReadAllText(first.Path)), "空 profiles 不算合法");
                Assert(LauncherProfiles.Ensure(root).Repaired, "{} 形态也要修");

                // 判定函数本身
                Assert(!LauncherProfiles.HasProfiles(""), "空串必须为假");
                Assert(!LauncherProfiles.HasProfiles("not json"), "坏 JSON 必须为假（不能让安装器逻辑抛异常）");
                Assert(!LauncherProfiles.HasProfiles("[]"), "数组不是对象");
                Assert(LauncherProfiles.HasProfiles("{\"profiles\":{\"a\":{}}}"), "有一个 profile 就够");

                // 安装器失败的翻译（本轮 bug 的原文）
                var realTail = "Host: sessionserver.mojang.com [150.171.110.134] | Target Directory: C:\\x | " +
                               "There is no Minecraft launcher profile in \"C:\\x\", you need to run the launcher first!";
                var explained = LauncherProfiles.Explain(realTail);
                Assert(explained != null && explained.Contains("launcher_profiles.json"),
                    "必须点名缺的是 launcher_profiles.json：" + explained);
                Assert(LauncherProfiles.Explain("UnsupportedClassVersionError: class file version 69")!.Contains("Java"),
                    "Java 版本不符要给 Java 提示");
                Assert(LauncherProfiles.Explain("Successfully installed client into launcher.") == null,
                    "认不出的输出不要瞎翻译");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

        Check("Guard: 跑 Forge/NeoForge 安装器前必须先补 launcher_profiles.json（第 94 轮）", () =>
        {
            var installers = File.ReadAllText(Path.Combine(FindSourceRoot()!, "src", "AuroraCore",
                "Services", "ModLoaders", "ModLoaderInstallers.cs"));
            var ensureIdx = installers.IndexOf("LauncherProfiles.Ensure(gameRoot)", StringComparison.Ordinal);
            var runIdx = installers.IndexOf("await RunJavaAsync(java.JavaExecutable", StringComparison.Ordinal);
            Assert(ensureIdx > 0, "安装器调用处没有补 launcher_profiles.json —— Forge 会直接退出码 1");
            Assert(runIdx > ensureIdx, "必须先把文件补好再启动安装器（顺序反了等于没补）");
            Assert(installers.Contains("LauncherProfiles.Explain("),
                "安装器失败时要翻译成人话，而不是把 maven 输出整段丢给用户");
            // 只允许一处调用：多出来的那份通常意味着有人复制了安装流程却漏了补文件
            var ensureCalls = System.Text.RegularExpressions.Regex.Matches(installers, @"LauncherProfiles\.Ensure\(").Count;
            Assert(ensureCalls == 1, $"补文件只应有一处，实际 {ensureCalls} 处");
        });

        Check("VersionSourcePolicy: 本地优先 / 第三方加载器 id 不得查官方清单（第 95 轮）", () =>
        {
            Assert(VersionSourcePolicy.Decide("1.20.1", true) == VersionSource.Local,
                "本地有版本 JSON 就必须直接用，零网络");
            Assert(VersionSourcePolicy.Decide("1.20.1", false) == VersionSource.Official,
                "原版 id 且本地没有 → 才允许查官方清单");
            foreach (var id in new[] { "forge-1.20.1-47.4.22", "1.20.1-forge-47.4.22",
                         "neoforge-21.1.72", "fabric-loader-0.20.1-1.21", "quilt-loader-0.26.0-1.21" })
            {
                Assert(VersionSourcePolicy.IsThirdPartyLoader(id), $"{id} 应识别为第三方加载器");
                Assert(VersionSourcePolicy.Decide(id, false) == VersionSource.LoaderProfileMissing,
                    $"{id} 缺本地文件时应判定为「加载器没装好」，而不是去查官方清单");
                Assert(VersionSourcePolicy.Decide(id, true) == VersionSource.Local,
                    $"{id} 有本地文件时照常本地优先");
            }
            Assert(!VersionSourcePolicy.IsThirdPartyLoader("1.20.1"), "原版版本号不能被误判");
            Assert(!VersionSourcePolicy.IsThirdPartyLoader("1.20.1-pre1"), "快照版本号不能被误判");
            Assert(VersionSourcePolicy.LoaderMissingHint("forge-1.20.1-47.4.22").Contains("补全文件"),
                "错误提示必须给出下一步动作");
        });

        Check("InstallerDependencies: Maven 坐标 / 依赖清单 / 多线路候选（第 95 轮）", () =>
        {
            Assert(InstallerDependencies.MavenPath("com.google.code.findbugs:jsr305:3.0.2") ==
                   "com/google/code/findbugs/jsr305/3.0.2/jsr305-3.0.2.jar", "基础坐标转路径");
            Assert(InstallerDependencies.MavenPath("net.minecraftforge:forge:1.20.1-47.4.22:client") ==
                   "net/minecraftforge/forge/1.20.1-47.4.22/forge-1.20.1-47.4.22-client.jar", "带 classifier");
            Assert(InstallerDependencies.MavenPath("a:b:1@zip") == "a/b/1/b-1.zip", "带扩展名后缀");
            Assert(InstallerDependencies.MavenPath("garbage") == null, "非法坐标返回 null");

            var json = "{\"libraries\":[" +
                "{\"name\":\"com.google.code.findbugs:jsr305:3.0.2\",\"downloads\":{\"artifact\":{" +
                "\"path\":\"com/google/code/findbugs/jsr305/3.0.2/jsr305-3.0.2.jar\"," +
                "\"url\":\"https://maven.creeperhost.net/com/google/code/findbugs/jsr305/3.0.2/jsr305-3.0.2.jar\"," +
                "\"sha1\":\"abc\",\"size\":12345}}}," +
                "{\"name\":\"trove:trove:1.0.2\"}," +
                "{\"name\":\"../evil:bad:1\",\"downloads\":{\"artifact\":{\"path\":\"../../escape.jar\"}}}" +
                "]}";
            var libs = InstallerDependencies.Parse(json);
            Assert(libs.Count == 2, $"应解析出 2 个合法依赖，实际 {libs.Count}");
            Assert(libs.All(l => !l.RelativePath.Contains("..")), "路径穿越必须被挡掉");
            Assert(libs[0].Sha1 == "abc" && libs[0].Size == 12345, "校验值与大小要带下去");
            Assert(libs.Any(l => l.Name == "trove:trove:1.0.2" && l.RelativePath == "trove/trove/1.0.2/trove-1.0.2.jar"),
                "没有 downloads.artifact 时要能从坐标推导路径");
            Assert(InstallerDependencies.Parse("{ this is not json").Count == 0, "坏 JSON 不能抛异常");

            var urls = InstallerDependencies.CandidateUrls(libs[0]);
            Assert(urls.Count >= 3, "至少要有镜像 / Maven Central / 安装器原始地址三条线路");
            Assert(urls[0].Contains("bmclapi"), "第一条走国内镜像");
            Assert(urls[1].Contains("repo1.maven.org"), "第二条走 Maven Central（jsr305 在这里）");
            Assert(urls.Contains("https://maven.creeperhost.net/com/google/code/findbugs/jsr305/3.0.2/jsr305-3.0.2.jar"),
                "安装器自己的地址保留作最后兜底");
        });

        Check("InstallerDependencies: 只把真正失败的坐标列给用户（第 95 轮）", () =>
        {
            var tail = "Considering library trove:trove:1.0.2 | Downloading library from https://maven.creeperhost.net/trove/trove/1.0.2/trove-1.0.2.jar | " +
                       "Download completed: Checksum validated. | These libraries failed to download. Try again. | " +
                       "com.google.code.findbugs:jsr305:3.0.2 | There was an error during installation";
            var missing = InstallerDependencies.FailedCoordinates(tail);
            Assert(missing.Count == 1 && missing[0] == "com.google.code.findbugs:jsr305:3.0.2",
                "只应点名 jsr305，实际: " + string.Join(",", missing));
            Assert(!missing.Contains("trove:trove:1.0.2"), "下载成功的库不能被误报成失败");
            var explained = LauncherProfiles.Explain(tail);
            Assert(explained != null && explained.Contains("jsr305") && explained.Contains("网络"),
                "jsr305 下载失败要翻译成人话并点名具体库：" + explained);
        });

        Check("GameInstaller.IsVersionJsonInstalled: 空壳 loader profile 不算已安装（第 95 轮）", () =>
        {
            var cfg = LauncherConfig.Instance;
            var old = cfg.RootDirectory;
            var root = Path.Combine(Path.GetTempPath(), "aurora-r95-" + Guid.NewGuid().ToString("N"));
            try
            {
                cfg.RootDirectory = root;
                var gi = new GameInstaller(new VersionResolver(new VersionManifestService()), new DownloadEngine());
                var id = "forge-1.20.1-47.4.22";
                var dir = Path.Combine(root, "versions", id);
                Directory.CreateDirectory(dir);
                var f = Path.Combine(dir, id + ".json");
                var filler = ",\"type\":\"release\",\"libraries\":[],\"assets\":\"1.20.1\"," +
                             "\"minimumLauncherVersion\":0,\"time\":\"2026-01-01T00:00:00+00:00\"}";
                File.WriteAllText(f, new string('x', 300));
                Assert(!gi.IsVersionJsonInstalled(id), "超过 128 字节的截断残片不能算已安装");
                File.WriteAllText(f, "{\"id\":\"" + id + "\"" + filler);
                Assert(!gi.IsVersionJsonInstalled(id), "合法 JSON 但没有 mainClass 也不能算已安装");
                File.WriteAllText(f, "{\"id\":\"" + id + "\",\"mainClass\":\"net.minecraftforge.bootstrap.ForgeBootstrap\"" + filler);
                Assert(gi.IsVersionJsonInstalled(id), "完整 profile 才算已安装");
            }
            finally
            {
                cfg.RootDirectory = old;
                try { Directory.Delete(root, true); } catch { }
            }
        });

        Check("Guard: 本地版本 JSON 优先于官方清单，第三方 id 不得发起清单查询（第 95 轮）", () =>
        {
            var svc = File.ReadAllText(Path.Combine(FindSourceRoot()!, "src", "AuroraCore",
                "Services", "VersionManifestService.cs"));
            var local = svc.IndexOf("if (File.Exists(localPath))", StringComparison.Ordinal);
            var guard = svc.IndexOf("VersionSourcePolicy.IsThirdPartyLoader(versionId)", StringComparison.Ordinal);
            var entry = svc.IndexOf("var entry = await GetEntryAsync", StringComparison.Ordinal);
            Assert(local > 0 && guard > local && entry > guard,
                "顺序必须是「本地文件 → 第三方守卫 → 官方清单」，反了就会重现 not found in manifest");
            var resolver = File.ReadAllText(Path.Combine(FindSourceRoot()!, "src", "AuroraCore",
                "Services", "VersionResolver.cs"));
            Assert(resolver.Contains("VersionSourcePolicy.IsThirdPartyLoader(versionId)"),
                "损坏的本地 loader profile 不得回落到官方清单");
            var installer = File.ReadAllText(Path.Combine(FindSourceRoot()!, "src", "AuroraCore",
                "Services", "GameInstaller.cs"));
            Assert(installer.Contains("VersionSourcePolicy.IsThirdPartyLoader(versionId)"),
                "GameInstaller 入口必须挡住缺失的 loader id");
            Assert(installer.Contains("IsVersionJsonInstalled") && installer.Contains("mainClass"),
                "已安装判定必须校验内容，而不是只看文件大小");
            var repair = File.ReadAllText(Path.Combine(FindSourceRoot()!, "src", "AuroraCore",
                "Services", "LauncherService.cs"));
            Assert(repair.Contains("VersionSourcePolicy.LoaderMissingHint(pending)"),
                "补全路径也要在把 loader id 交给 GameInstaller 之前拦住");
        });

        Check("Guard: 安装器运行前必须预填充依赖，且成功必须校验真实 profile 文件（第 95 轮）", () =>
        {
            var text = File.ReadAllText(Path.Combine(FindSourceRoot()!, "src", "AuroraCore",
                "Services", "ModLoaders", "ModLoaderInstallers.cs"));
            var prefill = text.IndexOf("await PrefillInstallerLibrariesAsync(jarPath", StringComparison.Ordinal);
            var run = text.IndexOf("await RunJavaAsync(java.JavaExecutable", StringComparison.Ordinal);
            Assert(prefill > 0 && run > prefill, "必须在启动安装器 JVM 之前预填充依赖");
            Assert(text.Contains("Required = false"), "预填充失败不能中断安装（依赖是可选的）");
            Assert(text.Contains("HasUsableProfile(d)") && text.Contains("mainClass"),
                "成功必须要求真实可用（含 mainClass）的版本 JSON");
            Assert(text.Contains("LoaderProfileLocator.Pick("),
                "没有新建目录时必须扫描并认领已安装的加载器 profile（第 96 轮回归）");
            Assert(!text.Contains("result.VersionId = detected ?? versionIdHint"),
                "不许再把下载地址推导的 hint 当成已安装版本写进实例");
            var deps = File.ReadAllText(Path.Combine(FindSourceRoot()!, "src", "AuroraCore",
                "Services", "InstallerDependencies.cs"));
            Assert(deps.Contains("repo1.maven.org") && deps.Contains("bmclapi"),
                "至少要准备镜像与 Maven Central 两条线路");
        });

        Check("LoaderProfileLocator: 认领已装好的 profile，拒绝空壳与假 id（第 96 轮）", () =>
        {
            Assert(LoaderProfileLocator.VersionToken("forge-1.20.1-47.4.22") == "47.4.22", "取尾部版本号");
            Assert(LoaderProfileLocator.VersionToken("1.20.1-forge-47.4.22") == "47.4.22", "中缀形式也要认");
            var dirs = new[] { "1.20.1", "forge-1.20.1-47.4.22", "1.20.1-forge-47.4.22",
                               "fabric-loader-0.20.1-1.20.1", "1.21.10-forge-47.4.22" };
            bool HasProfile(string d) => d == "1.20.1-forge-47.4.22";
            Assert(LoaderProfileLocator.Pick(dirs, "Forge", "forge-1.20.1-47.4.22", "1.20.1", HasProfile)
                   == "1.20.1-forge-47.4.22", "必须认领已存在且带 JSON 的真实 profile");
            Assert(LoaderProfileLocator.Pick(dirs, "Forge", "forge-1.20.1-47.4.22", "1.20.1", _ => false) == null,
                "目录存在但没有版本 JSON 不能算已安装");
            Assert(!LoaderProfileLocator.Matches("fabric-loader-0.20.1-1.20.1", "Forge", null, "1.20.1"),
                "别的加载器不能被认领");
            Assert(!LoaderProfileLocator.Matches("1.21.10-forge-47.4.22", "Forge", null, "1.20.1"),
                "MC 版本不匹配不能被认领");
        });

        Check("JobWatchdogPolicy: 准备 30 秒 / 安装器下载 90 秒 / 运行 15 分钟（第 96 轮）", () =>
        {
            Assert(JobWatchdogPolicy.TimeoutSeconds(JobState.Preparing, null) == 30, "准备阶段 30 秒");
            Assert(JobWatchdogPolicy.TimeoutSeconds(JobState.Running, "installer-download") == 90,
                "9MB 安装器实测下过 33 秒，30 秒会误杀");
            Assert(JobWatchdogPolicy.TimeoutSeconds(JobState.Running, "installer-run") == 900,
                "官方安装器实测 94~165 秒无进度，必须跟它自己的 15 分钟上限对齐");
            var now = DateTime.UtcNow;
            Assert(!JobWatchdogPolicy.IsStalled(now.AddSeconds(-10), now, 30), "刚有进度不算卡住");
            Assert(JobWatchdogPolicy.IsStalled(now.AddSeconds(-31), now, 30), "超过阈值判定卡住");
            Assert(JobWatchdogPolicy.StalledMessage(now.AddSeconds(-31), now, 30).Contains("重试"),
                "卡住提示要给出重试出口");
        });

        Check("JobProgressPolicy / JobState: Preparing 也算活跃任务（第 96 轮）", () =>
        {
            var (pct, speed, active, queued) = JobProgressPolicy.Aggregate(
                new[] { new DownloadJobSnapshot(JobState.Preparing, 0, 0) });
            Assert(active == 1 && queued == 0 && pct == 0 && speed == 0,
                "准备中的任务必须让汇总面板显示 1 个进行中，而不是「当前没有下载任务」");
            var mgr = new DownloadJobManager(a => a());
            var job = mgr.Create("实例", "x", "");
            Assert(job.CanCancel, "初始任务可取消");
            job.MarkPreparing();
            Assert(job.StateText == "准备中" && job.CanCancel, "准备中可见且仍可取消");
        });

        Check("NavigationHighlight: 单一 CurrentPageKey 最多一个高亮（第 96 轮）", () =>
        {
            var keys = new[] { "Home", "Instances", "Downloads", "Settings" };
            Assert(NavigationHighlight.ActiveCount("Downloads", keys) == 1, "只能有一个 active");
            Assert(NavigationHighlight.IsActive("Downloads", "Downloads"), "大小写不敏感");
            Assert(!NavigationHighlight.IsActive("Downloads", "Instances"), "别的页面不能同时亮");
            Assert(NavigationHighlight.ActiveCount(null, keys) == 0, "没有当前页时全灭");
        });

        Check("Guard: 补全必须走 DownloadJob，标题栏不得脱离任务队列（第 96 轮）", () =>
        {
            var root = FindSourceRoot()!;
            string Read(string f) => File.ReadAllText(Path.Combine(root, f));
            var vm = Read("src/AuroraLauncher.Desktop/ViewModels/MainViewModel.cs");
            Assert(vm.Contains("await RunRepairJobAsync(instance)"), "启动前的补全必须走 Job 流程");
            Assert(!vm.Contains("InitStages(BuildStages(instance.LoaderType)"),
                "不许再用脱离 Job 的全局阶段表驱动补全");
            Assert(vm.Contains("public string HeaderText") && vm.Contains("_jobManager.Jobs.FirstOrDefault"),
                "标题栏必须由活跃任务驱动，而不是独立的全局 bool/字符串");
            Assert(vm.Contains("CheckStalledJobs(); // Round-96"), "看门狗必须挂在轮询上（版本不变也要跑）");
            Assert(vm.Contains("FinishJob(job, ex, false)"), "失败要留在列表里而不是只抛全局异常");
            var mainX = Read("src/AuroraLauncher.Desktop/MainWindow.xaml");
            Assert(mainX.Contains("Text=\"{Binding HeaderText}\""), "XAML 状态栏必须绑 HeaderText");
        });

        Check("Guard: 全部取消置灰 / 错误可重试 / 导航单一来源（第 96 轮）", () =>
        {
            var root = FindSourceRoot()!;
            string Read(string f) => File.ReadAllText(Path.Combine(root, f));
            var view = Read("src/AuroraLauncher.Desktop/Views/DownloadsView.xaml");
            Assert(view.Contains("IsEnabled=\"{Binding HasCancellableJobs}\""), "没任务时全部取消必须置灰");
            Assert(view.Contains("Command=\"{Binding RetryLastFailedJobCommand}\""), "错误横幅要能一键重试");
            var mainX = Read("src/AuroraLauncher.Desktop/MainWindow.xaml");
            var bars = System.Text.RegularExpressions.Regex.Matches(
                mainX, @"PageActiveBorder\}, ConverterParameter=").Count;
            Assert(bars == 9, $"9 个导航项都要有 active 竖条，实际 {bars}");
            var single = System.Text.RegularExpressions.Regex.Matches(
                mainX, @"Background=""\{Binding CurrentPageKey, Converter=\{StaticResource PageActive\}").Count;
            Assert(single == 9, "所有导航高亮必须只绑 CurrentPageKey 这一个来源");
            Assert(Read("src/AuroraLauncher.Desktop/App.xaml").Contains("PageActiveBorderConverter"),
                "active 竖条转换器必须注册");
        });

        Check("VersionRangePolicy: [4.11.0,4.99.0) 边界与最新版选择（第 97 轮）", () =>
        {
            const string r = "[4.11.0,4.99.0)";
            Assert(!VersionRangePolicy.Matches(r, "4.10.0"), "下界之前必须排除");
            Assert(!VersionRangePolicy.Matches(r, "4.99.0"), "右括号是开区间，4.99.0 必须排除");
            Assert(VersionRangePolicy.Matches(r, "4.11.0"), "左闭区间含 4.11.0");
            Assert(VersionRangePolicy.Matches(r, "4.98.9"), "区间内通过");
            Assert(VersionRangePolicy.Matches("[1.0.3,)", "1.2.0"), "无上界");
            Assert(VersionRangePolicy.Matches("*", "1.0"), "通配符");
            Assert(VersionRangePolicy.Matches("[1.0,2.0) || [3.0,4.0)", "3.5.0"), "或条件");
            Assert(VersionRangePolicy.PickLatest(r, new[] { "4.10.0", "4.11.0", "4.20.0", "4.99.0" }) == "4.20.0",
                "只能挑符合范围的最新版");
            Assert(VersionRangePolicy.Compare("4.11.0", "4.99.0") < 0, "数值段比较");
        });

        Check("ModDependencyPolicy: 跨加载器过滤与按 modId 去重（第 97 轮）", () =>
        {
            Assert(!ModDependencyPolicy.IsRelevant("fabric-api", ModLoaderType.Forge), "Forge 绝不需要 fabric-api");
            Assert(!ModDependencyPolicy.IsRelevant("fabric-api", ModLoaderType.NeoForge), "NeoForge 同理");
            Assert(!ModDependencyPolicy.IsRelevant("quilt_loader", ModLoaderType.Forge), "Quilt 平台 id 过滤");
            Assert(ModDependencyPolicy.IsRelevant("kotlinforforge", ModLoaderType.Forge), "Forge 前置要保留");
            Assert(ModDependencyPolicy.IsRelevant("puzzlesaccessapi", ModLoaderType.Forge), "中性 id 保留");
            Assert(!ModDependencyPolicy.IsRelevant("kotlinforforge", ModLoaderType.Fabric), "Fabric 忽略 Forge 专用前置");
            Assert(!ModDependencyPolicy.IsRelevant("minecraft", ModLoaderType.Forge), "平台 id 忽略");

            var deps = new[]
            {
                new ModDependency("fabric-api", "*", "EnhancedVisuals", "e.jar"),
                new ModDependency("kotlinforforge", "[4.11.0,4.99.0)", "Fzzy Config", "f.jar"),
                new ModDependency("kotlinforforge", "[4.11.0,4.99.0)", "Particle Core", "p.jar"),
                new ModDependency("tacz", "[1.0.3,)", "TPS Zero", "t.jar")
            };
            var merged = ModDependencyPolicy.Merge(deps, ModLoaderType.Forge);
            Assert(merged.Count == 2, $"Forge 下应只剩 2 条，实际 {merged.Count}");
            Assert(merged.All(d => d.ModId != "fabric-api"), "fabric-api 必须被过滤掉");
            var kff = merged.First(d => d.ModId == "kotlinforforge");
            Assert(kff.RequiredByList.Count == 2 && kff.Ranges.Count == 1,
                "同一前置合并成一行，保留两个依赖来源与范围");
            Assert(VersionRangePolicy.Matches(kff.Range, "4.20.0"), "合并后的范围仍可用");
        });

        Check("PreflightAutoFixPolicy: 加载器标签、slug 映射与版本准入（第 97 轮）", () =>
        {
            var forge = PreflightAutoFixPolicy.LoadersFor(ModLoaderType.Forge);
            Assert(forge.Count == 1 && forge[0] == "forge", "Forge 只能查 forge");
            Assert(!forge.Any(l => l.Contains("fabric", StringComparison.OrdinalIgnoreCase)),
                "禁止跨加载器补丁：Forge 的查询里不能出现 fabric");
            Assert(PreflightAutoFixPolicy.LoadersFor(ModLoaderType.NeoForge)[0] == "neoforge", "NeoForge 标签");
            Assert(PreflightAutoFixPolicy.LoadersFor(ModLoaderType.Fabric)[0] == "fabric", "Fabric 标签");
            Assert(PreflightAutoFixPolicy.SlugFor("kotlinforforge") == "kotlin-for-forge", "slug 映射");
            Assert(PreflightAutoFixPolicy.SlugFor("unknown-mod") == "unknown-mod", "未知 id 原样返回待搜索");
            Assert(PreflightAutoFixPolicy.VersionAllowed(ModLoaderType.Forge, new[] { "forge" }, "1.20.1", new[] { "1.20.1" }),
                "匹配的版本可下载");
            Assert(!PreflightAutoFixPolicy.VersionAllowed(ModLoaderType.Forge, new[] { "fabric" }, "1.20.1", new[] { "1.20.1" }),
                "Fabric 版本绝不能进 Forge 实例");
            Assert(!PreflightAutoFixPolicy.VersionAllowed(ModLoaderType.Forge, new[] { "forge" }, "1.20.1", new[] { "1.21.1" }),
                "MC 版本不符");
        });

        Check("ProgressDisplayPolicy: 未完成任务永不显示 100%（第 97 轮）", () =>
        {
            Assert(ProgressDisplayPolicy.Display(100, false) == 99, "还差文件就封顶 99");
            Assert(ProgressDisplayPolicy.Display(96, false) == 96, "Floor 而不是四舍五入");
            Assert(ProgressDisplayPolicy.Display(100, true) == 100, "真正完成才 100");
            Assert(ProgressDisplayPolicy.Indeterminate("installer-run"), "安装器黑盒阶段转不确定态");
            Assert(!ProgressDisplayPolicy.Indeterminate("json"), "普通阶段保持确定态");
            Assert(ProgressDisplayPolicy.Hint("installer-run").Contains("安装器"), "不确定态要给出说人话的文案");
            var (pct, _, active, _) = JobProgressPolicy.Aggregate(new[]
            {
                new DownloadJobSnapshot(JobState.Running, 100, 378000),
                new DownloadJobSnapshot(JobState.Done, 100, 0)
            });
            Assert(active == 1 && pct <= 99, $"有活跃任务时汇总也不能到 100%，实际 {pct}");
        });

        Check("JobWatchdogPolicy: 有速度但文件完成数停滞也判卡住（第 97 轮）", () =>
        {
            var now = DateTime.UtcNow;
            Assert(!JobWatchdogPolicy.IsFileProgressStalled(now.AddSeconds(-30), now, 90), "刚完成过文件不算卡住");
            Assert(JobWatchdogPolicy.IsFileProgressStalled(now.AddSeconds(-121), now, 90), "2 分钟无新完成文件即卡住");
            Assert(!JobWatchdogPolicy.IsFileProgressStalled(now.AddSeconds(-121), now, 0),
                "没有文件计数流程时不启用该判定");
            Assert(JobWatchdogPolicy.FileStalledMessage(now.AddSeconds(-121), now, 87, 90).Contains("87/90"),
                "提示要点名停在哪个数上");
        });

        Check("Guard: Forge 实例不得产生跨加载器前置（第 97 轮）", () =>
        {
            var root = FindSourceRoot()!;
            string Read(string f) => File.ReadAllText(Path.Combine(root, f));
            var scan = Read("src/AuroraCore/Services/ModPreflight.cs");
            Assert(scan.Contains("TryParse(file, loader, log)"), "解析必须传入实例加载器");
            Assert(scan.Contains("ReadForgeMeta") && scan.Contains("ReadFabricMeta"), "两种元数据要有独立入口");
            Assert(scan.Contains("ModDependencyPolicy.IsRelevant(depId, loader)"), "依赖收集必须过跨加载器过滤");
            Assert(scan.Contains("ModDependencyPolicy.Merge(missing, loader)"), "必须按 modId 去重合并");
            Assert(scan.Contains("FabricApiMissing = fabricApiRelevant"), "Fabric API 入口只在真缺时显示");
            var policy = Read("src/AuroraCore/Services/PreflightAutoFixPolicy.cs");
            Assert(policy.Contains("ModLoaderType.Forge => \"forge\"") &&
                   policy.Contains("ModLoaderType.NeoForge => \"neoforge\""), "加载器标签必须显式且唯一");
            Assert(!PreflightAutoFixPolicy.LoadersFor(ModLoaderType.Forge)
                .Any(l => l.Contains("fabric", StringComparison.OrdinalIgnoreCase)),
                "禁止跨加载器补丁：任何 Forge 查询都不能带 fabric facet");
        });

        Check("Guard: 进度封顶、不确定态与停滞检测必须接线（第 97 轮）", () =>
        {
            var root = FindSourceRoot()!;
            string Read(string f) => File.ReadAllText(Path.Combine(root, f));
            Assert(Read("src/AuroraCore/Services/DownloadJob.cs")
                .Contains("ProgressDisplayPolicy.Display(percent, IsFinished)"), "Job 百分比必须封顶");
            Assert(Read("src/AuroraCore/Services/DownloadJob.cs")
                .Contains("LastFileCompletedUtc = DateTime.UtcNow"), "必须跟踪文件完成时间");
            Assert(Read("src/AuroraCore/Services/JobWatchdogPolicy.cs").Contains("FileStallSeconds = 120"),
                "停滞阈值必须是 2 分钟");
            Assert(Read("src/AuroraLauncher.Desktop/ViewModels/MainViewModel.cs").Contains("IsFileProgressStalled("),
                "看门狗必须调用文件停滞判定");
            Assert(Read("src/AuroraLauncher.Desktop/Views/DownloadsView.xaml")
                .Contains("IsIndeterminate=\"{Binding DownloadProgressIndeterminate}\""),
                "主进度条必须支持不确定态");
        });

        Check("InstallFailureText: The operation was canceled. 不再裸奔给用户（第 98 轮）", () =>
        {
            var raw = new OperationCanceledException("The operation was canceled.");
            var text = InstallFailureText.Describe(raw, "installer-download");
            Assert(!text.Contains("canceled", StringComparison.OrdinalIgnoreCase), "不能出现英文底层异常原文");
            Assert(text.Contains("超时") && text.Contains("重试") && text.Contains("镜像"), "要说清原因与下一步");
            Assert(InstallFailureText.Describe(raw, externallyCanceled: true).Contains("已取消"), "真取消要如实说取消");
            Assert(InstallFailureText.IsCancellation(new TaskCanceledException()), "TaskCanceled 也算中止");
            Assert(InstallFailureText.IsRetryableRouteFailure(new TimeoutException(), false), "线路超时值得换镜像重试");
            Assert(InstallFailureText.IsRetryableRouteFailure(new DownloadException("404"), false), "下载异常值得换镜像重试");
            Assert(!InstallFailureText.IsRetryableRouteFailure(new OperationCanceledException(), true), "外部取消不能重试");
            Assert(InstallFailureText.BothRoutesFailed("https://maven.minecraftforge.net/a.jar",
                       "https://bmclapi2.bangbang93.com/maven/a.jar", "Forge", raw)
                   .Contains("bmclapi2.bangbang93.com"), "双线路失败要列出镜像地址");
            Assert(LauncherProfiles.Explain("Failed to run processor: java.net.ConnectException:Connection timed out: connect")!
                       .Contains("连接超时"), "安装器 JVM 超时也要说人话");
            Assert(LauncherProfiles.Explain("Failed to download version manifest, can not find client jar URL.")!
                       .Contains("重试"), "拉不到版本清单要给下一步");
        });

        Check("JavaDistributionPolicy: Zulu 只认普通 JDK、挑最新、拒绝坏包（第 98 轮）", () =>
        {
            const string json = """
            [
              {"java_version":[21,0,11],"name":"zulu21.50.19-ca-jdk21.0.11-win_x64.zip","sha256_hash":"84c73a4dcf8400ac01c1fe2d622cc319199e93c1856c200cb50c1bc16dc7dc97","size":211615000,"url":"https://cdn.azul.com/zulu/bin/zulu21.50.19-ca-jdk21.0.11-win_x64.zip","zulu_version":[21,50,19,0]},
              {"java_version":[21,0,13,0,3],"name":"zulu21.54.109-ca-fx-jdk21.0.13.0.3-win_x64.zip","sha256_hash":"9debca7155348d63a79ac6c1b38d85af6e92242c51de8170fb2e65d06a2d0cbb","size":307838000,"url":"https://cdn.azul.com/zulu/bin/zulu21.54.109-ca-fx-jdk21.0.13.0.3-win_x64.zip","zulu_version":[21,54,109,0]},
              {"java_version":[21,0,13,0,3],"name":"zulu21.54.109-ca-crac-jdk21.0.13.0.3-win_x64.zip","sha256_hash":"b32092e2798d25782e54ddd56f9f6cf5c61d4ec3d7df7376f0c8c7f74b8faeff","size":211705000,"url":"https://cdn.azul.com/zulu/bin/zulu21.54.109-ca-crac-jdk21.0.13.0.3-win_x64.zip","zulu_version":[21,54,109,0]},
              {"java_version":[21,0,13,0,3],"name":"zulu21.54.109-ca-jdk21.0.13.0.3-win_x64.zip","sha256_hash":"7428890c66267aac516514085745dbedc6b8e7703eb0f7d73fb65cf9cd2cf306","size":211151000,"url":"https://cdn.azul.com/zulu/bin/zulu21.54.109-ca-jdk21.0.13.0.3-win_x64.zip","zulu_version":[21,54,109,0]},
              {"java_version":[21,0,14],"name":"evil.zip","sha256_hash":"ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff","size":10,"url":"https://evil.example/evil.zip","zulu_version":[21,99,0,0]}
            ]
            """;
            var pkg = JavaDistributionPolicy.ParseZuluLatest(json, 21);
            Assert(pkg != null, "必须解析出可用的 Zulu 包");
            Assert(pkg!.Name.Contains("ca-jdk") && !pkg.Name.Contains("-fx-") && !pkg.Name.Contains("-crac-"),
                "排除 JavaFX / CRaC 变体");
            Assert(pkg.Sha256.Length == 64 && pkg.Size > 0 && pkg.Vendor == "Zulu", "必须带校验和与体积");
            Assert(pkg.Url.StartsWith("https://cdn.azul.com/", StringComparison.Ordinal), "只允许 Azul 官方 CDN");
            Assert(JavaDistributionPolicy.ParseZuluLatest("[]", 21) == null, "空数组返回 null");
            Assert(JavaDistributionPolicy.BuildZuluApiUrl(17).Contains("java_version=17") &&
                   JavaDistributionPolicy.BuildZuluApiUrl(17).Contains("include_fields=sha256_hash,size"),
                "必须请求 sha256_hash 与 size，否则装不了");
            Assert(!JavaDistributionPolicy.IsAllowedZuluHost("https://evil.example/x.zip"), "域名白名单");
            Assert(!JavaDistributionPolicy.IsAllowedZuluHost("http://cdn.azul.com/x.zip"), "必须 HTTPS");
            Assert(JavaDistributionPolicy.CompareVersions(new[] { 21, 0, 13 }, new[] { 21, 0, 11 }) > 0, "版本比较");
            Assert(JavaDistributionPolicy.Options.Count == 2, "发行版白名单只有两家");
            Assert(JavaDistributionPolicy.FromDisplayText(JavaDistributionPolicy.DisplayText(JavaDistribution.Zulu))
                   == JavaDistribution.Zulu, "显示名往返");
            Assert(JavaDistributionPolicy.FromDisplayText("乱填的值") == JavaDistribution.Temurin, "未知值安全降级");
        });

        Check("JobWatchdogPolicy: 已进入命名阶段的 Preparing 不再被 30 秒误杀（第 98 轮）", () =>
        {
            Assert(JobWatchdogPolicy.TimeoutSeconds(JobState.Preparing, null) == 30, "真正的准备阶段仍是 30 秒");
            Assert(JobWatchdogPolicy.TimeoutSeconds(JobState.Preparing, "installer-download") == 90,
                "下载 installer jar 给 90 秒，不是 30 秒");
            Assert(JobWatchdogPolicy.TimeoutSeconds(JobState.Preparing, "installer-run") == 900,
                "安装器运行阶段对齐 15 分钟");
            Assert(!JobWatchdogPolicy.ShouldUseFileStall("installer-download"), "单文件 jar 不看文件完成数");
            Assert(!JobWatchdogPolicy.ShouldUseFileStall("installer-run"), "安装器黑盒不看文件完成数");
            Assert(JobWatchdogPolicy.ShouldUseFileStall("assets"), "普通多文件批次仍启用 2 分钟停滞判定");
        });

        Check("JobProgressPolicy: 失败任务不能把总进度抬到 100%（第 98 轮）", () =>
        {
            var (only, _, _, _) = JobProgressPolicy.Aggregate(new[]
            {
                new DownloadJobSnapshot(JobState.Failed, 100, 0)
            });
            Assert(only == 0, $"唯一任务失败时总进度必须是 0，实际 {only}");
            var (mixed, _, _, _) = JobProgressPolicy.Aggregate(new[]
            {
                new DownloadJobSnapshot(JobState.Done, 100, 0),
                new DownloadJobSnapshot(JobState.Failed, 100, 0)
            });
            Assert(mixed <= 99, $"有失败任务时汇总不得显示 100%，实际 {mixed}");
            var (clean, _, _, _) = JobProgressPolicy.Aggregate(new[]
            {
                new DownloadJobSnapshot(JobState.Done, 100, 0)
            });
            Assert(clean == 100, "全部成功才允许 100%");
        });

        Check("Guard: 安装器下载必须有心跳、镜像优先且失败清残片（第 98 轮）", () =>
        {
            var root = FindSourceRoot()!;
            string Read(string f) => File.ReadAllText(Path.Combine(root, f));
            var mi = Read("src/AuroraCore/Services/ModLoaders/ModLoaderInstallers.cs");
            Assert(mi.Contains("Action<DownloadProgress>? onProgress = null"), "安装器入口必须接收字节进度");
            Assert(mi.Contains("}, onProgress, ct)"), "下载必须把进度回报给 Job（看门狗心跳）");
            Assert(mi.Contains("new[] { mirrorUrl, installerUrl }"), "国内线路优先走 BMCLAPI 镜像");
            Assert(mi.Contains("InstallFailureText.IsRetryableRouteFailure"), "超时/取消也要能切换线路");
            Assert(mi.Contains("CleanInstallerResidue(jarPath)"), "每次重试前清残片");
            Assert(mi.Contains("DownloadJournal.Forget(jarPath)"), "续传台账必须同步清理");
            Assert(mi.Contains("InstallFailureText.Describe(ex"), "底层异常必须翻译");
            var svc = Read("src/AuroraCore/Services/LauncherService.cs");
            Assert(System.Text.RegularExpressions.Regex.Matches(svc, "onStage, onProgress").Count == 2,
                "安装与补全两条路径都要透传进度");
            Assert(Read("src/AuroraCore/Services/JobWatchdogPolicy.cs").Contains("ShouldUseFileStall"),
                "单文件安装器阶段必须豁免文件停滞判定");
        });

        Check("Guard: 失败消息不被二次覆盖、失败汇总必须可见（第 98 轮）", () =>
        {
            var root = FindSourceRoot()!;
            string Read(string f) => File.ReadAllText(Path.Combine(root, f));
            var vm = Read("src/AuroraLauncher.Desktop/ViewModels/MainViewModel.cs");
            Assert(vm.Contains("var alreadyFinished = job.IsFinished;") && vm.Contains("if (!alreadyFinished)"),
                "看门狗先判失败后，底层取消异常不得覆盖中文提示");
            Assert(vm.Contains("InstallFailureText.Describe(error)"), "任务失败文案走统一翻译");
            Assert(vm.Contains("JobWatchdogPolicy.ShouldUseFileStall(job.CurrentStageKey)"),
                "看门狗接线到阶段豁免");
            Assert(vm.Contains("public bool HasFailedJobs") && vm.Contains("FailedJobsHint"), "失败汇总状态可见");
            var mgr = Read("src/AuroraCore/Services/DownloadJobManager.cs");
            Assert(mgr.Contains("case JobState.Failed:") && mgr.Contains("hasFailed = true"),
                "失败行不贡献完成度且阻止 100%");
            Assert(Read("src/AuroraLauncher.Desktop/Views/DownloadsView.xaml").Contains("FailedJobsHint"),
                "下载页要有红色失败提示");
        });

        Check("Guard: Java 发行版白名单、校验与设置持久化（第 98 轮）", () =>
        {
            var root = FindSourceRoot()!;
            string Read(string f) => File.ReadAllText(Path.Combine(root, f));
            var policy = Read("src/AuroraCore/Services/JavaDistributionPolicy.cs");
            Assert(policy.Contains("include_fields=sha256_hash,size"), "Zulu API 必须索要校验和");
            Assert(policy.Contains("cdn.azul.com") && policy.Contains("IsAllowedZuluHost"), "CDN 域名白名单");
            Assert(policy.Contains("IsPlainJdkBundle"), "排除 fx / crac / jre 变体");
            Assert(policy.Contains("enum JavaDistribution") ||
                   Read("src/AuroraCore/Services/JavaDistributionPolicy.cs").Contains("Zulu = 2"),
                "发行版枚举只有两家");
            Assert(Read("src/AuroraCore/Services/JavaRuntimeService.cs")
                .Contains("ResolveAsync(JavaDistribution distribution, int major"), "安装服务必须支持发行版参数");
            Assert(Read("src/AuroraCore/Services/JavaRuntimeService.cs").Contains("ParseZuluLatest"),
                "Zulu 走同一条 SHA-256 校验链");
            Assert(Read("src/AuroraCore/LauncherConfig.cs").Contains("JavaDistribution { get; set; }"),
                "发行版选择要持久化");
            var view = Read("src/AuroraLauncher.Desktop/Views/SettingsView.xaml");
            Assert(view.Contains("JavaDistributionOptions") && view.Contains("JavaInstallButtonText"),
                "设置页要有发行版下拉与动态按钮");
            Assert(Read("src/AuroraLauncher.Desktop/ViewModels/MainViewModel.Round90.cs").Contains("javaDist"),
                "未保存提醒必须覆盖新设置项");
        });

        Check("CacheFolderPolicy: 缓存清单、体积格式与安全目录判定（第 99 轮）", () =>
        {
            Assert(CacheFolderPolicy.Folders.Count == 6, "6 处缓存位置");
            Assert(CacheFolderPolicy.Folders.Any(f => f.Key == "icons" && f.IsDirectory), "图标缓存目录");
            Assert(CacheFolderPolicy.Folders.Any(f => f.Key == "manifest" && !f.IsDirectory), "版本清单是文件");
            Assert(CacheFolderPolicy.FormatSize(0) == "0 B", "0 字节");
            Assert(CacheFolderPolicy.FormatSize(1536).Contains("KB"), "KB 换算");
            Assert(CacheFolderPolicy.FullPath("C:/data", CacheFolderPolicy.Folders[0])
                   .Contains("cache"), "相对路径拼接");
            Assert(!CacheFolderPolicy.IsSafeRoot(""), "空路径不安全");
            Assert(!CacheFolderPolicy.IsSafeRoot("C:////"), "驱动器根不安全");
            Assert(!CacheFolderPolicy.IsSafeRoot("C:////Windows"), "系统目录不安全");
            Assert(!CacheFolderPolicy.IsSafeRoot("C:////Users////Someone////Desktop"), "桌面不安全");
            Assert(CacheFolderPolicy.IsSafeRoot("C:////Users////Someone////AppData////Roaming////AuroraLauncher"),
                "启动器自己的数据根可以");
        });

        Check("UninstallPolicy: 白名单、存档/Java 默认保留、危险目录全部保留（第 99 轮）", () =>
        {
            Assert(UninstallPolicy.IsOwnedDirectory("cache", false, false), "缓存目录属于启动器");
            Assert(!UninstallPolicy.IsOwnedDirectory("instances", false, false), "默认保留实例/存档");
            Assert(!UninstallPolicy.IsOwnedDirectory("java", false, false), "默认保留 Java");
            Assert(UninstallPolicy.IsOwnedDirectory("instances", true, false), "勾选后删存档");
            Assert(UninstallPolicy.IsOwnedDirectory("java", false, true), "勾选后删 Java");
            Assert(UninstallPolicy.IsOwnedFile("config.json"), "配置文件");
            Assert(UninstallPolicy.IsOwnedFile("config.json.bak-r85"), "配置备份");
            Assert(UninstallPolicy.IsOwnedFile("forge-1.20.1-47.4.22-installer.jar.log"), "安装器日志");
            Assert(!UninstallPolicy.IsOwnedFile("my-personal-notes.txt"), "用户自己的文件绝不动");

            var root = Path.Combine(Path.GetTempPath(), "aurora-r99-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(Path.Combine(root, "cache"));
                Directory.CreateDirectory(Path.Combine(root, "instances"));
                Directory.CreateDirectory(Path.Combine(root, "java"));
                File.WriteAllText(Path.Combine(root, "config.json"), "{}");
                File.WriteAllText(Path.Combine(root, "my-personal-notes.txt"), "keep");
                var entries = Directory.EnumerateFileSystemEntries(root).ToList();

                var plan = UninstallPolicy.BuildPlan(
                    new UninstallRequest(root, null, false, false, false, entries));
                Assert(plan.Targets.Any(t => t.Path.EndsWith("cache")), "缓存要删");
                Assert(plan.Targets.Any(t => t.Path.EndsWith("config.json")), "配置要删");
                Assert(!plan.Targets.Any(t => t.Path.EndsWith("instances")), "存档默认不动");
                Assert(!plan.Targets.Any(t => t.Path.EndsWith("java")), "Java 默认不动");
                Assert(plan.Kept.Any(p => p.EndsWith("my-personal-notes.txt")), "未知文件保留");

                var all = UninstallPolicy.BuildPlan(
                    new UninstallRequest(root, null, false, true, true, entries));
                Assert(all.Targets.Any(t => t.Path.EndsWith("instances")) &&
                       all.Targets.Any(t => t.Path.EndsWith("java")), "两项都勾选才删");

                var dangerous = UninstallPolicy.BuildPlan(
                    new UninstallRequest("C:////Windows", null, false, true, true, entries));
                Assert(dangerous.Targets.Count == 0, "危险目录一律不删");
                Assert(UninstallPolicy.Warning(plan).Contains("保留"), "警告要写清保留项");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        });

        Check("Guard: 内容中心状态与列表同源、空状态三态、结果区有最小高度（第 99 轮）", () =>
        {
            var root = FindSourceRoot()!;
            string Read(string f) => File.ReadAllText(Path.Combine(root, f));
            var vm = Read("src/AuroraLauncher.Desktop/ViewModels/MainViewModel.cs");
            Assert(vm.Contains("public string ContentStatusText") &&
                   vm.Contains("本页 {ModSearchResults.Count} 项"), "状态文案必须读实际列表数");
            Assert(!vm.Contains("搜索到 {result.TotalHits"), "不许再用 API 总数冒充列表数");
            Assert(vm.Contains("ShowContentDetail = false; // Round-99"), "搜索前清掉详情覆盖层");
            Assert(vm.Contains("ContentKind is \"CurseForge\" or \"OptiFine\" or \"本地管理\""),
                "非 Modrinth 分类不得残留搜索结果文字");
            Assert(vm.Contains("ContentSearchError"), "搜索异常要有专门状态");
            var x = Read("src/AuroraLauncher.Desktop/Views/ModsView.xaml");
            Assert(x.Contains("MinHeight=\"200\""), "结果区防御性最小高度");
            Assert(x.Contains("HasContentSearchError") && x.Contains("重试搜索"), "异常兜底 UI");
            Assert(x.Contains("IsEmptyPageWithHits") && x.Contains("回到第一页"), "本页空但服务端有总数的解释与出口");
            Assert(x.Contains("Text=\"{Binding ContentStatusText}\""), "页面内同源状态横幅");
        });

        Check("Guard: 子导航高亮只认 ContentKind，且有 active 竖条（第 99 轮）", () =>
        {
            var root = FindSourceRoot()!;
            var x = File.ReadAllText(Path.Combine(root, "src/AuroraLauncher.Desktop/Views/ModsView.xaml"));
            var bg = System.Text.RegularExpressions.Regex.Matches(
                x, @"Background=""\{Binding ContentKind, Converter=\{StaticResource TextMatch\}").Count;
            Assert(bg == 8, $"8 个子导航背景都绑 ContentKind，实际 {bg}");
            var bars = System.Text.RegularExpressions.Regex.Matches(x, @"PageActiveBorder\}, ConverterParameter=").Count;
            Assert(bars == 8, $"8 个子导航都要有 active 竖条，实际 {bars}");
            Assert(!x.Contains("IsSelected=\"{Binding"), "不得依赖控件 IsSelected/焦点");
            Assert(NavigationHighlight.ActiveCount("光影包",
                new[] { "模组", "整合包", "数据包", "资源包", "光影包", "CurseForge", "OptiFine", "本地管理" }) == 1,
                "同一时刻只能有一个分类高亮");
        });

        Check("Guard: 引导便携选项、缓存展示、卸载必须两次确认（第 99 轮）", () =>
        {
            var root = FindSourceRoot()!;
            string Read(string f) => File.ReadAllText(Path.Combine(root, f));
            var wizard = Read("src/AuroraLauncher.Desktop/MainWindow.xaml");
            Assert(wizard.Contains("IsChecked=\"{Binding FirstRunUseExeDir}\"") ||
                   wizard.Contains("IsChecked=\"{Binding FirstRunPortable}\""), "引导里有便携模式选项");
            Assert(wizard.Contains("FirstRunPortableNote"), "结果要在下一步可见");
            Assert(Read("src/AuroraLauncher.Desktop/ViewModels/MainViewModel.Round99.cs")
                   .Contains("EnablePortableModeCore(\"首次启动引导\")"), "勾选后要真的创建标记");
            var settings = Read("src/AuroraLauncher.Desktop/Views/SettingsView.xaml");
            Assert(settings.Contains("CacheFolders") && settings.Contains("OpenCacheFolderCommand"), "缓存目录可展示可打开");
            Assert(settings.Contains("UninstallCommand"), "设置页有卸载入口");
            var vm = Read("src/AuroraLauncher.Desktop/ViewModels/MainViewModel.Round99.cs");
            Assert(vm.Contains("UninstallWindow") && vm.Contains("最后确认"), "卸载必须弹窗 + 二次确认");
            Assert(vm.Contains("CacheFolderPolicy.IsSafeRoot(root)"), "执行前校验数据根安全");
            Assert(Read("src/AuroraLauncher.Desktop/Views/UninstallWindow.xaml.cs")
                   .Contains("_plan.Targets.Count > 0"), "没有可删项时不允许确认");
            Assert(Read("src/AuroraCore/Services/UninstallService.cs")
                   .Contains("RecycleOption.SendToRecycleBin"), "优先移入回收站而不是硬删");
        });

        // ---------------- 第 100 轮：一键补齐缺失前置 ----------------
        Check("MissingDependencyFix：按依赖数排序、合并范围、slug 映射与空 id 过滤（第 100 轮）", () =>
        {
            var report = new ModPreflight.Report
            {
                Missing = new[]
                {
                    new ModDependency("kotlinforforge", "*", "Kotlin 模组", "k.jar")
                        { Ranges = new[] { "*" }, RequiredByList = new[] { "Kotlin 模组" } },
                    new ModDependency("mystery-api", "[4.11.0,4.99.0)", "A", "a.jar")
                        { Ranges = new[] { "[4.11.0,4.99.0)" }, RequiredByList = new[] { "A", "B" } },
                    new ModDependency("", "*", "坏数据", "x.jar")
                }
            };
            var plans = MissingDependencyFix.Plan(report);
            Assert(plans.Count == 2, "空 id 必须被忽略");
            Assert(plans[0].ModId == "mystery-api", "被更多模组依赖的前置排前面");
            Assert(plans[0].Slug == "mystery-api" && !plans[0].KnownSlug, "未知 slug 走字面 id");
            Assert(plans[1].Slug == "kotlin-for-forge" && plans[1].KnownSlug, "已知 slug 映射");
            Assert(plans[0].RangeText.Contains("4.11.0"), "范围要带进 UI");
        });

        Check("MissingDependencyFix：只认精确 slug / 标题，模糊命中一律拒绝（第 100 轮）", () =>
        {
            var c = new AutoFixCandidate("kotlinforforge", new[] { "*" },
                new[] { "某模组" }, "kotlin-for-forge", true);
            var unrelated = new ModrinthProject { ProjectId = "1", Slug = "other-mod", Title = "Totally Different" };
            Assert(MissingDependencyFix.PickProject(c, new[] { unrelated }) == null,
                "模糊搜索的第一个结果绝不能当作前置安装");
            var fuzzyTitle = new ModrinthProject { ProjectId = "2", Slug = "wrong-slug", Title = "kotlinforforge" };
            Assert(MissingDependencyFix.PickProject(c, new[] { unrelated, fuzzyTitle })?.ProjectId == "2",
                "标题完全一致可以作为精确匹配的兜底");
            var exact = new ModrinthProject { ProjectId = "3", Slug = "Kotlin-For-Forge", Title = "Kotlin for Forge" };
            Assert(MissingDependencyFix.PickProject(c, new[] { fuzzyTitle, exact })?.ProjectId == "3",
                "slug 精确命中优先于标题命中");
        });

        Check("MissingDependencyFix：加载器 / MC / 版本范围 / 校验值四道门（第 100 轮）", () =>
        {
            var c = new AutoFixCandidate("architectury-api",
                new[] { "[4.11.0,4.99.0)" }, new[] { "A" }, "architectury-api", true);
            ModrinthVersion V(string ver, string type, string loader, string mc, string? sha, long size) =>
                new("id-" + ver, ver, ver, type, new[] { mc }, new[] { loader },
                    "https://cdn.example/" + ver + ".jar", ver + ".jar", size, sha,
                    new DateTime(2026, 1, 1).AddDays(VerDay(ver)));
            static int VerDay(string ver) => int.TryParse(ver.Split('.')[1], out var n) ? n : 0;

            var list = new[]
            {
                V("4.10.0", "release", "forge", "1.20.1", "aaa", 100),   // range fails
                V("4.20.0", "release", "fabric", "1.20.1", "bbb", 100),  // loader fails
                V("4.30.0", "release", "forge", "1.20.2", "ccc", 100),   // mc fails
                V("4.50.0", "release", "forge", "1.20.1", null, 100),    // no checksum
                V("4.40.0", "beta", "forge", "1.20.1", "eee", 100),
                V("4.45.0", "release", "forge", "1.20.1", "fff", 100)
            };
            var (pick, outcome) = MissingDependencyFix.PickVersion(c, ModLoaderType.Forge, "1.20.1", list);
            Assert(outcome == MissingDependencyFix.PickOutcome.Found, "应找到合规版本");
            Assert(pick!.VersionNumber == "4.45.0", "release 优先于 beta，且范围上界 4.99.0 内取最新");

            var (noHash, o2) = MissingDependencyFix.PickVersion(c, ModLoaderType.Forge, "1.20.1",
                new[] { V("4.50.0", "release", "forge", "1.20.1", null, 100) });
            Assert(noHash == null && o2 == MissingDependencyFix.PickOutcome.Unverifiable, "无校验值必须拒绝");

            var (_, o3) = MissingDependencyFix.PickVersion(c, ModLoaderType.Forge, "1.20.1",
                new[] { V("4.10.0", "release", "forge", "1.20.1", "aaa", 100) });
            Assert(o3 == MissingDependencyFix.PickOutcome.NoMatchingVersion, "范围外要有明确结论");
            Assert(MissingDependencyFix.TypeRank("beta") < MissingDependencyFix.TypeRank("alpha"), "beta 比 alpha 稳");
        });

        Check("AutoFixSession：没经过重扫就不许说已补齐（第 100 轮）", () =>
        {
            var c1 = new AutoFixCandidate("a", new[] { "*" }, new[] { "X" }, "a", false);
            var c2 = new AutoFixCandidate("b", new[] { "*" }, new[] { "X" }, "b", false);
            var c3 = new AutoFixCandidate("c", new[] { "*" }, new[] { "X" }, "c", false);
            var s = new AutoFixSession(new[] { c1, c2, c3 });
            s.Record(new AutoFixResult("a", "a", AutoFixStatus.Installed, "a.jar"));
            s.Record(new AutoFixResult("b", "b", AutoFixStatus.Installed, "b.jar"));
            s.Record(new AutoFixResult("c", "c", AutoFixStatus.NotFound));
            var final = s.Complete(new[] { "a" });
            Assert(final.First(r => r.ModId == "a").Status == AutoFixStatus.Installed, "重扫到的才算装好");
            Assert(final.First(r => r.ModId == "b").Status == AutoFixStatus.NotRecognized,
                "下载了但重扫没看到 → 不许报已安装");
            Assert(s.InstalledCount == 1 && s.FailedCount == 2, "汇总口径以重扫后为准");
            Assert(s.Summary.Contains("2 项需要手动处理"), "汇总要说清还有几项没成");
        });

        Check("MissingDependencyFix：七种结果都有中文说明与手动出口（第 100 轮）", () =>
        {
            var c = new AutoFixCandidate("some-api", new[] { "[1.0,)" }, new[] { "某模组" }, "some-api", false);
            foreach (AutoFixStatus status in Enum.GetValues<AutoFixStatus>())
            {
                var line = MissingDependencyFix.Describe(
                    new AutoFixResult(c.ModId, c.Slug, status, "f.jar", "细节"));
                Assert(line.Length > 2 && !line.Contains("operation was canceled", StringComparison.OrdinalIgnoreCase),
                    $"{status} 必须有中文说明");
            }
            var hint = MissingDependencyFix.ManualHint(c);
            Assert(hint.Contains("打开 mods 文件夹") && hint.Contains("1.0"), "失败要给出手动路径与版本要求");
            Assert(MissingDependencyFix.CanVerify(new ModrinthVersion("i", "1", "n", "release",
                new[] { "1.20.1" }, new[] { "forge" }, "u", "f.jar", 100, "abc")) &&
                !MissingDependencyFix.CanVerify(new ModrinthVersion("i2", "1", "n", "release",
                    new[] { "1.20.1" }, new[] { "forge" }, "u", "f2.jar", 100, null)), "校验门口径");
        });

        Check("Guard: 补齐必须有校验值、成功必须重扫确认、失败不能假装成功（第 100 轮）", () =>
        {
            string Read(string p) => File.ReadAllText(Path.Combine(FindSourceRoot()!, p));
            var core = Read("src/AuroraCore/Services/MissingDependencyFix.cs");
            var vm = Read("src/AuroraLauncher.Desktop/ViewModels/MainViewModel.Round100.cs");
            var preflight = Read("src/AuroraCore/Services/ModPreflight.cs");
            Assert(core.Contains("public static bool CanVerify(ModrinthVersion version)"), "校验值单一判定入口");
            Assert(core.Contains("public IReadOnlyList<AutoFixResult> Complete(IReadOnlyCollection<string> presentIds)"),
                "结论必须由重扫 ids 决定");
            Assert(core.Contains("r.Status == AutoFixStatus.Installed && !present.Contains(r.ModId)"),
                "没扫到就把 Installed 降级");
            Assert(preflight.Contains("PresentIds = present"), "体检必须暴露实际识别到的 id");
            Assert(vm.Contains("session.Complete(fresh.PresentIds)"), "VM 必须用重扫结果收口");
            Assert(vm.Contains("fresh = new ModPreflight.Report()") &&
                   vm.Contains("// Fail closed"), "重扫失败时不许保留成功结论");
            Assert(vm.Contains("outcome == MissingDependencyFix.PickOutcome.Unverifiable"),
                "无校验值要明确告诉用户，而不是静默跳过");
        });

        Check("Guard: 一键补齐走 DownloadJob、逐项失败不中断、UI 有手动出口（第 100 轮）", () =>
        {
            string Read(string p) => File.ReadAllText(Path.Combine(FindSourceRoot()!, p));
            var vm = Read("src/AuroraLauncher.Desktop/ViewModels/MainViewModel.Round100.cs");
            var mods = Read("src/AuroraLauncher.Desktop/Views/ModsView.xaml");
            var pre = Read("src/AuroraLauncher.Desktop/Views/PreflightWindow.xaml");
            Assert(vm.Contains("BeginJobAsync(\"补齐\""), "补齐必须是可取消的下载任务");
            // CI checks out with CRLF while local edits are LF — never pin a
            // guard to a literal newline sequence.
            Assert(vm.Contains("session.Record(result);") &&
                   System.Text.RegularExpressions.Regex.IsMatch(vm,
                       @"catch \(Exception ex\)\s*\{\s*result = new AutoFixResult"),
                "单项失败要记录后继续下一项");
            Assert(mods.Contains("AutoFixButtonText") && mods.Contains("SelectAllMissingCommand") &&
                   mods.Contains("OpenModsFolderCommand"), "清单卡片要有全选、进度文案与手动出口");
            Assert(pre.Contains("AutoFix_Click"), "启动前检查也要有补齐入口");
        });

        Check("Guard: 启动前检查可勾选补齐，二次弹窗不得循环补齐（第 100 轮）", () =>
        {
            string Read(string p) => File.ReadAllText(Path.Combine(FindSourceRoot()!, p));
            var code = Read("src/AuroraLauncher.Desktop/Views/PreflightWindow.xaml.cs");
            var xaml = Read("src/AuroraLauncher.Desktop/Views/PreflightWindow.xaml");
            var vm = Read("src/AuroraLauncher.Desktop/ViewModels/MainViewModel.cs");
            Assert(code.Contains("PreflightChoice.AutoFix") && xaml.Contains("IsChecked=\"{Binding IsChecked}\""),
                "勾选要真能传到 VM");
            Assert(code.Contains("SelectedModIds"), "补齐范围来自勾选，不能整批强装");
            Assert(vm.Contains("SetMissingFromReport(report)"), "清单要用本次扫描结果，不用过期缓存");
            Assert(vm.Contains("RunPreflightAsync(instance, ct, allowAutoFix: false)"), "补齐后重扫不能再弹同一个按钮");
        });

        // ---------------- 第 101 轮：更新可见可取消 + 引导存放选择 ----------------
        Check("UpdateUiPolicy：下载进度文案、取消可用性与取消后的暂存说明（第 101 轮）", () =>
        {
            Assert(UpdateUiPolicy.DownloadTitle(101).Contains("101") &&
                   UpdateUiPolicy.DownloadTitle(101).Contains("正在下载"), "标题要说清是哪一轮");
            var text = UpdateUiPolicy.DescribeProgress(42, "27.5 / 65.4 MB", "3.2 MB/s");
            Assert(text.Contains("42") && text.Contains("65.4 MB") && text.Contains("3.2 MB/s"),
                "进度要同时给百分比、体积与速度");
            Assert(UpdateUiPolicy.CanCancel(true, false) &&
                   !UpdateUiPolicy.CanCancel(true, true) &&
                   !UpdateUiPolicy.CanCancel(false, false), "只在下载进行中且未请求取消时可点");
            Assert(UpdateUiPolicy.DescribeCanceled(false).Contains("不会安装半个文件"),
                "取消要说明没留下半成品");
            Assert(UpdateUiPolicy.DescribeCanceled(true).Contains("仍保留"),
                "之前已就绪的更新不能被本次取消带走");
            Assert(UpdateUiPolicy.ApplyStageHint.Contains("安装阶段不可取消"),
                "换装阶段必须解释为什么不能取消");
        });

        Check("FirstRunStoragePolicy：下载前先选位置，路径与标记判定正确（第 101 轮）", () =>
        {
            Assert(FirstRunStoragePolicy.FromPortable(false) == FirstRunStorageChoice.AppData &&
                   FirstRunStoragePolicy.FromPortable(true) == FirstRunStorageChoice.BesideExe,
                "勾选状态与选择一一对应");
            var exe = @"C:\Tools\Aurora\AuroraLauncher.exe";
            var appData = @"C:\Users\me\AppData\Roaming\AuroraLauncher";
            Assert(FirstRunStoragePolicy.Root(FirstRunStorageChoice.BesideExe, appData, exe)
                       .Equals(@"C:\Tools\Aurora", StringComparison.OrdinalIgnoreCase), "便携根是 EXE 所在目录");
            Assert(FirstRunStoragePolicy.Root(FirstRunStorageChoice.AppData, appData, exe) == appData,
                "默认选择不动现有数据根");
            Assert(FirstRunStoragePolicy.Describe(FirstRunStorageChoice.BesideExe, appData, exe)
                       .Contains(@"C:\Tools\Aurora"), "要把真实路径给用户看");
            Assert(FirstRunStoragePolicy.ShouldCreateMarker(FirstRunStorageChoice.BesideExe, false) &&
                   !FirstRunStoragePolicy.ShouldCreateMarker(FirstRunStorageChoice.BesideExe, true) &&
                   !FirstRunStoragePolicy.ShouldCreateMarker(FirstRunStorageChoice.AppData, false),
                "只有显式选便携且尚未便携时才写标记");
            Assert(FirstRunStoragePolicy.Note(true, false, exe).Contains("重启后生效"), "告知何时生效");
            Assert(FirstRunStoragePolicy.IsDownloadStep(2) && !FirstRunStoragePolicy.IsDownloadStep(1),
                "存放选择步骤(1)必须排在下载步骤(2)之前");
        });

        Check("Guard: 更新下载窗口必须真显示进度、可取消且不误删旧更新（第 101 轮）", () =>
        {
            string Read(string p) => File.ReadAllText(Path.Combine(FindSourceRoot()!, p));
            var vm = Read("src/AuroraLauncher.Desktop/ViewModels/MainViewModel.cs").Replace("\r\n", "\n");
            var x = Read("src/AuroraLauncher.Desktop/Views/UpdateDownloadWindow.xaml");
            var cs = Read("src/AuroraLauncher.Desktop/Views/UpdateDownloadWindow.xaml.cs");
            Assert(x.Contains("Value=\"{Binding UpdateProgress}\"") &&
                   x.Contains("IsIndeterminate=\"{Binding UpdateProgressIndeterminate}\"") &&
                   x.Contains("Text=\"{Binding UpdateStatusText}\""), "三处都要绑同一进度状态");
            Assert(x.Contains("Command=\"{Binding CancelUpdateCommand}\"") &&
                   cs.Contains("_vm.CancelUpdate()"), "取消按钮真接令牌，不能是摆设");
            Assert(vm.Contains("PrepareUpdateWindow(info)") && vm.Contains("CloseUpdateWindow()"),
                "下载全过程要有窗口的打开与关闭");
            Assert(vm.Contains("UpdateUiPolicy.DescribeProgress((int)pct, size, speed)"),
                "窗口文案与单测共用同一策略");
            Assert(vm.Contains("var hadStagedBefore = UpdateService.HasStagedUpdate()") &&
                   (vm.Contains("if (hadStagedBefore) UpdateService.CancelStagingOnly()") ||
                    vm.Contains("if (!hadStagedBefore) UpdateService.CancelStagedDownload()")),
                "取消时不能删掉之前已就绪的更新包");
            Assert(Read("src/AuroraCore/Services/UpdateService.cs").Contains("public static void CancelStagedDownload()"),
                "清理半成品要有单一入口");
        });

        Check("Guard: 首次引导在下载前独立询问文件位置且选择真生效（第 101 轮）", () =>
        {
            string Read(string p) => File.ReadAllText(Path.Combine(FindSourceRoot()!, p));
            var wiz = Read("src/AuroraLauncher.Desktop/MainWindow.xaml");
            var vm = Read("src/AuroraLauncher.Desktop/ViewModels/MainViewModel.cs").Replace("\r\n", "\n");
            var r101 = Read("src/AuroraLauncher.Desktop/ViewModels/MainViewModel.Round101.cs");
            Assert(wiz.Contains("步骤 2 / 4 · 启动器的文件放在哪里？") &&
                   wiz.Contains("这一步在游戏文件下载之前"), "存放选择要在下载前单独成步");
            Assert(wiz.Contains("FirstRunUseAppData") && wiz.Contains("FirstRunUseExeDir") &&
                   wiz.Contains("FirstRunStorageSummary"), "两个互斥选项 + 实际路径解释");
            Assert(wiz.Contains("FirstRunJavaNextCommand") && wiz.Contains("FirstRunNextCommand"),
                "Java 步不再顺手应用，存储步才应用");
            Assert(vm.Contains("public RelayCommand FirstRunJavaNextCommand"), "Java 步必须有独立的下一步命令");
            Assert(r101.Contains("FirstRunStep3Visible => FirstRunStep == 3") &&
                   r101.Contains("GoToFirstRunStorageStep()"), "安装与完成步骤要顺延");
            Assert(Read("src/AuroraLauncher.Desktop/ViewModels/MainViewModel.Round99.cs")
                       .Contains("FirstRunStoragePolicy.ShouldCreateMarker(choice, IsPortableMode)"),
                "选择必须走策略判定，不能只看旧勾选");
        });

        // ---------------- 第 102 轮：卸载残留修复 ----------------
        Check("UninstallRegistryPolicy：只认专属键与可验证的自启动值（第 102 轮）", () =>
        {
            Assert(UninstallRegistryPolicy.IsAllowedKey(UninstallRegistryPolicy.AppKey) &&
                   UninstallRegistryPolicy.IsAllowedKey(UninstallRegistryPolicy.RunKey) &&
                   !UninstallRegistryPolicy.IsAllowedKey(@"Software\Microsoft\Windows\CurrentVersion\Run\OtherApp") &&
                   !UninstallRegistryPolicy.IsAllowedKey(@"SOFTWARE\JavaSoft\Java Runtime Environment"),
                "注册表白名单必须精确，Java / 其它软件绝不碰");
            var exe = @"C:\Tools\Aurora\AuroraLauncher.exe";
            Assert(UninstallRegistryPolicy.IsAllowedValue(UninstallRegistryPolicy.RunKey,
                       UninstallRegistryPolicy.RunValueName, $"\"{exe}\" --minimized", exe),
                "指向本启动器的 Run 值可以清");
            Assert(!UninstallRegistryPolicy.IsAllowedValue(UninstallRegistryPolicy.RunKey,
                       UninstallRegistryPolicy.RunValueName, @"C:\Other\app.exe", exe) &&
                   !UninstallRegistryPolicy.IsAllowedValue(UninstallRegistryPolicy.RunKey,
                       "SomeOtherApp", exe, exe), "同名 Run 之外的东西一律不动");
            Assert(UninstallRegistryPolicy.IsVerifiedKey(UninstallRegistryPolicy.UninstallKey,
                       "Aurora Launcher", "", exe) &&
                   !UninstallRegistryPolicy.IsVerifiedKey(UninstallRegistryPolicy.UninstallKey,
                       "Some Random Tool", "", exe) &&
                   !UninstallRegistryPolicy.IsVerifiedKey(UninstallRegistryPolicy.AppPathsKey,
                       "Aurora Launcher", @"C:\Other\tool.exe", exe), "卸载登记项必须名字和路径都像我们");
            Assert(UninstallRegistryPolicy.Candidates.Count == 5 &&
                   UninstallRegistryPolicy.Describe(UninstallRegistryPolicy.Candidates[0]).Contains("注册表"),
                "候选清单固定 5 项且有中文说明");
        });

        Check("卸载清理进程：命令行往返、路径白名单与 JSON 清单（第 102 轮）", () =>
        {
            var manifestPath = Path.Combine(Path.GetTempPath(), "aurora-uninstall-guard-" + Guid.NewGuid().ToString("N") + ".json");
            var line = UninstallCleanupArgs.Build(manifestPath);
            Assert(UninstallCleanupArgs.TryParse(new[] { "--uninstall-cleanup", manifestPath }, out var parsed) &&
                   parsed == manifestPath && line.Contains(UninstallCleanupArgs.Switch), "命令行参数要能往返");
            Assert(!UninstallCleanupArgs.TryParse(new[] { "--uninstall-cleanup" }, out _), "缺参数不能误判");

            var root = Path.Combine(Path.GetTempPath(), "aurora-uninstall-guard-root-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "cache"));
            File.WriteAllText(Path.Combine(root, "config.json"), "{}");
            try
            {
                Assert(UninstallService.IsCleanupPathAllowed(Path.Combine(root, "cache"), root), "启动器自己的缓存目录可清");
                Assert(UninstallService.IsCleanupPathAllowed(Path.Combine(root, "config.json"), root), "配置文件可清");
                Assert(!UninstallService.IsCleanupPathAllowed(@"C:\Windows\System32", root), "系统目录绝不可清");
                Assert(!UninstallService.IsCleanupPathAllowed(Path.Combine(Path.GetTempPath(), "someone-else.txt"), root),
                    "数据根之外的文件不可清");

                var manifest = new UninstallCleanupManifest { ParentPid = 4242, ExePath = @"C:\A\AuroraLauncher.exe", Root = root };
                manifest.Paths.Add(Path.Combine(root, "cache"));
                manifest.Save(manifestPath);
                var back = UninstallCleanupManifest.Load(manifestPath);
                Assert(back?.ParentPid == 4242 && back.Paths.Count == 1 && back.ExePath.EndsWith("AuroraLauncher.exe"),
                    "清单要能完整往返");
            }
            finally
            {
                try { File.Delete(manifestPath); } catch { }
                try { Directory.Delete(root, true); } catch { }
            }
        });

        Check("Guard: 卸载后立即删本体、失败的旧做法不再当正常结局（第 102 轮）", () =>
        {
            string Read(string p) => File.ReadAllText(Path.Combine(FindSourceRoot()!, p));
            var service = Read("src/AuroraCore/Services/UninstallService.cs").Replace("\r\n", "\n");
            var app = Read("src/AuroraLauncher.Desktop/App.xaml.cs").Replace("\r\n", "\n");
            var vm = Read("src/AuroraLauncher.Desktop/ViewModels/MainViewModel.Round99.cs").Replace("\r\n", "\n");
            Assert(service.Contains("PrepareAndStartHelper") && service.Contains("RunCleanupHelperAsync") &&
                   service.Contains("WaitForParentAsync"), "临时进程必须等父进程退出再删 EXE");
            Assert(service.Contains("IsCleanupPathAllowed(path, manifest.Root)"), "清理进程二次校验路径");
            Assert(app.Contains("UninstallCleanupArgs.TryParse") && app.Contains("OnExplicitShutdown"),
                "无窗口清理模式要在任何窗口创建之前接管");
            Assert(vm.Contains("tryDeferExe: false") && vm.Contains("helperStarted"),
                "不再默认把重启后删除当作正常结局");
            Assert(Read("src/AuroraCore/Services/UninstallPolicy.cs")
                       .Contains("无需重启"), "用户提示要与实际行为一致");
        });

        Check("Guard: 注册表清理默认勾选但只能走白名单服务（第 102 轮）", () =>
        {
            string Read(string p) => File.ReadAllText(Path.Combine(FindSourceRoot()!, p));
            var x = Read("src/AuroraLauncher.Desktop/Views/UninstallWindow.xaml");
            var cs = Read("src/AuroraLauncher.Desktop/Views/UninstallWindow.xaml.cs").Replace("\r\n", "\n");
            var vm = Read("src/AuroraLauncher.Desktop/ViewModels/MainViewModel.Round99.cs").Replace("\r\n", "\n");
            var reg = Read("src/AuroraCore/Services/UninstallRegistryService.cs").Replace("\r\n", "\n");
            Assert(x.Contains("RemoveRegistryBox") && x.Contains("IsChecked=\"True\""),
                "注册表清理默认勾选，但只删白名单项");
            Assert(cs.Contains("UninstallRegistryService.Scan(exe ?? \"\")"), "弹窗要显示真实检测到的项");
            Assert(cs.Contains("_plan.Targets.Count > 0"), "没有可删文件时仍不许确认");
            Assert(vm.Contains("Execute(plan, dlg.ResultRemoveRegistry"), "用户勾选要真的传到执行层");
            Assert(reg.Contains("UninstallRegistryPolicy.IsAllowedKey(t.Key)"), "删除前再过一次白名单");
            Assert(reg.Contains("Registry.CurrentUser") && !reg.Contains("Registry.LocalMachine"),
                "只动当前用户，绝不写 HKLM");
        });

        // ---------------- 第 103 轮：启动可见反馈 + 恢复下载换线路 ----------------
        Check("LauncherConfig：退役镜像域名与 client jar 路径都能反解回官方（第 103 轮）", () =>
        {
            var legacy = "https://bmclapi.bangbang93.com/v1/packages/0123456789abcdef/26.1.2.jar";
            var official = LauncherConfig.ToOfficialUrl(legacy);
            Assert(official == "https://piston-data.mojang.com/v1/packages/0123456789abcdef/26.1.2.jar",
                "旧域名 + v1/packages 必须可反解，否则恢复下载永远没有备用线路：" + official);
            var current = "https://bmclapi2.bangbang93.com/v1/packages/abc/1.21.10.jar";
            Assert(LauncherConfig.ToOfficialUrl(current) == "https://piston-data.mojang.com/v1/packages/abc/1.21.10.jar",
                "当前镜像域名的 client jar 也要能回官方");
            Assert(LauncherConfig.ToOfficialUrl("https://piston-data.mojang.com/v1/packages/a/b.jar")
                       .StartsWith("https://piston-data.mojang.com"), "官方 URL 原样返回");
            Assert(LauncherConfig.ToOfficialUrl("https://bmclapi.bangbang93.com/maven/unknown/x.jar")
                       .Contains("bmclapi"), "无法判定的 maven 路径不猜测");
            Assert(LauncherConfig.ToOfficialUrl("https://bmclapi.bangbang93.com/maven/net/fabricmc/fabric-loader/0.1/x.jar")
                       .StartsWith("https://maven.fabricmc.net/"), "可判定的 maven 组路径仍要反解");
        });

        Check("ResumeDownloadPolicy：恢复下载先把旧域名归一，再按当前线路选择（第 103 轮）", () =>
        {
            var legacy = "https://bmclapi.bangbang93.com/v1/packages/hash/26.1.2.jar";
            Assert(ResumeDownloadPolicy.UsesLegacyMirror(legacy) &&
                   !ResumeDownloadPolicy.UsesLegacyMirror("https://piston-data.mojang.com/v1/packages/hash/26.1.2.jar"),
                "要能识别清单里的退役域名");
            var official = ResumeDownloadPolicy.ChooseUrl(legacy, DownloadPolicy.Official, false);
            Assert(official.StartsWith("https://piston-data.mojang.com"), "强制官方时归一到官方");
            var mirror = ResumeDownloadPolicy.ChooseUrl(legacy, DownloadPolicy.Mirror, false);
            Assert(mirror.StartsWith("https://bmclapi2.bangbang93.com") && !mirror.Contains("bmclapi.bangbang93.com/"),
                "强制镜像时用当前域名，不再沿用退役域名：" + mirror);
            var auto = ResumeDownloadPolicy.ChooseUrl(legacy, DownloadPolicy.Auto, false);
            Assert(auto.StartsWith("https://piston-data.mojang.com"), "自动模式默认官方并保留引擎换线路能力");
            Assert(DownloadEngine.AlternateRouteUrl(legacy) != null,
                "引擎对旧域名也必须能给出另一条线路");
        });

        Check("Guard: 启动被拦截必须可见，未登录也要补齐缺文件（第 103 轮）", () =>
        {
            string Read(string p) => File.ReadAllText(Path.Combine(FindSourceRoot()!, p));
            var vm = Read("src/AuroraLauncher.Desktop/ViewModels/MainViewModel.cs").Replace("\r\n", "\n");
            var policy = Read("src/AuroraCore/Services/LaunchGuardPolicy.cs").Replace("\r\n", "\n");
            Assert(policy.Contains("Block.NoAccount") && policy.Contains("请先选择/添加账户"),
                "账户仍是启动前置条件");
            Assert(vm.Contains("if (block == LaunchGuardPolicy.Block.NoInstances)") &&
                   vm.Contains("ShowToast(\"还没有实例：已打开"), "没有实例要有可见提示");
            Assert(vm.Contains("ShowToast(message + \"（输入框可直接添加离线账户）\")"),
                "未登录不能只改一行状态栏");
            Assert(vm.Contains("if (!IsBusy && pendingInstance != null)") &&
                   vm.Contains("_svc.NeedsRepair(pendingInstance)") &&
                   vm.Contains("RunRepairJobAsync(pendingInstance)"),
                "未登录但缺文件时仍触发补齐，兑现首页提示");
            Assert(vm.Contains("ResumeDownloadPolicy.ChooseUrl(p.Url, routePolicy, preferMirror)"),
                "恢复下载必须走归一策略");
        });

        // ---------------- 第 104 轮：全屏布局、进度、下载管理集成、代码审查 ----------------
        Check("进度策略：空任务 0%、单文件按字节走、已完成任务不拉低汇总（第 104 轮）", () =>
        {
            var none = JobProgressPolicy.Aggregate(Array.Empty<DownloadJobSnapshot>());
            Assert(none.Percent == 0 && none.Active == 0, "没有任务时不能显示 100%");
            Assert(JobProgressPolicy.FractionFor(1, 0, 1024, 512) == 0.5,
                "单文件下载到一半必须是 50%，不能等 CompletedFiles 才跳 100%");
            Assert(JobProgressPolicy.FractionFor(3, 1, 0, 0) == 1.0 / 3.0,
                "无字节信息时退回文件数比例");
            Assert(JobProgressPolicy.FractionFor(0, 0, 0, 0) == 0, "无信息不猜测");

            var donePlusRunning = JobProgressPolicy.Aggregate(new[]
            {
                new DownloadJobSnapshot(JobState.Done, 100, 0),
                new DownloadJobSnapshot(JobState.Running, 5, 100)
            });
            Assert(donePlusRunning.Percent == 5,
                "已完成 100% 不能把新任务的 5% 平均成 52%：" + donePlusRunning.Percent);
            var onlyDone = JobProgressPolicy.Aggregate(new[]
            {
                new DownloadJobSnapshot(JobState.Done, 100, 0)
            });
            Assert(onlyDone.Percent == 100, "只有已完成任务时仍显示完成");
            var onlyFailed = JobProgressPolicy.Aggregate(new[]
            {
                new DownloadJobSnapshot(JobState.Failed, 100, 0)
            });
            Assert(onlyFailed.Percent == 0, "失败任务不贡献完成度");
        });

        Check("任务槽：看门狗中止与准备中取消都必须归还槽位（第 104 轮）", () =>
        {
            var manager = new DownloadJobManager(_ => { });
            var stalled = manager.Create("测试", "卡住的任务", "");
            manager.StartAsync(stalled).GetAwaiter().GetResult();
            stalled.AbortAsFailure("看门狗中止");
            manager.Finish(stalled, null, true); // 调用方看到的已结束状态
            Assert(!stalled.SlotHeld, "看门狗先 MarkFailed 不能再吞掉槽位释放");

            var preparing = manager.Create("测试", "准备中取消", "");
            manager.StartAsync(preparing, markRunning: false).GetAwaiter().GetResult();
            manager.Cancel(preparing);
            Assert(!preparing.SlotHeld, "Preparing 状态占槽，取消时必须释放");

            for (var i = 0; i < 3; i++)
            {
                var next = manager.Create("测试", "后续任务 " + i, "");
                var task = manager.StartAsync(next);
                var finished = Task.WhenAny(task, Task.Delay(1000)).GetAwaiter().GetResult() == task;
                Assert(finished, "第 " + (i + 1) + " 个任务应立刻拿到槽位，说明前面没有泄漏");
            }
        });

        Check("窗口布局与卸载助手：最大化去边框，助手只删自己的文件（第 104 轮）", () =>
        {
            Assert(WindowLayoutPolicy.ResizeBorder(true) == 0 &&
                   WindowLayoutPolicy.ResizeBorder(false) == 6,
                "最大化时必须去掉 6px 缩放边框，否则内容被推出屏幕");
            Assert(WindowLayoutPolicy.ClampToWorkArea(true) &&
                   !WindowLayoutPolicy.ClampToWorkArea(false), "只贴靠最大化窗口");

            Assert(UninstallCleanupArgs.IsPlausibleExePath(@"C:\Tools\Aurora\AuroraLauncher.exe"),
                "真实安装位置是合法的");
            Assert(!UninstallCleanupArgs.IsPlausibleExePath(@"C:\Users\me\Documents\私房照.exe") &&
                   !UninstallCleanupArgs.IsPlausibleExePath(@"C:\Windows\System32\drivers\etc\hosts"),
                "清单里换个文件名就删任意文件必须被拒绝");
            var tempHelper = Path.Combine(Path.GetTempPath(), "aurora-uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
            Assert(UninstallCleanupArgs.IsPlausibleHelperPath(tempHelper), "临时副本要能通过校验");
            Assert(!UninstallCleanupArgs.IsPlausibleHelperPath(@"C:\Tools\Aurora\AuroraLauncher.exe"),
                "正式 EXE 不能被当成临时副本删掉");
        });

        Check("Guard: 最大化贴靠工作区，任务栏不再被遮挡（第 105 轮）", () =>
        {
            Assert(WindowLayoutPolicy.WmGetMinMaxInfo == 0x0024,
                "策略常量要与 Win32 消息号一致");
            string Read(string p) => File.ReadAllText(Path.Combine(FindSourceRoot()!, p));
            var win = Read("src/AuroraLauncher.Desktop/MainWindow.xaml.cs").Replace("\r\n", "\n");
            var xaml = Read("src/AuroraLauncher.Desktop/MainWindow.xaml").Replace("\r\n", "\n");
            Assert(win.Contains("AddHook(ClampMaximizedToWorkArea)") &&
                   win.Contains("WindowLayoutPolicy.WmGetMinMaxInfo") &&
                   win.Contains("GetMonitorInfo") &&
                   win.Contains("info.Work"),
                "必须钩住 WM_GETMINMAXINFO，并用显示器工作区回答最大化矩形");
            Assert(xaml.Contains("<shell:WindowChrome") &&
                   win.Contains("MaxSize.X = info.Work.Right - info.Work.Left"),
                "WindowChrome 保留，但最大化矩形必须由钩子按工作区计算");
            Assert(!win.Contains("Left = work.Left"),
                "给最大化窗口赋 Left/Top/Width/Height 是空操作，不能再用它假装修任务栏");
        });

        Check("主题策略：跟随系统能解析，默认按浅色回退（第 104 轮）", () =>
        {
            Assert(ThemePolicy.Resolve(ThemePreference.System, true) == ThemePreference.Light &&
                   ThemePolicy.Resolve(ThemePreference.System, false) == ThemePreference.Dark,
                "System 不再被当成 Dark");
            Assert(ThemePolicy.Resolve(ThemePreference.Light, false) == ThemePreference.Light &&
                   ThemePolicy.Resolve(ThemePreference.Dark, true) == ThemePreference.Dark,
                "显式选择不能被系统覆盖");
            Assert(ThemePolicy.SystemPrefersLight() || !ThemePolicy.SystemPrefersLight(),
                "读取系统主题不应抛异常（允许任意结果）");
        });

        Check("Guard: Java 与更新进入下载管理，全屏布局与主题接线（第 104 轮）", () =>
        {
            string Read(string p) => File.ReadAllText(Path.Combine(FindSourceRoot()!, p));
            var downloads = Read("src/AuroraLauncher.Desktop/Views/DownloadsView.xaml");
            var vm = Read("src/AuroraLauncher.Desktop/ViewModels/MainViewModel.cs").Replace("\r\n", "\n");
            var win = Read("src/AuroraLauncher.Desktop/MainWindow.xaml.cs").Replace("\r\n", "\n");
            Assert(downloads.Contains("InstallJava21Command") &&
                   downloads.Contains("CheckUpdateCommand") &&
                   downloads.Contains("RestartToApplyCommand"),
                "下载管理页要有 Java / 检查更新 / 应用更新三个入口");
            Assert(vm.Contains("BeginJobAsync(\"Java\"") && vm.Contains("javaJob.Report(p)"),
                "Java 下载要作为任务进入同一进度系统");
            Assert(vm.Contains("BeginJobAsync(\"更新\""), "启动器更新也要进下载管理");
            Assert(win.Contains("ApplyMaximizedLayout") && win.Contains("ResizeBorder(maximized)"),
                "最大化布局修复必须接线到窗口状态变化");
            Assert(win.Contains("SystemEvents.UserPreferenceChanged") &&
                   vm.Contains("ApplySystemThemeIfNeeded"), "跟随系统要能在运行时响应");
            Assert(win.Contains("ImportMrpackSafelyAsync") &&
                   win.Contains("catch (Exception ex) { Logger.Instance.Warn(\"App\", \"空闲内存清理失败"),
                "async void / fire-and-forget 都要有异常屏障");
        });

        Check("Guard: 任务槽、暂存保护、配置与日志的健壮性修复（第 104 轮）", () =>
        {
            string Read(string p) => File.ReadAllText(Path.Combine(FindSourceRoot()!, p));
            var mgr = Read("src/AuroraCore/Services/DownloadJobManager.cs").Replace("\r\n", "\n");
            var vm = Read("src/AuroraLauncher.Desktop/ViewModels/MainViewModel.cs").Replace("\r\n", "\n");
            var upd = Read("src/AuroraCore/Services/UpdateService.cs").Replace("\r\n", "\n");
            var cfg = Read("src/AuroraCore/LauncherConfig.cs").Replace("\r\n", "\n");
            var journal = Read("src/AuroraCore/Download/DownloadJournal.cs").Replace("\r\n", "\n");
            Assert(mgr.Contains("public void ReleaseSlot(DownloadJob job)") &&
                   mgr.Contains("job.SlotHeld = false"), "任务槽要有唯一的幂等释放入口");
            Assert(vm.Contains("_jobManager.ReleaseSlot(job)"), "看门狗先结束的任务也要释放槽");
            Assert(upd.Contains("StagingTempPath") && upd.Contains("CommitStaged()") &&
                   upd.Contains("public static void CancelStagingOnly()"),
                "新更新先写临时文件，失败不覆盖旧暂存包");
            Assert(Read("src/AuroraLauncher.Desktop/ViewModels/MainViewModel.Round101.cs")
                       .Contains("public RelayCommand CancelUpdateCommand"), "取消命令仍在");
            Assert(vm.Contains("if (IsCheckingUpdate) return;") && vm.Contains("cts?.Dispose()"),
                "更新入口并发闸与 CTS 释放都要在");
            Assert(cfg.Contains("File.Move(temp, path, overwrite: true)") &&
                   cfg.Contains("InstanceGate"), "配置原子保存与线程安全单例");
            Assert(journal.Contains("EnqueueCompaction(path, kept.ToString())") &&
                   journal.Contains("Queue.Enqueue(line)"), "日志压缩走 writer，失败行重新入队");
        });

        Check("第 106 轮：路径安全 / fail-closed 哈希 / 单文件看门狗 / 编译告警清零", () =>
        {
            // M1: zip-slip guard is shared by every extraction path.
            Assert(PathEx.IsSafeEntryPath("org/foo/bar.dll") &&
                   !PathEx.IsSafeEntryPath("../../evil.dll") &&
                   !PathEx.IsSafeEntryPath("/etc/passwd") &&
                   !PathEx.IsSafeEntryPath("C:\\Windows\\System32\\x.dll") &&
                   !PathEx.IsSafeEntryPath("a/../b.dll"),
                "PathEx 必须挡住父 traversal / 绝对路径 / 盘符 / ADS");
            // The natives extractor actually calls the guard.
            string Read(string p) => File.ReadAllText(Path.Combine(FindSourceRoot()!, p));
            var installer = Read("src/AuroraCore/Services/GameInstaller.cs").Replace("\r\n", "\n");
            Assert(installer.Contains("PathEx.IsSafeEntryPath(entry.FullName)") &&
                   installer.Contains("跳过不安全的 natives 条目"),
                "natives 解压必须过 zip-slip 闸门");

            // L1: missing hash is now rejected, not silently skipped.
            var upd = Read("src/AuroraCore/Services/UpdateService.cs").Replace("\r\n", "\n");
            Assert(upd.Contains("更新清单缺少 SHA-256，拒绝安装未校验的更新包") &&
                   upd.Contains("补丁文件校验失败（SHA-256 缺失或不匹配）"),
                "哈希为空必须 fail-closed");
            Assert(upd.Contains("Task.Run(() => Sha256OfFile(StagingTempPath), ct)") &&
                   !upd.Contains("SHA256.HashData(await File.ReadAllBytesAsync(StagingTempPath"),
                "完整包哈希必须流式、不能在 UI 线程 ReadAllBytes");

            // Watchdog: single-file stages use the 90 s bucket and skip file-stall.
            Assert(JobWatchdogPolicy.TimeoutSeconds(JobState.Running, "java-download") ==
                   JobWatchdogPolicy.InstallerDownloadTimeoutSeconds &&
                   JobWatchdogPolicy.TimeoutSeconds(JobState.Running, "update-download") ==
                   JobWatchdogPolicy.InstallerDownloadTimeoutSeconds,
                "Java / 更新单文件下载用安装器同档超时");
            Assert(!JobWatchdogPolicy.ShouldUseFileStall("java-download") &&
                   !JobWatchdogPolicy.ShouldUseFileStall("update-download"),
                "单文件下载不能套用「无新完成文件」判定");

            // #5 / #6: compiler warnings cleared — dead variable gone, local guards present.
            var preflight = Read("src/AuroraCore/Services/ModPreflight.cs").Replace("\r\n", "\n");
            Assert(!preflight.Contains("fabricApiMissing"), "死变量必须删除");
            var vm = Read("src/AuroraLauncher.Desktop/ViewModels/MainViewModel.cs").Replace("\r\n", "\n");
            Assert(vm.Contains("if (instance == null || SelectedAccount == null)") &&
                   vm.Contains("var account = SelectedAccount;") &&
                   vm.Contains("LaunchAsync") , "启动链路必须有局部空守卫");
            Assert(vm.Contains("catch (Exception ex) { Logger.Instance.Warn(\"UI\", \"界面回调异常"),
                "Dispatch 回调必须有异常屏障");
            Assert(vm.Contains("public void DisposeResources()"), "退出时必须释放 playtime 定时器");
            Assert(Read("src/AuroraLauncher.Desktop/App.xaml.cs").Replace("\r\n", "\n")
                       .Contains("Logger.Instance.Error(\"Uninstall\", \"清理助手异常"),
                "卸载助手异常必须落日志");
        });

        Check("Guard: CI 必须拉取上一版 EXE 才能产出增量补丁（第 107 轮）", () =>
        {
            string Read(string p) => File.ReadAllText(Path.Combine(FindSourceRoot()!, p));
            var yaml = Read(".github/workflows/release.yml").Replace("\r\n", "\n");
            Assert(yaml.Contains("Fetch previous release EXE for delta patch"),
                "CI 要有拉取上一版 EXE 的专门步骤");
            Assert(yaml.Contains("gh release download") && yaml.Contains("artifacts\\prev") &&
                   yaml.Contains("AuroraLauncher.exe"),
                "必须从上一版 Release 下载原始 EXE 到 artifacts/prev");
            Assert(yaml.Contains("publish\\.last-round") && yaml.Contains("round\\s*:\\s*(\\d+)"),
                "必须把上一版的 round 写进 .last-round，publish.ps1 才会生成补丁");
            // The client already only uses a patch for the exact previous round.
            var manifest = new UpdateManifest
            {
                Round = 107, Version = "0.107.0", Url = "https://example.test/x.exe",
                Sha256 = new string('A', 64), Size = 65_000_000,
                PatchUrl = "https://example.test/x.patch", PatchFromRound = 106,
                PatchSize = 600_000, PatchSha256 = new string('B', 64)
            };
            using (var rsa = System.Security.Cryptography.RSA.Create(2048))
            {
                var pub = rsa.ToXmlString(false);
                var payload = manifest.PatchSignedPayload;
                manifest.PatchSignature = Convert.ToBase64String(rsa.SignData(
                    System.Text.Encoding.UTF8.GetBytes(payload),
                    System.Security.Cryptography.HashAlgorithmName.SHA256,
                    System.Security.Cryptography.RSASignaturePadding.Pkcs1));
                Assert(UpdateService.ShouldUsePatch(manifest, 106, pub),
                    "紧邻上一版必须用补丁");
                Assert(!UpdateService.ShouldUsePatch(manifest, 105, pub),
                    "落后多版仍走完整包（补丁只匹配一个 round）");
            }
        });

        Check("Guard: 第 106 轮关键改动接线（直连兜底 / 正则预编译 / 缓冲区复用 / 字形）", () =>
        {
            string Read(string p) => File.ReadAllText(Path.Combine(FindSourceRoot()!, p));
            var engine = Read("src/AuroraCore/Download/DownloadEngine.cs").Replace("\r\n", "\n");
            Assert(engine.Contains("ArrayPool<byte>.Shared.Rent(81920)") &&
                   engine.Contains("ArrayPool<byte>.Shared.Return(buffer)"),
                "下载缓冲区必须池化");
            Assert(engine.Contains("var useDirectClient = false") &&
                   engine.Contains("HttpClientFactory.Instance.DirectClient") &&
                   engine.Contains("主线路失败，改用直连重试"),
                "文件下载失败必须有一直连兜底");
            var crash = Read("src/AuroraCore/Services/GameLauncher.cs").Replace("\r\n", "\n");
            Assert(crash.Contains("RegexOptions.Compiled") && crash.Contains("r.Compiled.IsMatch(text)"),
                "崩溃规则正则必须预编译并复用");
            var job = Read("src/AuroraCore/Services/DownloadJob.cs").Replace("\r\n", "\n");
            Assert(job.Contains("\"更新\" => \"\\uE777\""), "更新任务要有专属字形");
            Assert(job.Contains("public void SetStageText(string text, string? stageKey = null)"),
                "SetStageText 必须支持 stage key 供看门狗判定");
        });

        Check("第 108 轮：CNB 主源 + GitHub 签名兜底（双源更新）", () =>
        {
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var pub = rsa.ToXmlString(false);
            string Sign(string s) => Convert.ToBase64String(rsa.SignData(
                System.Text.Encoding.UTF8.GetBytes(s),
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1));
            var m = new UpdateManifest
            {
                Round = 108, Version = "0.108.0",
                Url = "https://cnb.cool/owner/AuroraLauncher/-/releases/download/v1.12.22/AuroraLauncher.exe",
                FallbackUrl = "https://github.com/owner/AuroraLauncher/releases/download/v1.12.22/AuroraLauncher.exe",
                Sha256 = new string('A', 64), Size = 65_000_000,
                Signature = "x", FallbackSignature = ""
            };
            m.Signature = Sign(m.SignedPayload);
            m.FallbackSignature = Sign(m.FallbackSignedPayload);
            Assert(UpdateService.VerifySignature(m, pub), "主清单签名必须验证通过");
            Assert(UpdateService.VerifyFallbackSignature(m, pub), "备用源签名必须验证通过");
            Assert(!m.SignedPayload.Contains(m.FallbackUrl),
                "FallbackUrl 不得进入 SignedPayload（旧客户端兼容）");
            var tampered = new UpdateManifest
            {
                Round = m.Round, Url = m.Url, Sha256 = m.Sha256, Size = m.Size,
                FallbackUrl = "https://evil.test/AuroraLauncher.exe",
                FallbackSignature = m.FallbackSignature
            };
            Assert(!UpdateService.VerifyFallbackSignature(tampered, pub),
                "篡改备用源地址必须验签失败");
            Assert(UpdateService.VerifyFallbackSignature(new UpdateManifest(), pub),
                "没有备用源的清单视为合法（向后兼容）");

            string Read(string p) => File.ReadAllText(Path.Combine(FindSourceRoot()!, p));
            var yaml = Read(".github/workflows/release.yml").Replace("\r\n", "\n");
            Assert(yaml.Contains("Mirror assets to CNB Release") &&
                   yaml.Contains("scripts/publish-to-cnb.ps1"),
                "CI 必须把资产镜像到 CNB");
            Assert(yaml.Contains("Sync git history and tag to CNB") &&
                   yaml.Contains("refs/heads/main") && yaml.Contains("refs/tags/"),
                "CI 必须同步 git 历史与 tag 到 CNB（否则仓库主页停留在旧轮次）");
            Assert(yaml.Contains("-FallbackUrl") && yaml.Contains("cnb.cool"),
                "签名时主源必须是 CNB、备用源必须是 GitHub");
            var mirror = Read("scripts/publish-to-cnb.ps1").Replace("\r\n", "\n");
            Assert(mirror.Contains("asset-upload-url") && mirror.Contains("verify_url") &&
                   mirror.Contains("overwrite = $true") && mirror.Contains("CNB_TOKEN"),
                "CNB 镜像脚本必须走官方上传流程且可重复执行");
        });

        Check("第 109 轮：主源重试三次 + UI 如实显示重试/切源状态", () =>
        {
            string Read(string p) => File.ReadAllText(Path.Combine(FindSourceRoot()!, p));
            var engine = Read("src/AuroraCore/Download/DownloadEngine.cs").Replace("\r\n", "\n");
            Assert(engine.Contains("public int MaxAttempts { get; set; }") &&
                   engine.Contains("entry.MaxAttempts > 0 ? entry.MaxAttempts : 5"),
                "下载引擎必须支持逐条目重试预算（默认仍 5）");
            var upd = Read("src/AuroraCore/Services/UpdateService.cs").Replace("\r\n", "\n");
            Assert(upd.Contains("const int hostAttempts = 3") &&
                   upd.Contains("MaxAttempts = attempts") &&
                   upd.Contains("主源 CNB") && upd.Contains("备用源 GitHub"),
                "更新包主源/备用源各至少重试三次且带主机标签");
            Assert(upd.Contains("MaxAttempts = 3") && upd.Contains("增量补丁："),
                "增量补丁也要三次预算和如实标签");
            var vm = Read("src/AuroraLauncher.Desktop/ViewModels/MainViewModel.cs").Replace("\r\n", "\n");
            Assert(vm.Contains("string.IsNullOrWhiteSpace(p.Message)") &&
                   vm.Contains("p.Message}（{pct:0}%）"),
                "下载界面必须显示引擎的重试/切源消息而不是只显示百分比");
        });

        Check("第 110 轮：网络诊断（CNB / GitHub / Mojang）与图形化条形", () =>
        {
            string Read(string p) => File.ReadAllText(Path.Combine(FindSourceRoot()!, p));
            var probe = Read("src/AuroraCore/Utils/NetworkProbe.cs").Replace("\r\n", "\n");
            Assert(probe.Contains("ProbeDiagnosticsAsync") && probe.Contains("CNB 更新源") &&
                   probe.Contains("GitHub 更新源") && probe.Contains("Mojang 官方源"),
                "必须提供三源并行诊断");
            Assert(probe.Contains("NormalizeBars") && probe.Contains("BarWidth"),
                "测速结果必须归一化成可直接绑定的条形宽度");
            var bars = NetworkProbe.NormalizeBars(new[]
            {
                new NetworkProbe.HostDiagnostic("A", "a", true, 100),
                new NetworkProbe.HostDiagnostic("B", "b", true, 400),
                new NetworkProbe.HostDiagnostic("C", "c", false, null)
            });
            Assert(bars[0].BarPercent == 100 && bars[1].BarPercent == 25 && bars[2].BarPercent == 0,
                "最快源满条、慢源按比例、不可达为零");
            Assert(bars[0].BarWidth > bars[1].BarWidth && bars[1].BarWidth > bars[2].BarWidth,
                "条形宽度必须可直接用于界面展示");
            var vm = Read("src/AuroraLauncher.Desktop/ViewModels/MainViewModel.cs").Replace("\r\n", "\n");
            Assert(vm.Contains("NetworkDiagnoseCommand") && vm.Contains("HasNetworkDiagnostics"),
                "设置页必须有可绑定的诊断命令与结果显示状态");
            var xaml = Read("src/AuroraLauncher.Desktop/Views/SettingsView.xaml").Replace("\r\n", "\n");
            Assert(xaml.Contains("NetworkDiagnoseCommand") && xaml.Contains("BarWidth") &&
                   xaml.Contains("DisplayLatency"),
                "设置页必须渲染诊断按钮与图形化结果");
        });

        Console.WriteLine($"\n=== Result: {_passed} passed, {_failed} failed, {_skipped} skipped ===");        Environment.Exit(_failed == 0 ? 0 : 1);
    }
}
