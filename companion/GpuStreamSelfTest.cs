using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CodexWallpaperSkin;

internal sealed record GpuEncoderSmokeOptions(
    int Width = 1280,
    int Height = 720,
    int FrameRate = 60,
    double Seconds = 3,
    string? OutputPath = null,
    double FragmentMilliseconds = 100,
    // Creation-only isolates Media Foundation initialisation from frame
    // submission, which is the difference between "no encoder" and "the encoder
    // pipeline stalls".
    bool CreateOnly = false,
    bool PreferHardware = true,
    bool LowLatency = true,
    bool HintFragmentDuration = true,
    bool AllowUnstableRate = false,
    // Retained bytes are capped so a soak run can measure a long stream without
    // holding the whole thing in memory: the prefix is enough for the structural
    // and decode checks, and the rest is counted and discarded.
    long RetainedBytes = 8L * 1024 * 1024);

internal sealed record GpuEncoderSmokeResult(
    bool Passed,
    string Summary,
    IReadOnlyList<string> Details);

/// <summary>
/// Verifies the v0.4 GPU media path without a GPU, a wallpaper or a browser:
/// synthetic BGRA frames are encoded by Media Foundation into a fragmented MP4,
/// the container is inspected structurally, and the result is optionally written
/// to a caller-chosen path so a browser test can attempt a real decode.
///
/// The fixture is generated in memory from a deterministic formula. No Workshop
/// asset, screenshot or user media is involved, so the test is redistributable.
///
/// Two Media Foundation hazards are handled explicitly. Media Foundation objects
/// are free-threaded but have no proxy/stub, so every call happens on one
/// thread-pool (multi-threaded apartment) thread. And a hardware encoder that
/// cannot be configured accepts no input at all, so frame submission runs on a
/// supervised thread that is abandoned rather than allowed to hang the process.
/// </summary>
internal static class GpuStreamSelfTest
{
    private const int CoInitMultithreaded = 0x0;
    private const int RpcChangedMode = unchecked((int)0x80010106);

    public static Task<GpuEncoderSmokeResult> RunEncoderSmokeTestAsync(
        GpuEncoderSmokeOptions options,
        CancellationToken cancellationToken,
        Action<string>? progress = null) =>
        Task.Run(() => RunCore(options, cancellationToken, progress), CancellationToken.None);

