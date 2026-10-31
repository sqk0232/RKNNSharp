using System.Diagnostics;
using System.Text.Json;
using RKNNSharp;

namespace RKNNSharp.Smoke;

// 最小采集/检测示例. 复用采集适配器, 每次仅借用一帧至同步推理完成.
internal static class SingleFrameSample
{
    internal const string Help = """
        RKNNSharp.Smoke (Linux ARM64 RK3588 hardware sample)

        Minimal detection:
          MODEL --single-frame (--file MP4|--rtsp-file PRIVATE_URL_FILE) [codec=h264] [colorspace=auto] [decoder=v5|dfl]

        Finite diagnostics (existing modes):
          MODEL (--file MP4|--file-unpaced MP4|--rtsp-file PRIVATE_URL_FILE)
            [frames=5] [codec=h264] [confidence=.25] [colorspace=auto]
            [cycles=1] [warmup=0] [mode=detect] [core=Auto]
            [sessionsPerCore=1] [inFlight=0] [workers=0]
          MODEL --verify-model-hash
          MODEL --contract-compare FIXTURE BASELINE_DLL [iterations=12] [EXPECTED_SHA256]

        codec: h264|h265; colorspace: auto|601limit|601full|709limit
        decoder: v5 (default) uses YOLOv5 Detect; dfl uses the explicit 640/80 nine-output RunInto path.
        To select dfl, pass codec and colorspace before it. Neither decoder infers a model from its file name.
        mode: detect|run|compare|pipeline|replay; core: Auto|Core0|Core1|Core2|All
        See samples/RKNNSharp.Smoke/README.md for ranges, prerequisites and ownership.
        The YOLOv5 official640 anchors and the dfl head map are explicit assumptions and must match your model.
        --help needs no model, native library or hardware. No camera URL is accepted on the command line.
        Exit codes: 0 success, 1 operation/argument failure, 2 missing arguments.
        """;

    internal static int Run(string[] arguments)
    {
        SingleFrameOptions options = Parse(arguments);
        // 先验证本地输入; 私有文件内容只交给采集层, 不输出到日志.
        if (!File.Exists(arguments[0]) || !File.Exists(arguments[3]))
        {
            throw new FileNotFoundException("Model or source file is missing.");
        }
        string source = options.Rtsp ? File.ReadAllText(arguments[3]).Trim() : arguments[3];
        if (string.IsNullOrWhiteSpace(source)) { throw new ArgumentException("Source is empty."); }

        using var model = new RknnModel(arguments[0]);
        if (options.Decoder == SingleFrameDecoder.Dfl)
        {
            YoloDetectionProfile profile = CreateExplicitDfl640Example();
            profile.ValidateModel(model.Inputs, model.Outputs);
            return DetectDfl(model, profile, source, options);
        }

        var yolov5 = YoloV5Profile.CreateOfficial640Example();
        yolov5.ValidateModel(model.Inputs, model.Outputs);
        return DetectV5(model, yolov5, source, options);
    }

    /// <summary>文档中的 640×640、80 类、regMax 16、含 score-sum 的九输出示例. 不按文件名选择.</summary>
    internal static YoloDetectionProfile CreateExplicitDfl640Example() => new(new YoloDetectionConfig(
        640, 640, 80, YoloDetectionOutputFormat.RknnDfl,
        [new(0, 1, 2, 8), new(3, 4, 5, 16), new(6, 7, 8, 32)]));

    private static int DetectV5(RknnModel model, YoloV5Profile profile, string source, SingleFrameOptions options)
    {
        using var capture = new GStreamerCapture(source, options.Codec, options.Rtsp);
        GstFrame sample = capture.Pull();
        try
        {
            DmaFrame frame = GStreamerCapture.Borrow(in sample, options.ColorSpace);
            DetectionResult result = model.Detect(frame, profile);
            WriteResult(sample, frame, "v5", result.Detections, result.Timings, result.PostprocessMs);
            return 0;
        }
        finally { GStreamerCapture.Release(ref sample); }
    }

    private static int DetectDfl(RknnModel model, YoloDetectionProfile profile, string source, SingleFrameOptions options)
    {
        using var capture = new GStreamerCapture(source, options.Codec, options.Rtsp);
        GstFrame sample = capture.Pull();
        try
        {
            DmaFrame frame = GStreamerCapture.Borrow(in sample, options.ColorSpace);
            var buffers = new Memory<float>[model.Outputs.Count];
            for (int i = 0; i < buffers.Length; i++)
            {
                uint elements = model.Outputs[i].ElementCount;
                if (elements == 0 || elements > int.MaxValue) { throw new ArgumentException("Output tensor does not fit a managed float buffer."); }
                buffers[i] = new float[(int)elements].AsMemory();
            }
            InferenceResult raw = model.RunInto(frame, buffers);
            long postprocessStart = Stopwatch.GetTimestamp();
            IReadOnlyList<Detection> detections = profile.Decode(raw.Outputs, raw.Transform);
            double postprocessMs = Stopwatch.GetElapsedTime(postprocessStart).TotalMilliseconds;
            WriteResult(sample, frame, "dfl", detections, raw.Timings, postprocessMs);
            return 0;
        }
        finally { GStreamerCapture.Release(ref sample); }
    }

    private static void WriteResult(GstFrame sample, DmaFrame frame, string decoder, IReadOnlyList<Detection> detections, RunTimings timings, double postprocessMs)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "single-frame",
            decoder,
            sample.PtsNs,
            frame.Width,
            frame.Height,
            Detections = detections,
            Timings = timings,
            PostprocessMs = postprocessMs
        }));
    }

    private static SingleFrameOptions Parse(string[] arguments)
    {
        if (arguments.Length is < 4 or > 7)
        {
            throw new ArgumentException("Single-frame mode requires model, source mode and source file; see --help.");
        }

        bool rtsp = arguments[2] switch
        {
            "--file" => false,
            "--rtsp-file" => true,
            _ => throw new ArgumentException("Unknown single-frame source mode.")
        };
        string codec = arguments.Length > 4 ? arguments[4] : "h264";
        if (codec is not ("h264" or "h265")) { throw new ArgumentException("Unsupported codec."); }
        ColorSpace? colorSpace = (arguments.Length > 5 ? arguments[5] : "auto") switch
        {
            "auto" => null,
            "601limit" => ColorSpace.Bt601Limited,
            "601full" => ColorSpace.Bt601Full,
            "709limit" => ColorSpace.Bt709Limited,
            _ => throw new ArgumentException("Unsupported color space.")
        };
        SingleFrameDecoder decoder = SingleFrameDecoder.V5;
        if (arguments.Length > 6)
        {
            decoder = arguments[6] switch
            {
                "v5" => SingleFrameDecoder.V5,
                "dfl" => SingleFrameDecoder.Dfl,
                _ => throw new ArgumentException("Unsupported single-frame decoder. Use v5 or dfl.")
            };
        }
        return new SingleFrameOptions(rtsp, codec, colorSpace, decoder);
    }

    private enum SingleFrameDecoder { V5, Dfl }

    private readonly record struct SingleFrameOptions(bool Rtsp, string Codec, ColorSpace? ColorSpace, SingleFrameDecoder Decoder);
}
