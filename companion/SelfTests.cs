using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.WebSockets;

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
        });
        Check("visual preset normalization", () =>
        {
            var preset = new VisualPresetSettings
            {
                Opacity = 2,
                BlackOverlay = -1,
                Brightness = 4,
                Contrast = 0,
                Saturation = 5,
                PanelOpacity = .1,
                Blur = 80,
                SceneResolutionScale = .1
            }.Normalize();
            Equal(1d, preset.Opacity);
            Equal(0d, preset.BlackOverlay);
            Equal(1.5d, preset.Brightness);
            Equal(.5d, preset.Contrast);
            Equal(2d, preset.Saturation);
            Equal(.2d, preset.PanelOpacity);
            Equal(30d, preset.Blur);
            Equal(.5d, preset.SceneResolutionScale);
            Equal("--launch-remembered-wallpaper", LegacyDesktopLauncherCleanup.LegacyArgument);
        });
        Check("named visual preset library migration", () =>
        {
            var legacy = new AppState
            {
                SchemaVersion = 7,
                VisualPreset = new VisualPresetSettings
                {
                    Brightness = 1.23,
                    Contrast = 1.08,
                    Saturation = 1.17
                },
                VisualPresets = []
            };
            StateStore.MigrateState(legacy);
            StateStore.NormalizeState(legacy);
            Equal(AppState.CurrentSchema, legacy.SchemaVersion);
            Equal(1, legacy.VisualPresets.Count);
            Equal("Brighter high-clarity", legacy.VisualPresets[0].Name);
            Equal(1.23d, legacy.VisualPresets[0].Settings.Brightness);
            Equal(VisualPresetProfile.DefaultId, legacy.SelectedVisualPresetId);
            True(legacy.VisualPreset is null);

            legacy.VisualPresets.Add(new VisualPresetProfile
            {
                Name = "Dark wallpaper",
                Settings = new VisualPresetSettings { Brightness = .88, Saturation = .92 }
            });
            StateStore.ValidateStateForSave(legacy);

            legacy.VisualPresets[1].Id = legacy.VisualPresets[0].Id.ToUpperInvariant();
            Throws<InvalidDataException>(() => StateStore.ValidateStateForSave(legacy));
        });
        Check("Steam workshop scan intersects subscribed and downloaded items", () =>
        {
            const string manifest = """
                "AppWorkshop"
                {
                    "appid" "431960"
                    "WorkshopItemsInstalled"
                    {
                        "3596725214" { "size" "968600" }
                        "3718972568" { "size" "3426784" }
                    }
                    "WorkshopItemDetails"
                    {
                        "3718972568" { "manifest" "3257423810985589306" }
                        "9999999999" { "manifest" "not-downloaded" }
                    }
                }
                """;
            var eligible = SteamWorkshopManifest.ParseDownloadedSubscriptions(manifest);
            Equal(1, eligible.Count);
            True(eligible.Contains("3718972568"));
            True(!eligible.Contains("3596725214"));
            True(!eligible.Contains("9999999999"));
        });
        Check("large Wallpaper Engine videos use bounded native capture", () =>
        {
            True(!WallpaperCatalog.ShouldUseNativeVideoCapture(WallpaperCatalog.MaximumVideoBytes));
            True(WallpaperCatalog.ShouldUseNativeVideoCapture(WallpaperCatalog.MaximumVideoBytes + 1));
            True(WallpaperCatalog.ShouldUseNativeVideoCapture(379_737_294));
            True(!WallpaperCatalog.ShouldUseNativeVideoCapture(WallpaperCatalog.MaximumNativeVideoBytes + 1));
            var entry = new WallpaperEntry
            {
                Source = "Wallpaper Engine",
                ProjectPath = "C:\\workshop\\3507712876\\project.json",
                MediaPath = "C:\\workshop\\3507712876\\wallpaper.mp4",
                Kind = WallpaperKind.Video,
                Support = WallpaperSupport.Direct,
                PreferNativeCapture = true
            };
            True(entry.UsesWallpaperEngineCapture);
            True(entry.CanApply);
            True(entry.DisplayLabel.Contains("WE NATIVE VIDEO", StringComparison.Ordinal));
        });
        Check("BGRA to NV12 conversion is bounded and deterministic", () =>
        {
            var black = new byte[4 * 4 * 4];
            for (var index = 3; index < black.Length; index += 4) black[index] = 255;
            var nv12 = MediaFoundationH264Encoder.ConvertBgraToNv12(black, 4, 4, 16);
            Equal(24, nv12.Length);
            True(nv12.Take(16).All(value => value == 16));
            True(nv12.Skip(16).All(value => value == 128));

            var white = Enumerable.Repeat((byte)255, 4 * 4 * 4).ToArray();
            var whiteNv12 = MediaFoundationH264Encoder.ConvertBgraToNv12(white, 4, 4, 16);
            True(whiteNv12.Take(16).All(value => value == 235));
            True(whiteNv12.Skip(16).All(value => value == 128));
        });
        Check("bounded loopback binary media protocol", () =>
            TestLoopbackMediaProtocolAsync().GetAwaiter().GetResult());
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
        Check("native capture sizing preserves the Codex viewport aspect ratio", () =>
        {
            var fullHd = WallpaperEngineCaptureSession.CalculateCaptureSize(3840, 2160, 1);
            Equal(2560, fullHd.Width);
            Equal(1440, fullHd.Height);
            True(Math.Abs((double)fullHd.Width / fullHd.Height - 16d / 9d) < 0.001);

            var halfScale = WallpaperEngineCaptureSession.CalculateCaptureSize(3840, 2160, 0.5);
            Equal(1920, halfScale.Width);
            Equal(1080, halfScale.Height);

            var sixteenTen = WallpaperEngineCaptureSession.CalculateCaptureSize(2560, 1600, 1);
            Equal(2560, sixteenTen.Width);
            Equal(1600, sixteenTen.Height);
            True(Math.Abs((double)sixteenTen.Width / sixteenTen.Height - 1.6) < 0.001);
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
            True(CdpInjectionService.BootstrapScript.Contains("existing.version === 17", StringComparison.Ordinal));
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
            True(CdpInjectionService.BootstrapScript.Contains("captureStaging", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("HTMLCanvasElement", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("capturePointer.buttons", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("capturePointer.wheel", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("__codexWallpaperSkinBeginLoopbackStream", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("__codexWallpaperSkinBeginH264Stream", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("__codexWallpaperSkinPushH264Batch", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("new VideoDecoder", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("new EncodedVideoChunk", StringComparison.Ordinal));
            True(CdpInjectionService.BootstrapScript.Contains("createImageBitmap", StringComparison.Ordinal));
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

    private static async Task TestLoopbackMediaProtocolAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var server = LoopbackMediaStreamServer.Start();
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        await socket.ConnectAsync(server.Endpoint, timeout.Token);
        await server.WaitForConnectionAsync(timeout.Token);

        byte[] payload = [0xFF, 0xD8, 0x01, 0x02, 0xFF, 0xD9];
        var publish = server.PublishAsync(
            LoopbackMediaPacketKind.Jpeg,
            payload,
            waitForPresentation: true,
            timeout.Token);
        using var received = new MemoryStream();
        var buffer = new byte[128];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, timeout.Token);
            received.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        var packet = received.ToArray();
        True(result.MessageType == WebSocketMessageType.Binary);
        True(packet.Length == payload.Length + 24);
        True(packet.AsSpan(0, 4).SequenceEqual("CWS4"u8));
        Equal((byte)LoopbackMediaPacketKind.Jpeg, packet[4]);
        True(packet.AsSpan(24).SequenceEqual(payload));
        var sequence = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(8, 8));
        var acknowledgement = System.Text.Encoding.UTF8.GetBytes($"presented:{sequence}");
        await socket.SendAsync(acknowledgement, WebSocketMessageType.Text, true, timeout.Token);
        Equal(sequence, await publish);
        var metrics = server.GetMetrics();
        Equal(1L, metrics.Sent);
        Equal(1L, metrics.Presented);
        Equal(sequence, metrics.LastPresentedSequence);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test-complete", timeout.Token);
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
