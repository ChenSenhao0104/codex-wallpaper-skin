using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
            Equal(-50d, settings.FocusX);
            Equal(200d, settings.FocusY);
            Equal(1d, settings.Opacity);
            Equal(0d, settings.BlackOverlay);
            Equal(1d, settings.PaletteStrength);
            Equal(0.2d, settings.PanelOpacity);
            Equal(30d, settings.Blur);
            Equal(1.5d, settings.Brightness);
            Equal(0.5d, settings.Contrast);
            Equal(2d, settings.Saturation);
            Equal(0.25d, settings.PlaybackRate);
            Equal(30, settings.SceneFrameRate);
            Equal(0.5d, settings.SceneResolutionScale);
            Equal(60, new WallpaperSettings { SceneFrameRate = 60 }.Normalize().SceneFrameRate);
            Equal(60, new WallpaperSettings { SceneFrameRate = 144 }.Normalize().SceneFrameRate);
            Equal(30, new WallpaperSettings { SceneFrameRate = 15 }.Normalize().SceneFrameRate);
            True(new WallpaperSettings().GpuStreamEnabled);
        });
        Check("private render window task-switcher style", () =>
        {
            const long ordinaryApplicationWindow = 0x00040000L;
            const long unrelatedStyle = 0x00080000L;
            var style = WallpaperEngineCaptureSession.ToPrivateRenderExtendedStyle(
                ordinaryApplicationWindow | unrelatedStyle);
            True(WallpaperEngineCaptureSession.IsPrivateRenderExtendedStyle(style));
            Equal(0L, style & ordinaryApplicationWindow);
            Equal(unrelatedStyle, style & unrelatedStyle);
            Equal(style, WallpaperEngineCaptureSession.ToPrivateRenderExtendedStyle(style));
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
        Check("wallpaper library rename collection and filters", () =>
        {
            var entry = new WallpaperEntry
            {
                Id = "test:library",
                Title = "Complicated original title",
                CustomTitle = WallpaperLibrary.NormalizeCustomTitle("  Quiet\nNight  "),
                Collection = WallpaperLibrary.NormalizeCollection(" Relaxing "),
                Source = "Test",
                Kind = WallpaperKind.Scene,
                Support = WallpaperSupport.LiveScene,
                Note = string.Empty
            };
            Equal("Quiet Night", entry.DisplayTitle);
            True(entry.DisplayLabel.Contains("[Relaxing] Quiet Night", StringComparison.Ordinal));
            True(WallpaperLibrary.Matches(entry, "quiet", WallpaperKind.Scene, "relaxing", ungrouped: false));
            True(WallpaperLibrary.Matches(entry, "complicated", null, null, ungrouped: false));
            True(!WallpaperLibrary.Matches(entry, null, WallpaperKind.Video, null, ungrouped: false));
            True(!WallpaperLibrary.Matches(entry, null, null, null, ungrouped: true));

            var refreshed = new WallpaperEntry { Title = "Updated source title" };
            WallpaperLibrary.CopyPersonalization(entry, refreshed);
            Equal("Quiet Night", refreshed.CustomTitle);
            Equal("Relaxing", refreshed.Collection);
            var personalizations = new Dictionary<string, WallpaperPersonalization>(StringComparer.OrdinalIgnoreCase);
            WallpaperLibraryStore.Update(entry, personalizations);
            var restored = new WallpaperEntry { Id = entry.Id, Title = "Rescanned title" };
            WallpaperLibraryStore.Apply(restored, personalizations);
            Equal("Quiet Night", restored.DisplayTitle);
            Equal("Relaxing", restored.Collection);
            restored.CustomTitle = null;
            restored.Collection = null;
            WallpaperLibraryStore.Update(restored, personalizations);
            Equal(0, personalizations.Count);
            Throws<InvalidDataException>(() => WallpaperLibrary.NormalizeCustomTitle("   "));
        });
        Check("schema 6 sibling state round-trips without data loss", () =>
        {
            const string json = """
                {
                  "SchemaVersion": 6,
                  "CdpBaseUrl": "http://127.0.0.1:60239",
                  "PendingAttempts": 2,
                  "PendingLastFailure": "CdpNotReady",
                  "Wallpapers": [
                    {
                      "Id": "workshop:test",
                      "Title": "Native scene",
                      "Source": "Wallpaper Engine",
                      "ProjectPath": "C:\\wallpaper\\project.json",
                      "Kind": "Scene",
                      "Support": "NativeScene",
                      "Note": ""
                    }
                  ],
                  "Settings": {}
                }
                """;
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            options.Converters.Add(new JsonStringEnumConverter());
            var state = JsonSerializer.Deserialize<AppState>(json, options)
                ?? throw new InvalidDataException("Schema 6 test state did not deserialize.");
            Equal(6, state.SchemaVersion);
            Equal(WallpaperSupport.NativeScene, state.Wallpapers.Single().Support);
            True(state.Wallpapers.Single().IsWallpaperEngineScene);
            True(state.ExtensionData?.ContainsKey("PendingAttempts") == true);
            StateStore.ValidateStateForSave(state);

            using var roundTrip = JsonDocument.Parse(JsonSerializer.Serialize(state, options));
            Equal(2, roundTrip.RootElement.GetProperty("PendingAttempts").GetInt32());
            Equal("CdpNotReady", roundTrip.RootElement.GetProperty("PendingLastFailure").GetString());
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
            var pending = new WallpaperEntry
            {
                Id = "local:pending",
                Title = "Pending",
                Source = "Local",
                Note = string.Empty,
                Support = WallpaperSupport.StaticPreview
            };
            state.Wallpapers.Add(pending);
            state.PendingActivation = true;
            state.PendingWallpaperId = pending.Id;
            Equal(pending, AutoRestoreService.ResolveLastWallpaper(state));
            state.PendingActivation = false;
            state.LastAppliedWallpaperId = "missing";
            True(AutoRestoreService.ResolveLastWallpaper(state) is null);
        });
        Check("running-without-CDP message is actionable and non-technical", () =>
        {
            var exception = new CodexAlreadyRunningWithoutCdpException([42]);
            True(exception.Message.Contains("only when Codex starts", StringComparison.OrdinalIgnoreCase));
            True(exception.Message.Contains("not interrupted", StringComparison.OrdinalIgnoreCase));
            True(!exception.Message.Contains("listener process", StringComparison.OrdinalIgnoreCase));
            Equal(42, exception.ProcessIds.Single());
            Throws<ArgumentOutOfRangeException>(() => CodexRestartService
                .RequestNormalCloseAsync(TimeSpan.Zero).GetAwaiter().GetResult());
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
        Check("native Scene eligibility is independent from safe package parsing", () =>
        {
            var entry = new WallpaperEntry
            {
                Source = "Wallpaper Engine",
                ProjectPath = Path.Combine(Path.GetTempPath(), "workshop", "project.json"),
                PreviewPath = Path.Combine(Path.GetTempPath(), "workshop", "preview.gif"),
                Kind = WallpaperKind.Scene,
                Support = WallpaperSupport.AnimatedPreview
            };
            True(entry.IsWallpaperEngineScene);
            True(entry.CanApply);
            True(!entry.IsScene);
            True(entry.DisplayLabel.EndsWith("[WE SCENE]", StringComparison.Ordinal));
        });
        Check("captured transient frames fail closed", () =>
        {
            const int width = 64, height = 64, stride = width * 4;
            var uniform = new byte[stride * height];
            Array.Fill(uniform, (byte)31);
            for (var index = 3; index < uniform.Length; index += 4) uniform[index] = 255;
            var uniformBitmap = System.Windows.Media.Imaging.BitmapSource.Create(
                width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, uniform, stride);
            True(!CapturedFrameQuality.IsAcceptable(uniformBitmap));

            var detailed = new byte[stride * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var offset = y * stride + x * 4;
                    detailed[offset] = (byte)(x * 4);
                    detailed[offset + 1] = (byte)(y * 4);
                    detailed[offset + 2] = (byte)((x + y) * 2);
                    detailed[offset + 3] = 255;
                }
            }
            var detailedBitmap = System.Windows.Media.Imaging.BitmapSource.Create(
                width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, detailed, stride);
            True(CapturedFrameQuality.IsAcceptable(detailedBitmap));
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
            True(CdpInjectionService.BootstrapScript.Contains("existing.version === 16", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("__codexWallpaperSkinBeginCapturedStream", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("__codexWallpaperSkinSetCapturedFrame", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("decode-timeout", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("resolve(status)", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("__codexWallpaperSkinGetCapturedPointer", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("pointermove", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("pointerleave", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("--app-color-background-surface: transparent", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("setProperty('--cws-root-alpha', '0')", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("markedAncestor", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("removeProperty(name)", StringComparison.Ordinal));
            True(CdpInjectionService.NativeSurfaceProbeScript.Contains("active.nativeSurface", StringComparison.Ordinal));
            True(CdpInjectionService.NativeSurfaceProbeScript.Contains("cws-active", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("cancelAnimationFrame", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("await waitForMedia", StringComparison.Ordinal));
            True(!CdpInjectionService.BootstrapCoreScript.Contains("setInterval", StringComparison.Ordinal));
            // The bundled GPU surface owns exactly one watchdog interval and must
            // clear it on disposal, so a torn-down runtime leaves no timer behind.
            True(SceneRuntimeSource.Script.Contains("__cwsCreateGpuSurface", StringComparison.Ordinal));
            True(SceneRuntimeSource.Script.Contains("clearInterval", StringComparison.Ordinal));
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
            True(CdpInjectionService.BootstrapScript.Contains("captureStaging", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("HTMLCanvasElement", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("capturePointer.buttons", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("capturePointer.wheel", StringComparison.Ordinal));
        });

        Check("v0.4 status labels fail closed and never borrow a GPU state", () =>
        {
            Equal("GPU dynamic - 60 FPS target", GpuStreamStatusLabel.Describe(GpuStreamStatus.GpuDynamic60));
            Equal("GPU dynamic - 30 FPS fallback", GpuStreamStatusLabel.Describe(GpuStreamStatus.GpuDynamic30Fallback));
            Equal("reduced-frame-rate JPEG compatibility", GpuStreamStatusLabel.Describe(GpuStreamStatus.ReducedFrameRateCompatibility));
            Equal("static/preview fallback", GpuStreamStatusLabel.Describe(GpuStreamStatus.StaticPreviewFallback));
            Equal("recovering", GpuStreamStatusLabel.Describe(GpuStreamStatus.Recovering));
            Equal("unsupported or failed", GpuStreamStatusLabel.Describe(GpuStreamStatus.UnsupportedOrFailed));

            Equal(60, GpuStreamStatusLabel.NormalizeFrameRate(120));
            Equal(60, GpuStreamStatusLabel.NormalizeFrameRate(60));
            Equal(30, GpuStreamStatusLabel.NormalizeFrameRate(45));
            Equal(30, GpuStreamStatusLabel.NormalizeFrameRate(15));

            // An active recovery outranks a GPU claim, and a failed GPU path must
            // never be reported as GPU dynamic just because a compatibility
            // backend is available.
            Equal(GpuStreamStatus.Recovering, GpuStreamStatusLabel.Decide(
                new GpuStreamStatusInput(true, true, 60, true, true)));
            Equal(GpuStreamStatus.GpuDynamic60, GpuStreamStatusLabel.Decide(
                new GpuStreamStatusInput(true, false, 60, false, false)));
            Equal(GpuStreamStatus.GpuDynamic30Fallback, GpuStreamStatusLabel.Decide(
                new GpuStreamStatusInput(true, false, 30, false, false)));
            Equal(GpuStreamStatus.ReducedFrameRateCompatibility, GpuStreamStatusLabel.Decide(
                new GpuStreamStatusInput(false, false, 60, true, true)));
            Equal(GpuStreamStatus.StaticPreviewFallback, GpuStreamStatusLabel.Decide(
                new GpuStreamStatusInput(false, false, 60, false, true)));
            Equal(GpuStreamStatus.UnsupportedOrFailed, GpuStreamStatusLabel.Decide(
                new GpuStreamStatusInput(false, false, 60, false, false)));
            True(GpuStreamStatusLabel.TryParse("GPU dynamic - 60 FPS target", out var parsed)
                && parsed == GpuStreamStatus.GpuDynamic60);
            True(!GpuStreamStatusLabel.TryParse("GPU dynamic - 240 FPS", out var unknown)
                && unknown == GpuStreamStatus.UnsupportedOrFailed);
            True(GpuStreamStatusLabel.IsGpuDynamic(GpuStreamStatus.GpuDynamic60));
            True(!GpuStreamStatusLabel.IsGpuDynamic(GpuStreamStatus.ReducedFrameRateCompatibility));
        });
        Check("v0.4 diagnostics stay bounded and privacy safe", () =>
        {
            var diagnostics = new GpuStreamDiagnostics("streamtoken0123456789", GpuMediaPipeline.TransportName, 1280, 720, 45)
            {
                EncoderMode = "hardware",
                DecoderMode = "hardware"
            };
            Equal(30, diagnostics.RequestedFrameRate);
            for (var index = 0; index < 600; index++)
            {
                diagnostics.CountCaptured();
                diagnostics.CountEncoded(1000 + index);
                diagnostics.CountDeliveredFragment();
                diagnostics.CountPresented();
                diagnostics.RecordLatency(TimeSpan.FromMilliseconds(index % 120));
                diagnostics.ObserveQueueDepth(index % 7);
            }
            diagnostics.CountRejected();
            diagnostics.CountDropped();
            diagnostics.CountStaleFragment();
            diagnostics.CountRecovery(TimeSpan.FromSeconds(2));

            var snapshot = diagnostics.Snapshot(GpuStreamStatus.GpuDynamic60);
            Equal(600L, snapshot.CapturedFrames);
            Equal(600L, snapshot.PresentedFragments);
            Equal(6, snapshot.MaximumQueueDepth);
            Equal(1, snapshot.RecoveryCount);
            True(snapshot.MedianEncodeToPresentMs is >= 0 and <= 120);
            True(snapshot.P95EncodeToPresentMs >= snapshot.MedianEncodeToPresentMs);
            Equal("hardware", snapshot.EncoderMode);
            Equal("cdp-fragment", snapshot.Transport);
            True(snapshot.LastPresentedAt is not null);
            // The latency reservoir is fixed size, so a long run cannot grow the
            // diagnostic footprint without bound.
            for (var index = 0; index < 5000; index++) diagnostics.RecordLatency(TimeSpan.FromMilliseconds(10));

            var description = snapshot.Describe();
            True(description.Contains("stream=streamtoken0123456789", StringComparison.Ordinal));
            True(description.Contains("p95=", StringComparison.Ordinal));
            // Diagnostics must never carry a Windows drive path or personal location.
            True(!description.Contains(@":\", StringComparison.Ordinal));
            True(!snapshot.DegradedCadence);

            var degraded = new GpuStreamDiagnostics("streamtoken0123456789", GpuMediaPipeline.TransportName, 640, 360, 60);
            degraded.CountPresented();
            True(!degraded.Snapshot(GpuStreamStatus.GpuDynamic60).DegradedCadence);
            // A GPU mode that cannot hold 28 presented FPS is degraded and must be
            // diagnosable rather than reported as success.
            True(GpuStreamStatusLabel.IsDegradedCadence(GpuStreamStatus.GpuDynamic60, 10, 20));
            True(GpuStreamStatusLabel.IsDegradedCadence(GpuStreamStatus.GpuDynamic30Fallback, 10, 12));
            True(!GpuStreamStatusLabel.IsDegradedCadence(GpuStreamStatus.GpuDynamic60, 10, 30));
            True(!GpuStreamStatusLabel.IsDegradedCadence(GpuStreamStatus.GpuDynamic60, 3, 10));
            // The compatibility backend is labeled honestly rather than as degraded GPU.
            True(!GpuStreamStatusLabel.IsDegradedCadence(GpuStreamStatus.ReducedFrameRateCompatibility, 60, 5));
        });
        Check("v0.4 fragment batcher keeps order and is bounded", () =>
        {
            var batcher = new GpuStreamBatcher(maximumFragments: 3, maximumBytes: 1024 * 1024, maximumDelay: TimeSpan.FromMilliseconds(50));
            var now = DateTimeOffset.UnixEpoch;
            True(!batcher.ShouldFlush(now));
            batcher.Add(new Mp4Chunk(Mp4ChunkKind.Media, new byte[10], 0, now, 0));
            batcher.Add(new Mp4Chunk(Mp4ChunkKind.Media, new byte[10], 1, now.AddMilliseconds(10), 0));
            True(!batcher.ShouldFlush(now.AddMilliseconds(20)));
            batcher.Add(new Mp4Chunk(Mp4ChunkKind.Media, new byte[10], 2, now.AddMilliseconds(20), 0));
            True(batcher.ShouldFlush(now.AddMilliseconds(20)));
            var batch = batcher.Take(now.AddMilliseconds(20));
            True(batch is not null);
            Equal(0, batch!.FirstSequence);
            Equal(3, batch.Count);
            Equal(30, batch.ByteCount);
            Equal(0, batcher.Count);

            // Age alone must flush, so a 60 FPS stream cannot be held waiting for
            // a fragment count that a static Scene never reaches.
            batcher.Add(new Mp4Chunk(Mp4ChunkKind.Media, new byte[10], 3, now, 0));
            True(!batcher.ShouldFlush(now.AddMilliseconds(49)));
            True(batcher.ShouldFlush(now.AddMilliseconds(50)));

            // A byte ceiling flushes large frames early.
            var byteBatcher = new GpuStreamBatcher(maximumFragments: 32, maximumBytes: 4096, maximumDelay: TimeSpan.FromSeconds(5));
            byteBatcher.Add(new Mp4Chunk(Mp4ChunkKind.Media, new byte[4096], 0, now, 0));
            True(byteBatcher.ShouldFlush(now));
            True(byteBatcher.Take(now)!.Count == 1);
            True(byteBatcher.Take(now) is null);
        });
        Check("v0.4 fMP4 assembler groups boxes into MSE units", () =>
        {
            var init = CreateInitSegment(0x64, 0x00, 0x28);
            var fragment = CreateMediaFragment(4);
            var assembler = new Mp4ChunkAssembler();
            var stamp = DateTimeOffset.UnixEpoch;

            // Media Foundation does not promise one box per Write call, so the
            // stream is delivered in deliberately awkward pieces.
            var chunks = new List<Mp4Chunk>();
            var whole = init.Concat(fragment).ToArray();
            for (var cut = 0; cut < whole.Length; cut++)
            {
                chunks.AddRange(assembler.Append(whole.AsSpan(cut, 1), 0, stamp));
            }
            True(!assembler.HasFailed);
            Equal(2, chunks.Count);
            Equal(Mp4ChunkKind.Init, chunks[0].Kind);
            Equal(Mp4ChunkKind.Media, chunks[1].Kind);
            Equal(0, chunks[0].Sequence);
            Equal(1, chunks[1].Sequence);
            // The init segment must carry every byte before moov, byte for byte.
            Equal(init.Length, chunks[0].Bytes.Length);
            True(chunks[0].Bytes.AsSpan().SequenceEqual(init));
            True(chunks[1].Bytes.AsSpan().SequenceEqual(fragment));

            var report = Mp4StreamInspector.Inspect(whole);
            True(report.IsMseCompatible);
            True(report.HasMovieBox && HasBoxName(report, "moof"));
            Equal(4, report.SampleCount);
            Equal(640, report.Width);
            Equal(360, report.Height);
            True(Mp4StreamInspector.TryReadAvcCodecString(init, out var codec));
            Equal("avc1.640028", codec);
            var avc = Mp4StreamInspector.InspectAvcBitstream(whole);
            True(avc.IsDecodable);
            Equal(1, avc.SequenceParameterSets);
            Equal(1, avc.PictureParameterSets);
            Equal(1, avc.InstantaneousRefreshFrames);
            Equal(4, avc.NalLengthSize);

            // A media data box whose NAL lengths overrun it must be rejected, which
            // is the failure that makes a structurally valid MP4 unplayable.
            var corrupted = CreateBox("mdat", new byte[] { 0, 0, 0, 0xFF, 0x65 }).ToArray();
            True(!Mp4StreamInspector.InspectAvcBitstream(init.Concat(corrupted).ToArray()).IsDecodable);
        });
        Check("v0.4 fMP4 assembler fails closed on corruption", () =>
        {
            var stamp = DateTimeOffset.UnixEpoch;
            var truncated = new Mp4ChunkAssembler();
            var partial = CreateInitSegment(0x64, 0x00, 0x28)[..20];
            True(truncated.Append(partial, 0, stamp).Count == 0);
            True(!truncated.HasFailed);
            True(truncated.PendingBytes > 0);

            var oversized = new Mp4ChunkAssembler();
            var header = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(header, Mp4ChunkAssembler.MaximumBoxBytes + 8u);
            Encoding.ASCII.GetBytes("mdat").CopyTo(header, 4);
            oversized.Append(header, 0, stamp);
            True(oversized.HasFailed);

            var unbounded = new Mp4ChunkAssembler();
            var zeroLength = new byte[8];
            Encoding.ASCII.GetBytes("free").CopyTo(zeroLength, 4);
            unbounded.Append(zeroLength, 0, stamp);
            True(unbounded.HasFailed);

            var duplicatedMdat = new Mp4ChunkAssembler();
            var init = CreateInitSegment(0x64, 0x00, 0x28);
            var moof = CreateBox("moof", CreateBox("traf", new byte[8]));
            var mdat = CreateBox("mdat", new byte[4]);
            var media = moof.Concat(mdat).ToArray();
            duplicatedMdat.Append(init.Concat(media).Concat(moof).ToArray(), 0, stamp);
            // Two moof boxes without an intervening mdat is a corrupt stream.
            duplicatedMdat.Append(moof, 0, stamp);
            True(duplicatedMdat.HasFailed);
            // A failed assembler must never be mistaken for a short one.
            True(duplicatedMdat.Append(media, 0, stamp).Count == 0);
        });
        Check("v0.4 renderer wiring is present and reversible", () =>
        {
            var script = CdpInjectionService.BootstrapScript;
            True(script.Contains("__codexWallpaperSkinBeginGpuStream", StringComparison.Ordinal));
            True(script.Contains("__codexWallpaperSkinPushGpuBatch", StringComparison.Ordinal));
            True(script.Contains("__codexWallpaperSkinGetGpuStreamStatus", StringComparison.Ordinal));
            True(script.Contains("__codexWallpaperSkinEndGpuStream", StringComparison.Ordinal));
            True(script.Contains("__cwsCreateGpuSurface", StringComparison.Ordinal));
            True(script.Contains("cws-gpu-surface-1", StringComparison.Ordinal));
            True(script.Contains("gpuPreviousMedia", StringComparison.Ordinal));
            True(script.Contains("existing.version === 16", StringComparison.Ordinal));
            // Networking stays out of the renderer, and the GPU surface must not
            // be reachable through a socket that the page could be told to open.
            True(!script.Contains("WebSocket", StringComparison.Ordinal));
            True(!script.Contains("XMLHttpRequest", StringComparison.Ordinal));
            True(CdpInjectionService.CleanupScript.Contains("GpuStream", StringComparison.Ordinal));
            True(CdpInjectionService.CleanupVerificationScript
                .Contains("__codexWallpaperSkinBeginGpuStream", StringComparison.Ordinal));
            True(CdpInjectionService.ArtifactProbeScript
                .Contains("__codexWallpaperSkinPushGpuBatch", StringComparison.Ordinal));
        });
        Check("v0.4 encoder options reject unsafe geometries", () =>
        {
            True(!MediaFoundationH264Encoder.TryCreate(
                new GpuEncoderOptions(32, 32, 60, 4_000_000, true, TimeSpan.FromMilliseconds(100)),
                out var tooSmall, out var smallReason));
            True(tooSmall is null && smallReason.Length > 0);
            True(!MediaFoundationH264Encoder.TryCreate(
                new GpuEncoderOptions(1920, 1080, 24, 8_000_000, true, TimeSpan.FromMilliseconds(100)),
                out _, out var rateReason));
            True(rateReason.Contains("30 FPS fallback", StringComparison.Ordinal));

            // The captured-frame gate is shared by both media paths.
            var uniform = new byte[64 * 64 * 4];
            for (var index = 3; index < uniform.Length; index += 4) uniform[index] = 255;
            True(!CapturedFrameQuality.IsAcceptable(uniform, 64, 64));
            var flat = new byte[64 * 64 * 4];
            True(!CapturedFrameQuality.IsAcceptable(flat, 64, 64));
            True(!CapturedFrameQuality.IsAcceptable(uniform, 32, 32));
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

    private static void True(bool value, [CallerArgumentExpression(nameof(value))] string? expression = null)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true: " + (expression ?? "condition"));
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

    private static bool HasBoxName(Mp4StreamReport report, string name) => report.TopLevelBoxes.Contains(name);

    /// <summary>Builds one big-endian MP4 box, optionally containing child boxes.</summary>
    private static byte[] CreateBox(string type, params byte[][] children)
    {
        var payload = children.Length == 0
            ? Array.Empty<byte>()
            : children.SelectMany(child => child).ToArray();
        var box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

    /// <summary>
    /// Builds a synthetic but structurally faithful fragmented MP4 initialisation
    /// segment. Synthetic fixtures are mandatory: a real Workshop project may be
    /// used for local manual validation but is never copied into the repository.
    /// </summary>
    private static byte[] CreateInitSegment(byte profile, byte compatibility, byte level)
    {
        var avcC = CreateBox("avcC", [1, profile, compatibility, level, 0xFF, 0xE0, 0x00]);
        var entry = new byte[78 + avcC.Length];
        entry[7] = 1; // data_reference_index
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(24), 640);
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(26), 360);
        avcC.CopyTo(entry, 78);

        var stsdBody = new byte[8 + 8 + entry.Length];
        BinaryPrimitives.WriteUInt32BigEndian(stsdBody.AsSpan(4), 1); // entry_count
        BinaryPrimitives.WriteUInt32BigEndian(stsdBody.AsSpan(8), (uint)(8 + entry.Length));
        Encoding.ASCII.GetBytes("avc1").CopyTo(stsdBody, 12);
        entry.CopyTo(stsdBody, 16);

        var stsd = CreateBox("stsd", stsdBody);
        var stbl = CreateBox("stbl", stsd);
        var minf = CreateBox("minf", stbl);
        var mdhd = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(mdhd.AsSpan(12), 60000);
        var mdia = CreateBox("mdia", CreateBox("mdhd", mdhd), minf);
        var trak = CreateBox("trak", mdia);
        var mvex = CreateBox("mvex", CreateBox("trex", new byte[24]));
        var ftyp = CreateBox("ftyp", "isomisom"u8.ToArray());
        var moov = CreateBox("moov", mvex, trak);
        return ftyp.Concat(moov).ToArray();
    }

    /// <summary>Builds one synthetic moof+mdat media fragment carrying <paramref name="sampleCount"/> samples.</summary>
    private static byte[] CreateMediaFragment(int sampleCount)
    {
        var trun = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(trun.AsSpan(4), (uint)sampleCount);
        var moof = CreateBox("moof", CreateBox("traf", CreateBox("trun", trun)));
        var payload = new List<byte>();
        for (var sample = 0; sample < sampleCount; sample++)
        {
            // One access unit per sample: parameter sets on the first, then an
            // instantaneous refresh frame followed by non-refresh frames, all
            // length-prefixed with four-byte lengths as avcC declares.
            if (sample == 0)
            {
                payload.AddRange(CreateNalUnit(7, 4));
                payload.AddRange(CreateNalUnit(8, 4));
            }
            payload.AddRange(CreateNalUnit((byte)(sample == 0 ? 5 : 1), 12));
        }
        var mdat = CreateBox("mdat", payload.ToArray());
        return moof.Concat(mdat).ToArray();
    }

    private static byte[] CreateNalUnit(byte nalType, int payloadBytes)
    {
        var unit = new byte[4 + payloadBytes];
        BinaryPrimitives.WriteUInt32BigEndian(unit, (uint)payloadBytes);
        unit[4] = nalType;
        return unit;
    }

    private static byte[] CreatePngHeader(int width, int height) =>    [
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

    private static byte[] CreateScenePackage(string entryName, byte[] payload)
    {
        var name = System.Text.Encoding.UTF8.GetBytes(entryName);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(8u);
        writer.Write("PKGV0022"u8);
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