    private static GpuEncoderSmokeResult RunCore(
        GpuEncoderSmokeOptions options,
        CancellationToken cancellationToken,
        Action<string>? progress)
    {
        var details = new List<string>();
        void Stage(string message)
        {
            details.Add(message);
            progress?.Invoke(message);
        }

        if (options.Width < 64 || options.Height < 64)
        {
            return new GpuEncoderSmokeResult(false, "The smoke test size is below the supported minimum.", details);
        }
        if (options.FrameRate is not (30 or 60))
        {
            return new GpuEncoderSmokeResult(false, "The smoke test frame rate must be 30 or 60.", details);
        }
        if (options.Seconds is < 0.5 or > 900)
        {
            return new GpuEncoderSmokeResult(false, "The smoke test duration must be between 0.5 and 900 seconds.", details);
        }

        var comResult = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
        if (comResult < 0 && comResult != RpcChangedMode)
        {
            return new GpuEncoderSmokeResult(false, $"COM could not be initialised for Media Foundation (0x{comResult:X8}).", details);
        }
        if (!MediaFoundationInterop.TryStartup(out var startupFailure))
        {
            return new GpuEncoderSmokeResult(false, startupFailure, details);
        }

        // Media Foundation is deliberately never shut down here: the process exits
        // immediately afterwards, and MFShutdown can block while a hardware
        // encoder's asynchronous workers retire.
        var bitrate = Math.Clamp(
            (int)(options.Width * (long)options.Height * options.FrameRate * 0.10),
            2_000_000,
            40_000_000);
        var encoderOptions = new GpuEncoderOptions(
            options.Width,
            options.Height,
            options.FrameRate,
            bitrate,
            TopDownRows: true,
            MinimumFragmentDuration: TimeSpan.FromMilliseconds(options.FragmentMilliseconds),
            PreferHardware: options.PreferHardware,
            LowLatency: options.LowLatency,
            HintFragmentDuration: options.HintFragmentDuration,
            AllowUnstableRate: options.AllowUnstableRate);
        Stage($"COM initialised (0x{comResult:X8}); Media Foundation {MediaFoundationInterop.MfVersion:X8} started.");
        Stage($"encoder request: {options.Width}x{options.Height}@{options.FrameRate} bitrate={bitrate} "
            + $"fragment={options.FragmentMilliseconds}ms seconds={options.Seconds} "
            + $"hardware={options.PreferHardware} lowLatency={options.LowLatency} fragmentHint={options.HintFragmentDuration}");

        if (!MediaFoundationH264Encoder.TryCreate(encoderOptions, out var created, out var createFailure)
            || created is null)
        {
            // A refused hardware configuration is not the end of the story: the
            // software encoder was measured stable at the same resolution and rate,
            // so it is tried before the run is reported as a failure. The refusal is
            // recorded either way, because it is the reason the mode is not hardware.
            if (options.PreferHardware && !options.AllowUnstableRate)
            {
                Stage("hardware encoder refused: " + createFailure);
                var fallbackOptions = encoderOptions with { PreferHardware = false, AllowUnstableRate = true };
                if (MediaFoundationH264Encoder.TryCreate(fallbackOptions, out created, out var fallbackFailure)
                    && created is not null)
                {
                    Stage("retried with the software encoder, which was measured stable for this configuration");
                    encoderOptions = fallbackOptions;
                }
                else
                {
                    return new GpuEncoderSmokeResult(
                        false,
                        "The Media Foundation H.264 encoder could not be created with hardware or software: "
                        + fallbackFailure,
                        details);
                }
            }
            else
            {
                return new GpuEncoderSmokeResult(
                    false, "The Media Foundation H.264 encoder could not be created: " + createFailure, details);
            }
        }

        var stalled = false;
        CancellationTokenSource? drainCancellation = null;
        try
        {
            Stage($"encoder created and started writing; encoder mode: {created.EncoderMode}");
            if (options.CreateOnly)
            {
                created.Finish();
                Thread.Sleep(300);
                var drainedChunks = 0;
                while (created.TryTakeChunk(out _, 200))
                {
                    drainedChunks++;
                }
                return new GpuEncoderSmokeResult(
                    created.FailureReason is null,
                    created.FailureReason is null
                        ? $"PASS GPU encoder creation ({created.EncoderMode}, {options.Width}x{options.Height}@{options.FrameRate}, {drainedChunks} chunk(s))"
                        : "FAIL GPU encoder creation: " + created.FailureReason,
                    details);
            }

            var chunks = new List<Mp4Chunk>();
            var fragmentArrival = new List<double>();
            var retainedBytes = 0L;
            var retentionClosed = false;
            var mediaChunks = 0;
            byte[]? initChunk = null;
            long totalStreamBytes = 0;
            drainCancellation = new CancellationTokenSource();
            var drainClock = Stopwatch.StartNew();
            var drainToken = drainCancellation.Token;
            var drain = Task.Run(() =>
            {
                void Collect(Mp4Chunk chunk)
                {
                    totalStreamBytes += chunk.Bytes.Length;
                    if (chunk.Kind == Mp4ChunkKind.Init)
                    {
                        initChunk = chunk.Bytes;
                    }
                    else
                    {
                        mediaChunks++;
                    }
                    // Keep only a bounded, contiguous prefix, so a soak run cannot
                    // grow the test's own memory and invalidate what it measures.
                    // Retention stops for good once the cap is reached: resuming for
                    // a later, smaller chunk would leave a sequence gap and make the
                    // ordering check report a defect that does not exist.
                    if (!retentionClosed && retainedBytes + chunk.Bytes.Length <= options.RetainedBytes)
                    {
                        retainedBytes += chunk.Bytes.Length;
                        chunks.Add(chunk);
                    }
                    else
                    {
                        retentionClosed = true;
                    }
                    fragmentArrival.Add(drainClock.Elapsed.TotalMilliseconds);
                }

                while (!drainToken.IsCancellationRequested)
                {
                    if (created.TryTakeChunk(out var chunk, 100) && chunk is not null)
                    {
                        lock (chunks)
                        {
                            Collect(chunk);
                        }
                    }
                    else if (created.HasFailed)
                    {
                        return;
                    }
                }
                while (created.TryTakeChunk(out var tail, 150) && tail is not null)
                {
                    lock (chunks)
                    {
                        Collect(tail);
                    }
                }
            }, CancellationToken.None);

            var workingSetBefore = Environment.WorkingSet;
            var frameBytes = checked(options.Width * options.Height * 4);
            var baseFrame = new byte[frameBytes];
            var frame = new byte[frameBytes];
            FillStaticPattern(baseFrame, options.Width, options.Height);
            Array.Copy(baseFrame, frame, frameBytes);

            var totalFrames = (int)Math.Round(options.Seconds * options.FrameRate);
            var blockSize = Math.Max(16, Math.Min(96, options.Width / 12));
            var submitted = 0;
            var submitFailure = string.Empty;
            var submittedAt = Stopwatch.GetTimestamp();
            var submissionElapsed = TimeSpan.Zero;
            var submitDone = new ManualResetEventSlim(false);
            var submitThread = new Thread(() =>
            {
                try
                {
                    submittedAt = Stopwatch.GetTimestamp();
                    var previousBlockX = -1;
                    var previousBlockY = -1;
                    for (var index = 0; index < totalFrames; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var target = TimeSpan.FromSeconds(index / (double)options.FrameRate);
                        while (Stopwatch.GetElapsedTime(submittedAt) < target)
                        {
                            var remaining = target - Stopwatch.GetElapsedTime(submittedAt);
                            if (remaining.TotalMilliseconds > 2)
                            {
                                Thread.Sleep(1);
                            }
                            else
                            {
                                Thread.SpinWait(200);
                            }
                        }

                        DrawMovingBlock(frame, baseFrame, options.Width, options.Height, blockSize, index,
                            totalFrames, ref previousBlockX, ref previousBlockY);
                        if (!created.TrySubmitFrame(frame, DateTimeOffset.UtcNow, out submitFailure))
                        {
                            break;
                        }
                        submitted++;
                    }
                }
                catch (Exception exception)
                {
                    submitFailure = exception.Message;
                }
                finally
                {
                    submissionElapsed = Stopwatch.GetElapsedTime(submittedAt);
                    submitDone.Set();
                }
            })
            {
                IsBackground = true,
                Name = "cws-gpu-encoder-smoke-submit"
            };
            submitThread.Start();

            var submissionBudget = TimeSpan.FromSeconds(options.Seconds + 20);
            if (!submitDone.Wait(submissionBudget))
            {
                stalled = true;
                Stage($"the encoder stopped consuming input after {Volatile.Read(ref submitted)} frame(s); "
                    + $"the submit thread did not finish within {submissionBudget.TotalSeconds:F0}s");
                Stage(created.StreamDiagnostics);
                return new GpuEncoderSmokeResult(
                    false,
                    "FAIL GPU H.264 encoder: Media Foundation stopped accepting frames. This is the signature of a "
                    + "hardware encoder that could not be configured for the requested input type.",
                    details);
            }
            if (submitFailure.Length > 0)
            {
                Stage("submission stopped: " + submitFailure);
            }

            var elapsed = submissionElapsed;
            Stage($"frame submission finished: {submitted}/{totalFrames} in {elapsed.TotalSeconds:F2}s; finalising");
            created.Finish();
            Thread.Sleep(400);
            drainCancellation.Cancel();
            drain.Wait(TimeSpan.FromSeconds(5));
            var workingSetAfter = Environment.WorkingSet;
            Stage($"drained {totalStreamBytes} bytes in {mediaChunks + (initChunk is null ? 0 : 1)} chunk(s); "
                + $"working set {(workingSetBefore / 1048576d):F1} MiB -> {(workingSetAfter / 1048576d):F1} MiB");

            // The retained prefix is what the structural and decode checks can see;
            // a soak run deliberately keeps only a bounded part of the stream.
            var stream = new byte[chunks.Sum(chunk => chunk.Bytes.Length)];
            var offset = 0;
            foreach (var chunk in chunks)
            {
                chunk.Bytes.CopyTo(stream, offset);
                offset += chunk.Bytes.Length;
            }

            var initChunks = chunks.Count(chunk => chunk.Kind == Mp4ChunkKind.Init);
            var initIsFirst = chunks.Count > 0 && chunks[0].Kind == Mp4ChunkKind.Init;
            var ordered = IsOrdered(chunks);

            var report = Mp4StreamInspector.Inspect(stream);
            var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            var retainedFraction = totalStreamBytes > 0 ? (double)stream.Length / totalStreamBytes : 0;
            Stage($"chunks: init={initChunks} media={mediaChunks} initFirst={initIsFirst} ordered={ordered} "
                + $"streamBytes={totalStreamBytes} retained={stream.Length} ({retainedFraction:P1}) sha256={digest}");
            if (fragmentArrival.Count > 1)
            {
                var span = fragmentArrival[^1] - fragmentArrival[0];
                Stage($"fragment cadence: {span / (fragmentArrival.Count - 1):F1} ms average over "
                    + $"{fragmentArrival.Count} chunks (requested {options.FragmentMilliseconds:F0} ms)");
            }
            Stage(report.Describe());
            var avc = Mp4StreamInspector.InspectAvcBitstream(stream);
            Stage(avc.Describe());
            var decoded = MediaFoundationDecodeCheck.TryDecodeInMemory(
                stream, options.Width, options.Height, orientationExpected: true, out var decodeSummary, out var decodeFailure);
            Stage(decoded ? decodeSummary : "decode check: " + decodeFailure);
            Stage(created.StreamDiagnostics);
            var sustained = submitted / Math.Max(0.001, elapsed.TotalSeconds);
            Stage($"sustained input {sustained:F1} fps of a requested {options.FrameRate} fps at "
                + $"{options.Width}x{options.Height} for {elapsed.TotalSeconds:F1}s");

            if (!string.IsNullOrWhiteSpace(options.OutputPath))
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath));
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllBytes(options.OutputPath, stream);
                Stage($"wrote {stream.Length} bytes for the browser decode check");
            }

            var passed = created.FailureReason is null
                && submitted >= totalFrames - 2
                && initChunks == 1
                && mediaChunks >= 2
                && initIsFirst
                && ordered
                && report.IsMseCompatible
                && report.Width == options.Width
                && report.Height == options.Height
                && report.SampleCount > 0
                && avc.IsDecodable
                && decoded
                && sustained >= options.FrameRate - 2;
            var summary = passed
                ? $"PASS GPU H.264 encoder ({created.EncoderMode}, {options.Width}x{options.Height}@{options.FrameRate}, "
                    + $"{submitted} frames in {elapsed.TotalSeconds:F1}s = {sustained:F1} fps sustained, "
                    + $"{mediaChunks} fragments, {totalStreamBytes} bytes, "
                    + $"{avc.NalUnits} AVC NAL units, sps={avc.SequenceParameterSets} pps={avc.PictureParameterSets} idr={avc.InstantaneousRefreshFrames}, "
                    + $"decode verified with orientation)"
                : "FAIL GPU H.264 encoder: " + DescribeFailure(created, submitted, totalFrames, initChunks, mediaChunks, report, avc, decodeFailure);
            return new GpuEncoderSmokeResult(passed, summary, details);
        }
        finally
        {
            drainCancellation?.Cancel();
            if (!stalled)
            {
                // Disposing a stalled pipeline would block on the same
                // non-returning call, so the supervised thread is abandoned and
                // the process exits without it.
                created.Dispose();
            }
        }
    }

    private static string DescribeFailure(
        MediaFoundationH264Encoder encoder,
        int submitted,
        int totalFrames,
        int initChunks,
        int mediaChunks,
        Mp4StreamReport report,
        AvcBitstreamReport avc,
        string decodeFailure)
    {
        var builder = new StringBuilder();
        if (decodeFailure.Length > 0)
        {
            builder.Append("decode=").Append(decodeFailure).Append("; ");
        }
        if (encoder.FailureReason is not null)
        {
            builder.Append("encoder=").Append(encoder.FailureReason).Append("; ");
        }
        if (submitted < totalFrames - 2)
        {
            builder.Append($"accepted {submitted}/{totalFrames} frames; ");
        }
        if (initChunks != 1)
        {
            builder.Append($"init segments={initChunks} (expected 1, so the fragmented MP4 is not streamable from the first byte); ");
        }
        if (mediaChunks < 2)
        {
            builder.Append($"media fragments={mediaChunks} (expected at least 2); ");
        }
        if (!report.IsMseCompatible)
        {
            builder.Append("container is not Media Source Extensions compatible; ");
        }
        if (!report.HasMovieExtendsBox)
        {
            builder.Append("the moov box has no mvex box, so Media Source Extensions cannot append fragments; ");
        }
        if (!report.HasAvcConfiguration)
        {
            builder.Append("no avcC decoder configuration was found; ");
        }
        if (report.SampleCount <= 0)
        {
            builder.Append("no samples were muxed; ");
        }
        if (!avc.IsDecodable)
        {
            builder.Append("the AVC elementary stream failed validation: ").Append(avc.Describe()).Append("; ");
        }
        // Conditions that are not covered by a counter or a report must still be
        // named, otherwise a failing run reports "unspecified" and cannot be diagnosed.
        if (builder.Length == 0)
        {
            builder.Append("one of the ordering, geometry or cadence checks failed while every counter looked healthy: ")
                .Append($"initFirst={initChunks == 1}, mediaChunks={mediaChunks}, ")
                .Append($"reported={report.Width}x{report.Height}, samples={report.SampleCount}, ")
                .Append($"mseCompatible={report.IsMseCompatible}, avcDecodable={avc.IsDecodable}");
        }
        return builder.Length == 0 ? "unspecified encoder failure." : builder.ToString();
    }

    /// <summary>True when the retained fragment prefix is contiguous from sequence zero.</summary>
    private static bool IsOrdered(IReadOnlyList<Mp4Chunk> chunks)
    {
        for (var index = 0; index < chunks.Count; index++)
        {
            if (chunks[index].Sequence != index)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Deterministic, redistributable test pattern with a high-contrast top-left marker.</summary>
    private static void FillStaticPattern(byte[] frame, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var offset = row + (x * 4);
                frame[offset] = (byte)((x * 255) / Math.Max(1, width - 1));
                frame[offset + 1] = (byte)((y * 255) / Math.Max(1, height - 1));
                frame[offset + 2] = (byte)(((x / 32) + (y / 32)) % 2 == 0 ? 200 : 60);
                frame[offset + 3] = 255;
            }
        }
        var marker = Math.Min(48, Math.Min(width, height));
        for (var y = 0; y < marker; y++)
        {
            var row = y * width * 4;
            for (var x = 0; x < marker; x++)
            {
                var offset = row + (x * 4);
                frame[offset] = 0;
                frame[offset + 1] = 0;
                frame[offset + 2] = 255;
                frame[offset + 3] = 255;
            }
        }
    }

    /// <summary>Moves a solid block so the encoder has real inter-frame motion to code.</summary>
    private static void DrawMovingBlock(
        byte[] frame,
        byte[] baseFrame,
        int width,
        int height,
        int blockSize,
        int index,
        int totalFrames,
        ref int previousX,
        ref int previousY)
    {
        var rowBytes = width * 4;
        if (previousX >= 0)
        {
            for (var row = 0; row < blockSize; row++)
            {
                var now = ((previousY + row) * rowBytes) + (previousX * 4);
                Array.Copy(baseFrame, now, frame, now, blockSize * 4);
            }
        }
        var travel = Math.Max(1, width - blockSize);
        var x = (int)((long)index * travel / Math.Max(1, totalFrames - 1));
        var blockY = (height / 2) - (blockSize / 2);
        for (var row = 0; row < blockSize; row++)
        {
            var offset = ((blockY + row) * rowBytes) + (x * 4);
            for (var column = 0; column < blockSize; column++)
            {
                frame[offset + (column * 4)] = 32;
                frame[offset + (column * 4) + 1] = 255;
                frame[offset + (column * 4) + 2] = 64;
                frame[offset + (column * 4) + 3] = 255;
            }
        }
        previousX = x;
        previousY = blockY;
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, int concurrencyModel);
}
