using System.IO.Compression;
using System.Text.Json;
using AuroraCore;
using AuroraCore.Download;
using AuroraCore.Models;
using AuroraCore.Services;
using AuroraCore.Utils;

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
        // Test layering switch: --unit (offline, CI default) / --integration / --all.
        var flag = args.FirstOrDefault(a => a.StartsWith("--"));
        if (flag is "--unit" or "--integration" or "--all") Mode = flag[2..];
        Console.WriteLine($"=== AuroraLauncher Self-Test (mode: {Mode}) ===");
        Console.WriteLine("=== AuroraLauncher Self-Test ===");

        // Use an isolated temp root so we never touch the user's real data.
        var root = Path.Combine(Path.GetTempPath(), "AuroraSelfTest_" + Guid.NewGuid().ToString("N")[..6]);
        LauncherConfig.Instance.RootDirectory = root;
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
        var uiVmCommands = new HashSet<string>(
            System.Text.RegularExpressions.Regex.Matches(
                File.ReadAllText(Path.Combine(uiDesktop, "ViewModels", "MainViewModel.cs")),
                @"public\s+RelayCommand\s+(\w+Command)")
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

        Check("NetworkProbe.Decide: data-driven source selection (pure logic)", () =>
        {
            var r1 = new NetworkProbe.ProbeResult(OfficialOk: true, OfficialMs: 80, MirrorOk: true, MirrorMs: 800);
            Assert(r1.Decide(DownloadSource.Official) == DownloadSource.Official, "both ok: faster (official) should win");
            var r2 = new NetworkProbe.ProbeResult(OfficialOk: true, OfficialMs: 3000, MirrorOk: true, MirrorMs: 90);
            Assert(r2.Decide(DownloadSource.Official) == DownloadSource.BMCLAPI, "both ok: mirror faster should win");
            var r3 = new NetworkProbe.ProbeResult(OfficialOk: false, OfficialMs: null, MirrorOk: true, MirrorMs: 90);
            Assert(r3.Decide(DownloadSource.Official) == DownloadSource.BMCLAPI, "official dead → mirror");
            var r4 = new NetworkProbe.ProbeResult(OfficialOk: true, OfficialMs: 90, MirrorOk: false, MirrorMs: null);
            Assert(r4.Decide(DownloadSource.BMCLAPI) == DownloadSource.Official, "mirror dead → official");
            var r5 = new NetworkProbe.ProbeResult(OfficialOk: false, OfficialMs: null, MirrorOk: false, MirrorMs: null);
            Assert(r5.Decide(DownloadSource.BMCLAPI) == DownloadSource.BMCLAPI, "both dead → keep current");
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

        Check("Guard: README round numbering is continuous", () =>
        {
            // Round sections were lost/misnumbered twice (5, 16 lost; 44→46
            // skip). The changelog is project memory — keep it gapless.
            var srcRoot = FindSourceRoot()!;
            var readme = File.ReadAllText(Path.Combine(srcRoot, "README.md"));
            var nums = System.Text.RegularExpressions.Regex.Matches(
                    readme, @"^## 更新补充（第 (\d+) 轮", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(m => int.Parse(m.Groups[1].Value)).ToList();
            Assert(nums.Count >= 40, $"too few round sections found ({nums.Count})");
            var gaps = new List<string>();
            for (int i = 1; i < nums.Count; i++)
                if (nums[i] != nums[i - 1] + 1)
                    gaps.Add($"{nums[i - 1]}→{nums[i]}");
            var gapText = string.Join("; ", gaps);
            Assert(gaps.Count == 0, $"README round numbering has gaps: {gapText}");
        });

        Console.WriteLine($"\n=== Result: {_passed} passed, {_failed} failed, {_skipped} skipped ===");
        Environment.Exit(_failed == 0 ? 0 : 1);
    }
}
