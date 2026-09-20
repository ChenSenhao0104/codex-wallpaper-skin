using System.Text.Json;

namespace CodexWallpaperSkin;

public sealed record SelfTestResult(int Passed, int Failed, IReadOnlyList<string> Messages)
{
    public bool Success => Failed == 0;
}

public static class SelfTests
{
    public static SelfTestResult Run()
    {
        var passed = 0;
        var failed = 0;
        var messages = new List<string>();

        Check("MIME types", () =>
        {
            Equal("image/webp", WallpaperCatalog.MimeTypeFor("x.WEBP"));
            Equal("image/gif", WallpaperCatalog.MimeTypeFor("animated.GIF"));
            Equal("video/webm", WallpaperCatalog.MimeTypeFor("movie.webm"));
            Equal("application/x-wallpaper-engine-scene", WallpaperCatalog.MimeTypeFor("scene.pkg"));
            Equal(256L * 1024 * 1024, WallpaperCatalog.MaximumVideoBytes);
        });
        Check("loopback endpoint policy", () =>
        {
            True(CdpEndpoint.IsLoopbackHttp("http://127.0.0.1:9222"));
            True(CdpEndpoint.IsLoopbackHttp("http://127.0.0.1:9222/"));
            True(!CdpEndpoint.IsLoopbackHttp("http://localhost:9333"));
            True(!CdpEndpoint.IsLoopbackHttp("http://[::1]:9333"));
            True(!CdpEndpoint.IsLoopbackHttp("http://127.1:9222"));
            True(!CdpEndpoint.IsLoopbackHttp("http://2130706433:9222"));
            True(!CdpEndpoint.IsLoopbackHttp("http://0177.0.0.1:9222"));
            True(!CdpEndpoint.IsLoopbackHttp("http://127.000.000.001:9222"));
            True(!CdpEndpoint.IsLoopbackHttp("http://127.0.0.1"));
            True(!CdpEndpoint.IsLoopbackHttp("http://127.0.0.1:09222"));
            True(!CdpEndpoint.IsLoopbackHttp(" http://127.0.0.1:9222"));
            True(!CdpEndpoint.IsLoopbackHttp("https://127.0.0.1:9222"));
            True(!CdpEndpoint.IsLoopbackHttp("http://192.168.1.5:9222"));
            True(!CdpEndpoint.IsLoopbackHttp("http://127.0.0.1:9222/json/list"));
            True(!CdpEndpoint.IsLoopbackHttp("http://user@127.0.0.1:9222"));
            True(!CdpEndpoint.IsLoopbackHttp("http://127.0.0.1:9222/?redirect=true"));
            True(!CdpEndpoint.IsLoopbackHttp("file:///C:/temp"));
            var client = new CdpClient();
            try
            {
                True(client.IsProxyDisabled);
            }
            finally
            {
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
        Check("settings normalization", () =>
        {
            var settings = new WallpaperSettings
            {
                FocusX = -50,
                FocusY = 500,
                Opacity = 5,
                BlackOverlay = -1,
                PaletteStrength = 4,
                PanelOpacity = 0.1,
                Blur = 80,
                Brightness = 4,
                Contrast = 0,
                Saturation = 5,
                PlaybackRate = 0,
                SceneFrameRate = 44,
                SceneResolutionScale = 0.1
            }.Normalize();
            Equal(0d, settings.FocusX);
            Equal(100d, settings.FocusY);
            Equal(1d, settings.Opacity);
            Equal(0d, settings.BlackOverlay);
            Equal(1d, settings.PaletteStrength);
            Equal(0.2d, settings.PanelOpacity);
            Equal(30d, settings.Blur);
            Equal(1.5d, settings.Brightness);
            Equal(0.5d, settings.Contrast);
            Equal(2d, settings.Saturation);
            Equal(0.25d, settings.PlaybackRate);
            Equal(15, settings.SceneFrameRate);
            Equal(0.5d, settings.SceneResolutionScale);
        });
        Check("state catalog overflow fails closed", () =>
        {
            var state = new AppState
            {
                Wallpapers = Enumerable.Range(0, 5_001)
                    .Select(index => new WallpaperEntry
                    {
                        Id = "test:" + index,
                        Title = "Test " + index,
                        Source = "Test",
                        Note = string.Empty
                    })
                    .ToList()
            };
            Throws<InvalidDataException>(() => StateStore.ValidateStateForSave(state));
        });
        Check("last applied wallpaper resolves safely", () =>
        {
            var remembered = new WallpaperEntry
            {
                Id = "local:test",
                Title = "Remembered",
                Source = "Local",
                Note = string.Empty,
                Support = WallpaperSupport.StaticPreview
            };
            var state = new AppState
            {
                LastAppliedWallpaperId = remembered.Id,
                Wallpapers = [remembered]
            };
            Equal(remembered, AutoRestoreService.ResolveLastWallpaper(state));
            state.LastAppliedWallpaperId = "missing";
            True(AutoRestoreService.ResolveLastWallpaper(state) is null);
        });
        Check("fit mapping", () =>
        {
            Equal("cover", CdpInjectionService.FitToCss(WallpaperFit.Cover));
            Equal("scale-down", CdpInjectionService.FitToCss(WallpaperFit.ScaleDown));
        });
        Check("application wallpapers rejected", () =>
        {
            using var json = JsonDocument.Parse("{\"type\":\"application\",\"title\":\"Unsafe\",\"file\":\"run.exe\"}");
            var entry = WallpaperCatalog.ParseProjectDocument(json.RootElement, @"C:\wallpaper", @"C:\wallpaper\project.json");
            Equal(WallpaperSupport.Rejected, entry.Support);
            True(entry.Note.Contains("never launched", StringComparison.OrdinalIgnoreCase));
        });
        Check("multi-root catalog scan", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "codex-wallpaper-skin-scan-tests", Guid.NewGuid().ToString("N"));
            var firstProject = Path.Combine(directory, "first-root", "100");
            var secondProject = Path.Combine(directory, "second-root", "200");
            Directory.CreateDirectory(firstProject);
            Directory.CreateDirectory(secondProject);
            try
            {
                File.WriteAllText(Path.Combine(firstProject, "project.json"), "{\"type\":\"application\",\"title\":\"First\"}");
                File.WriteAllText(Path.Combine(secondProject, "project.json"), "{\"type\":\"application\",\"title\":\"Second\"}");
                var results = WallpaperCatalog.ScanWorkshopRoots(
                    [Path.Combine(directory, "first-root"), Path.Combine(directory, "second-root")]);
                Equal(2, results.Count);
                True(results.All(item => item.Support == WallpaperSupport.Rejected));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
        Check("media signatures are enforced", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "codex-wallpaper-skin-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var validPng = Path.Combine(directory, "valid.png");
                File.WriteAllBytes(validPng, CreatePngHeader(1920, 1080));
                WallpaperCatalog.ValidateMediaFile(validPng, isVideo: false);

                var disguisedPng = Path.Combine(directory, "disguised.png");
                File.WriteAllText(disguisedPng, "not a png");
                Throws<InvalidDataException>(() => WallpaperCatalog.ValidateMediaFile(disguisedPng, isVideo: false));

                var oversizedPng = Path.Combine(directory, "oversized.png");
                File.WriteAllBytes(oversizedPng, CreatePngHeader(16384, 16384));
                Throws<InvalidDataException>(() => WallpaperCatalog.ValidateMediaFile(oversizedPng, isVideo: false));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
        Check("GIF previews are bounded and accepted", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "codex-wallpaper-skin-gif-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var validGif = Path.Combine(directory, "preview.gif");
                File.WriteAllBytes(validGif, CreateGif(192, 108));
                WallpaperCatalog.ValidateMediaFile(validGif, isVideo: false);
                var truncatedGif = Path.Combine(directory, "truncated.gif");
                File.WriteAllBytes(truncatedGif, "GIF89a"u8.ToArray());
                Throws<InvalidDataException>(() => WallpaperCatalog.ValidateMediaFile(truncatedGif, isVideo: false));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
        Check("scene.pkg is preferred over Workshop thumbnails", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "codex-wallpaper-skin-scene-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var projectPath = Path.Combine(directory, "project.json");
                File.WriteAllText(projectPath, "{\"type\":\"scene\",\"title\":\"Scene test\",\"file\":\"scene.json\",\"preview\":\"preview.gif\"}");
                File.WriteAllBytes(Path.Combine(directory, "preview.gif"), CreateGif(192, 192));
                File.WriteAllBytes(Path.Combine(directory, "scene.pkg"), CreateScenePackage("scene.json", "{\"objects\":[]}"u8.ToArray()));
                var entry = WallpaperCatalog.ParseProject(projectPath);
                Equal(WallpaperSupport.LiveScene, entry.Support);
                True(entry.IsScene);
                True(entry.MediaPath!.EndsWith("scene.pkg", StringComparison.OrdinalIgnoreCase));
                using (WallpaperCatalog.OpenValidatedMediaFile(entry)) { }

                File.Delete(Path.Combine(directory, "scene.pkg"));
                entry = WallpaperCatalog.ParseProject(projectPath);
                Equal(WallpaperSupport.AnimatedPreview, entry.Support);
                True(entry.CanApply);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
        Check("scene.pkg rejects virtual path traversal", () =>
        {
            using var stream = new MemoryStream(CreateScenePackage("../scene.json", "{\"objects\":[]}"u8.ToArray()));
            Throws<InvalidDataException>(() => ScenePackageValidator.Validate(stream));
        });
        Check("missing media fails closed", () =>
        {
            var missing = Path.Combine(Path.GetTempPath(), "codex-wallpaper-skin-missing", Guid.NewGuid().ToString("N"), "missing.png");
            Throws<FileNotFoundException>(() => WallpaperCatalog.CreateLocal(missing));
        });
        Check("Wallpaper Engine media remains inside its project", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "codex-wallpaper-skin-containment-tests", Guid.NewGuid().ToString("N"));
            var projectDirectory = Path.Combine(directory, "project");
            Directory.CreateDirectory(projectDirectory);
            try
            {
                var projectPath = Path.Combine(projectDirectory, "project.json");
                File.WriteAllText(projectPath, "{\"type\":\"image\"}");
                var inside = Path.Combine(projectDirectory, "inside.png");
                var outside = Path.Combine(directory, "outside.png");
                File.WriteAllBytes(inside, CreatePngHeader(1920, 1080));
                File.WriteAllBytes(outside, CreatePngHeader(1920, 1080));
                var entry = new WallpaperEntry
                {
                    Source = "Wallpaper Engine",
                    ProjectPath = projectPath,
                    MediaPath = inside,
                    Kind = WallpaperKind.Image,
                    Support = WallpaperSupport.Direct
                };
                using (WallpaperCatalog.OpenValidatedMediaFile(entry)) { }
                entry.MediaPath = outside;
                Throws<InvalidDataException>(() =>
                {
                    using var stream = WallpaperCatalog.OpenValidatedMediaFile(entry);
                });
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });
        Check("CDP target selection fails closed", () =>
        {
            var valid = new CdpTarget("codex", "page", "Codex", "app://codex/", "ws://127.0.0.1:9222/devtools/page/codex");
            var browser = new CdpTarget("browser", "page", "Browser", "https://example.com", "ws://127.0.0.1:9222/devtools/page/browser");
            Equal(valid, CdpDiscovery.SelectCodexPage(new[] { browser, valid }));
            Throws<InvalidOperationException>(() => CdpDiscovery.SelectCodexPage(new[] { browser }));
        });
        Check("official Codex package identity policy", () =>
        {
            const string packageFamily = "OpenAI.Codex_2p2nqsd0c76g0";
            const string packageFullName = "OpenAI.Codex_26.908.9136.0_x64__2p2nqsd0c76g0";
            True(CdpProcessIdentity.IsOfficialPackageIdentity(packageFamily, packageFullName));
            True(!CdpProcessIdentity.IsOfficialPackageIdentity("OpenAI.Codex_wrongpublisher", packageFullName));
            True(!CdpProcessIdentity.IsOfficialPackageIdentity(packageFamily,
                "OpenAI.Codex_26.908.9136.0_arm64__2p2nqsd0c76g0"));
            True(!CdpProcessIdentity.IsOfficialPackageIdentity(packageFamily,
                "OpenAI.ChatGPT_26.908.9136.0_x64__2p2nqsd0c76g0"));

            var secondaryDriveRoot = Path.Combine(@"E:\WindowsApps", packageFullName);
            var systemDriveRoot = Path.Combine(@"C:\Program Files\WindowsApps", packageFullName);
            True(CdpProcessIdentity.HasExpectedCodexExecutableLayout(
                Path.Combine(secondaryDriveRoot, "app", "ChatGPT.exe"), packageFullName));
            True(CdpProcessIdentity.HasExpectedCodexExecutableLayout(
                Path.Combine(systemDriveRoot, "app", "Codex.exe"), packageFullName));
            True(!CdpProcessIdentity.HasExpectedCodexExecutableLayout(
                @"C:\Program Files\Google\Chrome\Application\chrome.exe", packageFullName));
            True(!CdpProcessIdentity.HasExpectedCodexExecutableLayout(
                Path.Combine(secondaryDriveRoot, "nested", "app", "ChatGPT.exe"), packageFullName));
            True(!CdpProcessIdentity.HasExpectedCodexExecutableLayout(
                Path.Combine(secondaryDriveRoot, "app", "ChatGPT.exe.bak"), packageFullName));
            True(AppActivation.IsOfficialAumid("OpenAI.Codex_2p2nqsd0c76g0!App"));
            True(!AppActivation.IsOfficialAumid("OpenAI.Codex_wrongpublisher!App"));
            Throws<ArgumentException>(() => AppActivation.ActivateWithCdpAsync(
                "OpenAI.Codex_wrongpublisher!App",
                "http://127.0.0.1:9222").GetAwaiter().GetResult());
            Throws<ArgumentException>(() => AppActivation.ActivateWithCdpAsync(
                AppActivation.OfficialAumid,
                "http://localhost:9222").GetAwaiter().GetResult());
        });
        Check("palette runtime is one-shot and reversible", () =>
        {
            True(CdpInjectionService.BootstrapScript.Contains("pointer-events: none", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("z-index: -1", StringComparison.Ordinal));
            True(!CdpInjectionService.BootstrapScript.Contains("body > :not", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("canvas.width = 32", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("cws-palette", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("existing.version === 12", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("__codexWallpaperSkinBeginCapturedStream", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("__codexWallpaperSkinSetCapturedFrame", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("pointermove", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("--app-color-background-surface: transparent", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("setProperty('--cws-root-alpha', '0')", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("markedAncestor", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("removeProperty(name)", StringComparison.Ordinal));
            True(CdpInjectionService.NativeSurfaceProbeScript.Contains("active.nativeSurface", StringComparison.Ordinal));
            True(CdpInjectionService.NativeSurfaceProbeScript.Contains("cws-active", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("cancelAnimationFrame", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("await waitForMedia", StringComparison.Ordinal));
            True(!CdpInjectionService.BootstrapScript.Contains("setInterval", StringComparison.Ordinal));
            True(CdpInjectionService.CleanupScript.Contains("__codexWallpaperSkinCleanup", StringComparison.Ordinal));
            True(!CdpInjectionService.CleanupVerificationScript.Contains("nativeThemeMarker", StringComparison.Ordinal));
            True(CdpInjectionService.CleanupVerificationScript.Contains("[data-cws-surface]", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains(".cws-media, .cws-overlay", StringComparison.Ordinal));
            True(!CdpInjectionService.BootstrapScript.Contains("require(", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains(SceneRuntimeSource.UpstreamRevision, StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("__cwsCreateSceneWallpaper", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("application/x-wallpaper-engine-scene", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("256 * 1024 * 1024", StringComparison.Ordinal));
            True(SceneRuntimeSource.Script.TrimEnd().EndsWith(';'));
            True(!CdpInjectionService.BootstrapScript.Contains("fetch(", StringComparison.Ordinal));
            True(!CdpInjectionService.BootstrapScript.Contains("XMLHttpRequest", StringComparison.Ordinal));
        });

        Check("connection recovery classifies every product state", () =>
        {
            Equal(CodexConnectionState.CodexClosed, ConnectionRecovery.Classify(
                new ConnectionProbe(true, false, false, false, 0, false, QueueFailureReason.None)));
            Equal(CodexConnectionState.CodexStarting, ConnectionRecovery.Classify(
                new ConnectionProbe(true, true, true, false, 0, false, QueueFailureReason.None)));
            Equal(CodexConnectionState.CodexConnected, ConnectionRecovery.Classify(
                new ConnectionProbe(true, true, true, true, 0, false, QueueFailureReason.None)));
            Equal(CodexConnectionState.RunningWithoutCdp, ConnectionRecovery.Classify(
                new ConnectionProbe(true, false, false, false, 2, false, QueueFailureReason.None)));
            Equal(CodexConnectionState.Queued, ConnectionRecovery.Classify(
                new ConnectionProbe(true, false, false, false, 2, true, QueueFailureReason.None)));
            Equal(CodexConnectionState.RetryFailed, ConnectionRecovery.Classify(
                new ConnectionProbe(true, false, false, false, 0, true, QueueFailureReason.CdpNotReady)));
            Equal(CodexConnectionState.RetryFailed, ConnectionRecovery.Classify(
                new ConnectionProbe(true, true, false, false, 0, false, QueueFailureReason.None)));
            Equal(CodexConnectionState.RetryFailed, ConnectionRecovery.Classify(
                new ConnectionProbe(false, false, false, false, 0, false, QueueFailureReason.None)));
            // A reachable Codex page always wins, and a queued wallpaper never
            // reports itself as applied.
            Equal(CodexConnectionState.CodexConnected, ConnectionRecovery.Classify(
                new ConnectionProbe(true, true, true, true, 1, true, QueueFailureReason.CdpNotReady)));
        });
        Check("recovery guidance is concise and free of raw diagnostics", () =>
        {
            foreach (var state in Enum.GetValues<CodexConnectionState>())
            {
                var badge = ConnectionRecovery.Badge(state);
                var guidance = ConnectionRecovery.Guidance(state);
                var action = ConnectionRecovery.ActionLabel(state);
                True(badge.Length is > 0 and <= 32);
                True(guidance.Length is > 0 and <= 260);
                True(action.Length is > 0 and <= 32);
                foreach (var forbidden in new[] { "PID", "0x", "Exception", "127.0.0.1", "hr=", "SocketException", "stack" })
                {
                    True(!guidance.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
                    True(!badge.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
                }
            }
        });
        Check("queued wallpaper survives and never reports itself as applied", () =>
        {
            var state = new AppState();
            var applied = new WallpaperEntry
            {
                Id = "local:applied",
                Title = "Applied",
                Source = "Local",
                Note = string.Empty,
                Support = WallpaperSupport.Direct,
                MediaPath = "applied.png"
            };
            state.LastAppliedWallpaperId = applied.Id;
            state.Wallpapers = [applied];

            var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
            WallpaperQueue.Enqueue(state, applied.Id, QueueFailureReason.CodexRunningWithoutCdp, now);
            True(WallpaperQueue.HasQueued(state));
            Equal(applied.Id, WallpaperQueue.QueuedWallpaperId(state));
            Equal(now, state.PendingQueuedAt);
            Equal(0, state.PendingAttempts);
            Equal(QueueFailureReason.CodexRunningWithoutCdp, state.PendingLastFailure);

            WallpaperQueue.RecordFailure(state, QueueFailureReason.CdpNotReady, now.AddMinutes(1));
            Equal(1, state.PendingAttempts);
            Equal(QueueFailureReason.CdpNotReady, state.PendingLastFailure);
            Equal(now.AddMinutes(1), state.PendingLastAttemptAt);

            WallpaperQueue.Clear(state);
            True(!WallpaperQueue.HasQueued(state));
            Equal(0, state.PendingAttempts);
            Equal(QueueFailureReason.None, state.PendingLastFailure);
            True(state.PendingQueuedAt is null && state.PendingLastAttemptAt is null);
            // Cancelling a queue must not look like applying or restoring.
            Equal(applied.Id, state.LastAppliedWallpaperId);

            Throws<ArgumentException>(() => WallpaperQueue.Enqueue(state, string.Empty, QueueFailureReason.None, now));
            Throws<ArgumentException>(() => WallpaperQueue.Enqueue(state, new string('x', 2049), QueueFailureReason.None, now));
        });
        Check("state schema 6 keeps a queue across restart", () =>
        {
            var legacy = new AppState
            {
                SchemaVersion = 5,
                PendingWallpaperId = "local:queued",
                PendingActivation = true
            };
            StateStore.MigrateState(legacy);
            Equal(6, legacy.SchemaVersion);
            True(WallpaperQueue.HasQueued(legacy));
            Equal(0, legacy.PendingAttempts);
            Equal(QueueFailureReason.None, legacy.PendingLastFailure);

            var orphaned = new AppState { PendingWallpaperId = null, PendingActivation = true, PendingAttempts = 4 };
            StateStore.NormalizeState(orphaned);
            True(!orphaned.PendingActivation);
            Equal(0, orphaned.PendingAttempts);

            var bounded = new AppState { PendingWallpaperId = "local:x", PendingActivation = true, PendingAttempts = 9_999 };
            StateStore.NormalizeState(bounded);
            Equal(1_000, bounded.PendingAttempts);
            True(WallpaperQueue.HasQueued(bounded));
            Throws<InvalidDataException>(() => StateStore.ValidateStateForSave(
                new AppState { PendingWallpaperId = "local:x", PendingAttempts = -1 }));
        });
        Check("controller invocation round-trips and detects a moved executable", () =>
        {
            var expected = new ControllerInvocation(@"C:\apps\skin\CodexWallpaperSkin.exe", null, "--wait-and-restore");
            Equal(StartupRegistrationStatus.Disabled, StartupRegistration.Evaluate(null, expected));
            Equal(StartupRegistrationStatus.Disabled, StartupRegistration.Evaluate("   ", expected));
            Equal(StartupRegistrationStatus.Unreadable, StartupRegistration.Evaluate("CodexWallpaperSkin.exe", expected));
            Equal(StartupRegistrationStatus.Current, StartupRegistration.Evaluate(
                "\"C:\\apps\\skin\\CodexWallpaperSkin.exe\" --wait-and-restore", expected));
            Equal(StartupRegistrationStatus.Current, StartupRegistration.Evaluate(
                "\"c:\\APPS\\skin\\CodexWallpaperSkin.exe\" --wait-and-restore", expected));
            Equal(StartupRegistrationStatus.LegacyArgument, StartupRegistration.Evaluate(
                "\"C:\\apps\\skin\\CodexWallpaperSkin.exe\" --auto-restore", expected));
            Equal(StartupRegistrationStatus.StaleExecutable, StartupRegistration.Evaluate(
                "\"C:\\old\\CodexWallpaperSkin.exe\" --wait-and-restore", expected));
            Equal(StartupRegistrationStatus.StaleExecutable, StartupRegistration.Evaluate(
                "\"C:\\apps\\skin\\CodexWallpaperSkin.exe\" --unknown-switch", expected));

            var parsed = ControllerInvocation.Parse("\"C:\\Program Files\\dotnet\\dotnet.exe\" \"D:\\skin build\\CodexWallpaperSkin.dll\" --wait-and-restore");
            True(parsed is not null);
            Equal("D:\\skin build\\CodexWallpaperSkin.dll", parsed!.AssemblyPath);
            Equal("--wait-and-restore", parsed.Argument);
            var reparsed = ControllerInvocation.Parse(parsed.ToCommandLine());
            True(reparsed is not null);
            True(reparsed!.Matches(parsed));
            True(parsed.TargetsSameBinary(reparsed));
            True(parsed.Matches(new ControllerInvocation(parsed.Executable, parsed.AssemblyPath, "--WAIT-AND-RESTORE")));
            True(!parsed.TargetsSameBinary(expected));

            var startup = new StartupRegistrationState(StartupRegistrationStatus.StaleExecutable, parsed);
            True(startup.NeedsRepair);
            True(startup.IsEnabled);
            True(!new StartupRegistrationState(StartupRegistrationStatus.Disabled, null).IsEnabled);
            True(!new StartupRegistrationState(StartupRegistrationStatus.Current, parsed).NeedsRepair);
        });
        Check("connection coordinator recovers after a Windows restart", () =>
        {
            // Codex is closed: Connect must activate the verified flow, wait for
            // readiness, then apply the queued wallpaper.
            var state = QueuedState("local:queued", QueueFailureReason.None);
            var environment = new FakeConnectionEnvironment
            {
                ProbeResults = { new EndpointProbe(true, false, false, false, 0) },
                ProbeAfterActivation = new EndpointProbe(true, true, true, true, 1)
            };
            var session = new FakeAttachSession { SucceedAfterAttempts = 1 };
            var result = ConnectionCoordinator.ConnectAsync(state, session, environment, applyQueuedWallpaper: true)
                .GetAwaiter().GetResult();
            Equal(ConnectionOutcome.Connected, result.Outcome);
            Equal(CodexConnectionState.CodexConnected, result.State);
            True(result.ActivatedCodex);
            Equal(1, environment.ActivationCount);
            Equal(1, session.ApplyCount);
            True(!WallpaperQueue.HasQueued(state));
            Equal("local:queued", state.LastAppliedWallpaperId);
            Equal("local:queued", result.AppliedWallpaper?.Id);
        });
        Check("connect only reapplies a previous wallpaper when restore is enabled", () =>
        {
            var entry = new WallpaperEntry
            {
                Id = "local:last",
                Title = "Last applied",
                Source = "Local",
                Note = string.Empty,
                Support = WallpaperSupport.Direct,
                MediaPath = "last.png"
            };
            var state = new AppState
            {
                Wallpapers = [entry],
                LastAppliedWallpaperId = entry.Id,
                AutoRestoreOnLaunch = false
            };
            var environment = new FakeConnectionEnvironment
            {
                ProbeResults = { new EndpointProbe(true, true, true, true, 1) }
            };
            var session = new FakeAttachSession();
            var result = ConnectionCoordinator.ConnectAsync(state, session, environment, applyQueuedWallpaper: true)
                .GetAwaiter().GetResult();
            Equal(ConnectionOutcome.Connected, result.Outcome);
            Equal(0, session.ApplyCount);
            True(result.AppliedWallpaper is null);
            Equal(entry.Id, state.LastAppliedWallpaperId);

            state.AutoRestoreOnLaunch = true;
            var restoring = new FakeAttachSession();
            var restored = ConnectionCoordinator.ConnectAsync(state, restoring, environment, applyQueuedWallpaper: true)
                .GetAwaiter().GetResult();
            Equal(1, restoring.ApplyCount);
            Equal(entry.Id, restored.AppliedWallpaper?.Id);
        });
        Check("running Codex without CDP is queued, never killed", () =>
        {
            var state = QueuedState("local:queued", QueueFailureReason.None);
            state.LastAppliedWallpaperId = null;
            var environment = new FakeConnectionEnvironment
            {
                ProbeResults = { new EndpointProbe(true, false, false, false, 3) }
            };
            var session = new FakeAttachSession();
            var result = ConnectionCoordinator.ConnectAsync(state, session, environment, applyQueuedWallpaper: true)
                .GetAwaiter().GetResult();
            Equal(ConnectionOutcome.Queued, result.Outcome);
            Equal(CodexConnectionState.Queued, result.State);
            Equal(0, environment.ActivationCount);
            Equal(0, session.AttachCount);
            True(WallpaperQueue.HasQueued(state));
            Equal(QueueFailureReason.CodexRunningWithoutCdp, state.PendingLastFailure);
            True(state.LastAppliedWallpaperId is null);
            Equal("local:queued", state.PendingWallpaperId);
        });
        Check("Codex already starting with CDP is waited for, not re-activated", () =>
        {
            var state = QueuedState("local:queued", QueueFailureReason.None);
            var environment = new FakeConnectionEnvironment
            {
                // The verified Codex process already owns the loopback port but its
                // page target is not exposed yet.
                ProbeResults = { new EndpointProbe(true, true, true, false, 1) }
            };
            var session = new FakeAttachSession { SucceedAfterAttempts = 3 };
            var result = ConnectionCoordinator.ConnectAsync(state, session, environment, applyQueuedWallpaper: true)
                .GetAwaiter().GetResult();
            Equal(ConnectionOutcome.Connected, result.Outcome);
            Equal(0, environment.ActivationCount);
            True(!result.ActivatedCodex);
            True(environment.Elapsed > TimeSpan.Zero);
            True(!WallpaperQueue.HasQueued(state));
            Equal("local:queued", state.LastAppliedWallpaperId);
        });
        Check("unverified listener and readiness failure fall back safely", () =>
        {
            // An unverified program owns the port: recovery must move to a fresh
            // loopback port instead of attaching to it.
            var state = QueuedState("local:queued", QueueFailureReason.None);
            var environment = new FakeConnectionEnvironment
            {
                ProbeResults =
                {
                    new EndpointProbe(true, true, false, false, 0),
                    new EndpointProbe(true, false, false, false, 0)
                },
                ProbeAfterActivation = new EndpointProbe(true, true, true, true, 1)
            };
            var session = new FakeAttachSession { SucceedAfterAttempts = 1 };
            var result = ConnectionCoordinator.ConnectAsync(state, session, environment, applyQueuedWallpaper: true)
                .GetAwaiter().GetResult();
            Equal(ConnectionOutcome.Connected, result.Outcome);
            Equal(1, environment.CreatedEndpointCount);
            Equal(environment.CreatedEndpoint, state.CdpBaseUrl);
            Equal(1, environment.ActivationCount);

            // A blocked port on a machine where Codex is already open without its
            // channel must still queue instead of activating Codex.
            var blocked = QueuedState("local:queued", QueueFailureReason.None);
            blocked.LastAppliedWallpaperId = null;
            var blockedEnvironment = new FakeConnectionEnvironment
            {
                ProbeResults =
                {
                    new EndpointProbe(true, true, false, false, 0),
                    new EndpointProbe(true, false, false, false, 2)
                }
            };
            var blockedSession = new FakeAttachSession();
            var blockedResult = ConnectionCoordinator.ConnectAsync(
                blocked, blockedSession, blockedEnvironment, applyQueuedWallpaper: true).GetAwaiter().GetResult();
            Equal(ConnectionOutcome.Queued, blockedResult.Outcome);
            Equal(0, blockedEnvironment.ActivationCount);
            True(WallpaperQueue.HasQueued(blocked));
            Equal(QueueFailureReason.CodexRunningWithoutCdp, blocked.PendingLastFailure);

            // Codex never becomes ready: bounded retry, queue preserved.
            var failing = QueuedState("local:queued", QueueFailureReason.None);
            failing.LastAppliedWallpaperId = null;
            var failingEnvironment = new FakeConnectionEnvironment
            {
                ProbeResults = { new EndpointProbe(true, false, false, false, 0) }
            };
            var failingSession = new FakeAttachSession { SucceedAfterAttempts = int.MaxValue };
            var failed = ConnectionCoordinator.ConnectAsync(failing, failingSession, failingEnvironment, applyQueuedWallpaper: true)
                .GetAwaiter().GetResult();
            Equal(ConnectionOutcome.RetryFailed, failed.Outcome);
            Equal(CodexConnectionState.RetryFailed, failed.State);
            True(failingEnvironment.Elapsed >= TimeSpan.FromSeconds(30));
            True(WallpaperQueue.HasQueued(failing));
            Equal(1, failing.PendingAttempts);
            Equal(QueueFailureReason.CdpNotReady, failing.PendingLastFailure);
            True(failing.LastAppliedWallpaperId is null);
        });
        Check("apply failure keeps the queue and reports no success", () =>
        {
            var state = QueuedState("local:queued", QueueFailureReason.None);
            state.LastAppliedWallpaperId = null;
            var environment = new FakeConnectionEnvironment
            {
                ProbeResults = { new EndpointProbe(true, true, true, true, 1) }
            };
            var session = new FakeAttachSession { ApplyException = new IOException("renderer rejected the frame") };
            var result = ConnectionCoordinator.ConnectAsync(state, session, environment, applyQueuedWallpaper: true)
                .GetAwaiter().GetResult();
            Equal(ConnectionOutcome.RetryFailed, result.Outcome);
            Equal(0, environment.ActivationCount);
            True(WallpaperQueue.HasQueued(state));
            Equal("local:queued", state.PendingWallpaperId);
            True(state.LastAppliedWallpaperId is null);

            // A passive attach never activates Codex and never applies a queue.
            var passive = QueuedState("local:queued", QueueFailureReason.None);
            var passiveEnvironment = new FakeConnectionEnvironment
            {
                ProbeResults = { new EndpointProbe(true, false, false, false, 0) }
            };
            var passiveSession = new FakeAttachSession();
            True(!ConnectionCoordinator.TryAttachWithoutActivationAsync(
                passive, passiveSession, passiveEnvironment).GetAwaiter().GetResult());
            Equal(0, passiveEnvironment.ActivationCount);
            Equal(0, passiveSession.ApplyCount);
            True(WallpaperQueue.HasQueued(passive));
        });

        Check("frame quality rejects empty and uniform captures", () =>
        {
            var uniform = new byte[256 * 4];
            for (var offset = 0; offset < uniform.Length; offset += 4)
            {
                uniform[offset] = 128;
                uniform[offset + 1] = 128;
                uniform[offset + 2] = 128;
                uniform[offset + 3] = 255;
            }
            var uniformResult = FrameQualityEvaluator.Evaluate(uniform);
            True(!uniformResult.Acceptable);
            Equal(256, uniformResult.SampleCount);
            True(uniformResult.Reason.Contains("uniform", StringComparison.OrdinalIgnoreCase));

            var black = new byte[256 * 4];
            for (var offset = 0; offset < black.Length; offset += 4)
            {
                black[offset + 3] = 255;
            }
            var blackResult = FrameQualityEvaluator.Evaluate(black);
            True(!blackResult.Acceptable);
            True(blackResult.Reason.Contains("black", StringComparison.OrdinalIgnoreCase));

            // A surface too small to describe anything is never presented either.
            True(!FrameQualityEvaluator.Evaluate(new byte[16]).Acceptable);

            var scene = new byte[256 * 4];
            for (var offset = 0; offset < scene.Length; offset += 4)
            {
                var value = (byte)(offset / 4 % 200);
                scene[offset] = value;
                scene[offset + 1] = (byte)(value / 2);
                scene[offset + 2] = (byte)(255 - value);
                scene[offset + 3] = 255;
            }
            var sceneResult = FrameQualityEvaluator.Evaluate(scene);
            True(sceneResult.Acceptable);
            True(sceneResult.LuminanceSpread >= FrameQualityEvaluator.MinimumLuminanceSpread);

            // A legitimately dark scene still has structure and must be accepted;
            // the gate rejects blank surfaces, not dark wallpapers.
            var darkScene = new byte[256 * 4];
            for (var offset = 0; offset < darkScene.Length; offset += 4)
            {
                var value = (byte)(4 + offset / 4 % 12);
                darkScene[offset] = value;
                darkScene[offset + 1] = value;
                darkScene[offset + 2] = value;
                darkScene[offset + 3] = 255;
            }
            True(FrameQualityEvaluator.Evaluate(darkScene).Acceptable);
        });
        Check("native Scene eligibility ignores the safe package parser", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "codex-wallpaper-skin-native-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                // A PKGV0024 package is outside PKGV0012-PKGV0023, so the safe
                // parser rejects it. Native eligibility must not care.
                var v24 = CreateScenePackage("scene.json", "{\"objects\":[]}"u8.ToArray(), "PKGV0024");
                var project24 = CreateWorkshopProject(
                    root, "3798584436", "{\"type\":\"scene\",\"title\":\"Lofi Girl\",\"file\":\"scene.json\",\"preview\":\"preview.gif\"}",
                    withEngine: true, v24, CreateGif(192, 108));
                True(!ScenePackageValidator.TryValidate(Path.Combine(Path.GetDirectoryName(project24)!, "scene.pkg"), out _));
                var entry = WallpaperCatalog.ParseProject(project24);
                Equal(WallpaperSupport.NativeScene, entry.Support);
                True(entry.IsNativeScene);
                True(entry.IsScene);
                True(!entry.IsBrowserScene);
                True(entry.CanApply);
                True(entry.MediaPath is null);
                True(entry.EffectivePath!.EndsWith("project.json", StringComparison.OrdinalIgnoreCase));
                True(entry.Note.Contains("PKGV", StringComparison.OrdinalIgnoreCase) == false);
                True(entry.Note.Contains("outside the built-in safe renderer", StringComparison.OrdinalIgnoreCase));
                Equal("WE NATIVE SCENE", entry.DisplayLabel.Split('[')[1].TrimEnd(']'));
                True(WallpaperEngineCaptureSession.CanUse(entry));

                // A package above the fallback size ceiling must also stay native.
                var oversized = CreateScenePackage("scene.json", "{\"objects\":[]}"u8.ToArray());
                var oversizedProject = CreateWorkshopProject(
                    root, "3801532994", "{\"type\":\"scene\",\"title\":\"Big scene\",\"file\":\"scene.json\",\"preview\":\"preview.gif\"}",
                    withEngine: true, oversized, CreateGif(192, 108));
                var oversizedPath = Path.Combine(Path.GetDirectoryName(oversizedProject)!, "scene.pkg");
                using (var stream = new FileStream(oversizedPath, FileMode.Open, FileAccess.Write))
                {
                    stream.SetLength(ScenePackageValidator.MaximumPackageBytes + 1);
                }
                True(!ScenePackageValidator.TryValidate(oversizedPath, out _));
                var oversizedEntry = WallpaperCatalog.ParseProject(oversizedProject);
                Equal(WallpaperSupport.NativeScene, oversizedEntry.Support);
                True(oversizedEntry.CanApply);
                File.Delete(oversizedPath);

                // A Scene that keeps its content loose on disk is still native.
                var looseProject = CreateWorkshopProject(
                    root, "3800850100", "{\"type\":\"scene\",\"title\":\"Loose scene\",\"file\":\"scene.json\",\"preview\":\"preview.gif\"}",
                    withEngine: true, scenePackage: null, CreateGif(192, 108));
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(looseProject)!, "scene.json"), "{\"objects\":[]}");
                var looseEntry = WallpaperCatalog.ParseProject(looseProject);
                Equal(WallpaperSupport.NativeScene, looseEntry.Support);
                True(looseEntry.CanApply);

                // Native entries have no browser media stream, and the module that
                // reads scene.pkg must be told so instead of reading project.json.
                Throws<InvalidOperationException>(() =>
                {
                    using var stream = WallpaperCatalog.OpenValidatedMediaFile(looseEntry);
                });

                // A supported package still prefers the native backend when the
                // engine is present, and keeps the safe renderer only as fallback.
                var v22 = CreateScenePackage("scene.json", "{\"objects\":[]}"u8.ToArray());
                var engineProject = CreateWorkshopProject(
                    root, "2935530316", "{\"type\":\"scene\",\"title\":\"Makima\",\"file\":\"scene.json\",\"preview\":\"preview.gif\"}",
                    withEngine: true, v22, CreateGif(192, 108));
                Equal(WallpaperSupport.NativeScene, WallpaperCatalog.ParseProject(engineProject).Support);

                // Without Wallpaper Engine beside the project the safe renderer
                // still works for a supported package.
                Directory.Delete(Path.Combine(root, "steamapps", "common", "wallpaper_engine"), recursive: true);
                var noEngineProject = CreateWorkshopProject(
                    root, "3494484288", "{\"type\":\"scene\",\"title\":\"Pastel\",\"file\":\"scene.json\",\"preview\":\"preview.gif\"}",
                    withEngine: false, v22, CreateGif(192, 108));
                var browserEntry = WallpaperCatalog.ParseProject(noEngineProject);
                Equal(WallpaperSupport.LiveScene, browserEntry.Support);
                True(browserEntry.IsBrowserScene);
                True(!browserEntry.IsNativeScene);
                True(browserEntry.EffectivePath!.EndsWith("scene.pkg", StringComparison.OrdinalIgnoreCase));
                True(!WallpaperEngineCaptureSession.CanUse(browserEntry));

                // Without the engine an unsupported package cannot silently claim
                // native rendering; it falls back to a clearly labeled preview and
                // says why.
                var unsupportedNoEngine = CreateWorkshopProject(
                    root, "3803167460", "{\"type\":\"scene\",\"title\":\"Geralt\",\"file\":\"scene.json\",\"preview\":\"preview.gif\"}",
                    withEngine: false, v24, CreateGif(192, 108));
                var previewEntry = WallpaperCatalog.ParseProject(unsupportedNoEngine);
                Equal(WallpaperSupport.AnimatedPreview, previewEntry.Support);
                True(!previewEntry.IsScene);
                True(previewEntry.CanApply);
                True(previewEntry.Note.Contains("Wallpaper Engine was not found", StringComparison.OrdinalIgnoreCase));

                // Application projects stay rejected even with an engine present.
                var applicationProject = CreateWorkshopProject(
                    root, "9999999999", "{\"type\":\"application\",\"title\":\"Unsafe\",\"file\":\"run.exe\"}",
                    withEngine: true, scenePackage: null, preview: null);
                Equal(WallpaperSupport.Rejected, WallpaperCatalog.ParseProject(applicationProject).Support);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
        Check("pointer mapping honours device scale and letterbox", () =>
        {
            // Matching aspect ratios: normalized positions map across the whole
            // surface, so device scale alone changes nothing.
            Equal((0, 0), CapturePointerTransform.MapToSurface(0, 0, 1600, 1000, 3200, 2000));
            Equal((1600, 1000), CapturePointerTransform.MapToSurface(0.5, 0.5, 1600, 1000, 3200, 2000));
            Equal((3199, 1999), CapturePointerTransform.MapToSurface(1, 1, 1600, 1000, 3200, 2000));

            // A wider surface pillar-boxes the scene, so the viewport edges land
            // inside the content rect instead of at the surface edges.
            Equal((160, 500), CapturePointerTransform.MapToSurface(0, 0.5, 1600, 1000, 1920, 1000));
            Equal((1760, 500), CapturePointerTransform.MapToSurface(1, 0.5, 1600, 1000, 1920, 1000));

            // A taller surface letterboxes it top and bottom.
            Equal((800, 100), CapturePointerTransform.MapToSurface(0.5, 0, 1600, 1000, 1600, 1200));
            Equal((800, 1100), CapturePointerTransform.MapToSurface(0.5, 1, 1600, 1000, 1600, 1200));

            // Out-of-range input is clamped instead of escaping the surface.
            Equal((0, 0), CapturePointerTransform.MapToSurface(-5, -5, 1600, 1000, 1600, 1000));
            Equal((1599, 999), CapturePointerTransform.MapToSurface(5, 5, 1600, 1000, 1600, 1000));
            Equal((0, 0), CapturePointerTransform.MapToSurface(0.5, 0.5, 0, 0, 1, 1));
        });
        Check("wheel rotation preserves direction and magnitude", () =>
        {
            Equal(-120, CapturePointerTransform.WheelDelta(100, 0));
            Equal(120, CapturePointerTransform.WheelDelta(-100, 0));
            Equal(-360, CapturePointerTransform.WheelDelta(3, 1));
            Equal(360, CapturePointerTransform.WheelDelta(-3, 1));
            Equal(-360, CapturePointerTransform.WheelDelta(1, 2));
            Equal(0, CapturePointerTransform.WheelDelta(0, 0));
            Equal(0, CapturePointerTransform.WheelDelta(double.NaN, 0));
            Equal(0, CapturePointerTransform.WheelDelta(double.PositiveInfinity, 0));
            // A single event can never claim more than nine notches.
            Equal(-1080, CapturePointerTransform.WheelDelta(10_000, 1));
            Equal(1080, CapturePointerTransform.WheelDelta(-10_000, 1));
        });
        Check("input transport carries buttons, wheel and leave in order", () =>
        {
            var bootstrap = CdpInjectionService.BootstrapScript;
            foreach (var listener in new[]
                     {
                         "'pointermove'", "'pointerdown'", "'pointerup'", "'pointercancel'",
                         "'pointerleave'", "'wheel'"
                     })
            {
                True(bootstrap.Contains("addEventListener(" + listener, StringComparison.Ordinal));
            }
            True(bootstrap.Contains("installCaptureInputHandlers", StringComparison.Ordinal));
            True(bootstrap.Contains("captureInputEvents", StringComparison.Ordinal));
            True(bootstrap.Contains("captureInputOverflow", StringComparison.Ordinal));
            True(bootstrap.Contains("drainCaptureInput", StringComparison.Ordinal));
            True(bootstrap.Contains("buttonName(event.button)", StringComparison.Ordinal));
            True(bootstrap.Contains("kind: 'wheel'", StringComparison.Ordinal));
            True(bootstrap.Contains("kind: 'leave'", StringComparison.Ordinal));
            True(bootstrap.Contains("cancelled: true", StringComparison.Ordinal));
            True(bootstrap.Contains("pointercancel", StringComparison.Ordinal));
            // The old single-button channel must be gone, and only the dedicated
            // input channel may drain the queue: a frame publish must return
            // position state alone, or it could swallow unsent input.
            True(!bootstrap.Contains("capturePointerHandlers", StringComparison.Ordinal));
            var readBodyIndex = bootstrap.IndexOf(
                "window.__codexWallpaperSkinReadCapturePointer = token => {", StringComparison.Ordinal);
            var frameBodyIndex = bootstrap.IndexOf(
                "window.__codexWallpaperSkinSetCapturedFrame = (token, encoded) => {", StringComparison.Ordinal);
            True(readBodyIndex > 0 && frameBodyIndex > readBodyIndex);
            True(bootstrap[readBodyIndex..frameBodyIndex].Contains("drainCaptureInput()", StringComparison.Ordinal));
            var frameBody = bootstrap[frameBodyIndex..];
            True(!frameBody.Contains("drainCaptureInput", StringComparison.Ordinal));
            True(frameBody.Contains("return capturePointerState();", StringComparison.Ordinal));
        });
        Check("native frames are presented atomically", () =>
        {
            var bootstrap = CdpInjectionService.BootstrapScript;
            True(bootstrap.Contains("__codexWallpaperSkinReadCapturePointer", StringComparison.Ordinal));
            True(bootstrap.Contains("installCaptureInputHandlers", StringComparison.Ordinal));
            True(bootstrap.Contains("candidate.onload", StringComparison.Ordinal));
            True(bootstrap.Contains("candidate.onerror", StringComparison.Ordinal));
            True(bootstrap.Contains("new Image()", StringComparison.Ordinal));
            True(bootstrap.Contains("captureRejectedCount", StringComparison.Ordinal));
            // The visible layer is only ever swapped after a successful decode.
            True(!bootstrap.Contains("media.onload = release", StringComparison.Ordinal));
            True(CdpInjectionService.ArtifactProbeScript.Contains("__codexWallpaperSkinReadCapturePointer", StringComparison.Ordinal));
            True(CdpInjectionService.CleanupScript.Contains("delete window.__codexWallpaperSkinReadCapturePointer", StringComparison.Ordinal));
            True(CdpInjectionService.CleanupVerificationScript.Contains("__codexWallpaperSkinReadCapturePointer", StringComparison.Ordinal));
        });

        return new SelfTestResult(passed, failed, messages);

        void Check(string name, Action test)
        {
            try
            {
                test();
                passed++;
                messages.Add("PASS " + name);
            }
            catch (Exception exception)
            {
                failed++;
                messages.Add("FAIL " + name + ": " + exception.Message);
            }
        }
    }

    /// <summary>
    /// Builds a minimal Steam-style Workshop layout so eligibility can be tested
    /// without any real Wallpaper Engine installation.
    /// </summary>
    private static string CreateWorkshopProject(
        string root,
        string workshopId,
        string projectJson,
        bool withEngine,
        byte[]? scenePackage,
        byte[]? preview)
    {
        var projectDirectory = Path.Combine(root, "steamapps", "workshop", "content", "431960", workshopId);
        Directory.CreateDirectory(projectDirectory);
        var projectPath = Path.Combine(projectDirectory, "project.json");
        File.WriteAllText(projectPath, projectJson);
        if (scenePackage is not null)
        {
            File.WriteAllBytes(Path.Combine(projectDirectory, "scene.pkg"), scenePackage);
        }
        if (preview is not null)
        {
            File.WriteAllBytes(Path.Combine(projectDirectory, "preview.gif"), preview);
        }
        if (withEngine)
        {
            var engineRoot = Path.Combine(root, "steamapps", "common", "wallpaper_engine");
            Directory.CreateDirectory(engineRoot);
            File.WriteAllText(Path.Combine(engineRoot, "wallpaper64.exe"), "engine stub");
        }
        return projectPath;
    }

    private static AppState QueuedState(string wallpaperId, QueueFailureReason reason)
    {
        var entry = new WallpaperEntry
        {
            Id = wallpaperId,
            Title = "Queued wallpaper",
            Source = "Local",
            Note = string.Empty,
            Support = WallpaperSupport.Direct,
            MediaPath = "queued.png"
        };
        var state = new AppState { Wallpapers = [entry] };
        WallpaperQueue.Enqueue(state, wallpaperId, reason, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return state;
    }

    /// <summary>
    /// Deterministic stand-in for Windows: scripted probes, a virtual clock and
    /// no real process or registry access.
    /// </summary>
    private sealed class FakeConnectionEnvironment : IConnectionEnvironment
    {
        private readonly DateTimeOffset _origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private int _probeIndex;

        public List<EndpointProbe> ProbeResults { get; } = [];

        public EndpointProbe ProbeAfterActivation { get; set; } = new(true, true, true, true, 1);

        public int ActivationCount { get; private set; }

        public int CreatedEndpointCount { get; private set; }

        public string CreatedEndpoint { get; private set; } = "http://127.0.0.1:59999";

        public TimeSpan Elapsed { get; private set; }

        public DateTimeOffset UtcNow => _origin + Elapsed;

        public TimeSpan ReadinessTimeout { get; } = TimeSpan.FromSeconds(30);

        public TimeSpan ReadinessPollInterval { get; } = TimeSpan.FromMilliseconds(500);

        public Task<EndpointProbe> ProbeAsync(string endpoint, CancellationToken cancellationToken)
        {
            if (ActivationCount > 0 || ProbeResults.Count == 0)
            {
                return Task.FromResult(ProbeAfterActivation);
            }
            var index = Math.Min(_probeIndex, ProbeResults.Count - 1);
            _probeIndex++;
            return Task.FromResult(ProbeResults[index]);
        }

        public string CreateUnusedEndpoint()
        {
            CreatedEndpointCount++;
            CreatedEndpoint = "http://127.0.0.1:" + (59999 + CreatedEndpointCount);
            return CreatedEndpoint;
        }

        public Task ActivateAsync(string aumid, string endpoint, CancellationToken cancellationToken)
        {
            ActivationCount++;
            return Task.CompletedTask;
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Elapsed += delay;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAttachSession : ICodexAttachSession
    {
        public bool IsConnected { get; private set; }

        public int AttachCount { get; private set; }

        public int ApplyCount { get; private set; }

        public int SucceedAfterAttempts { get; set; } = 1;

        public Exception? ApplyException { get; set; }

        public Task AttachAsync(string endpoint, CancellationToken cancellationToken)
        {
            AttachCount++;
            if (AttachCount >= SucceedAfterAttempts)
            {
                IsConnected = true;
                return Task.CompletedTask;
            }
            throw new InvalidOperationException("CDP page is not ready yet.");
        }

        public Task<WallpaperApplyResult> ApplyAsync(
            WallpaperEntry wallpaper,
            WallpaperSettings settings,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            ApplyCount++;
            if (ApplyException is not null)
            {
                throw ApplyException;
            }
            return Task.FromResult(new WallpaperApplyResult(null, "video", null));
        }
    }

    private static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static byte[] CreatePngHeader(int width, int height) =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
        (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height
    ];

    private static byte[] CreateGif(ushort width, ushort height)
    {
        using var stream = new MemoryStream();
        stream.Write("GIF89a"u8);
        Span<byte> word = stackalloc byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(word, width);
        stream.Write(word);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(word, height);
        stream.Write(word);
        stream.Write([0, 0, 0]);
        stream.WriteByte(0x2C);
        stream.Write([0, 0, 0, 0]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(word, width);
        stream.Write(word);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(word, height);
        stream.Write(word);
        stream.WriteByte(0);
        stream.Write([2, 2, 0x44, 0x01, 0, 0x3B]);
        return stream.ToArray();
    }

    private static byte[] CreateScenePackage(string entryName, byte[] payload, string version = "PKGV0022")
    {
        var name = System.Text.Encoding.UTF8.GetBytes(entryName);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(8u);
        writer.Write(System.Text.Encoding.ASCII.GetBytes(version));
        writer.Write(1u);
        writer.Write((uint)name.Length);
        writer.Write(name);
        writer.Write(0u);
        writer.Write((uint)payload.Length);
        writer.Write(payload);
        writer.Flush();
        return stream.ToArray();
    }
}
