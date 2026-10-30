using System.Text.Json;
using RKNNSharp;

namespace RKNNSharp.Smoke;

// 最小采集/检测示例. 复用采集适配器, 每次仅借用一帧至同步推理完成.
internal static class SingleFrameSample
{
    internal const string Help = """
        RKNNSharp.Smoke (Linux ARM64 RK3588 hardware sample)

        Minimal detection:
          MODEL --single-frame (--file MP4|--rtsp-file PRIVATE_URL_FILE) [codec=h264] [colorspace=auto]

        Finite diagnostics (existing modes):
          MODEL (--file MP4|--file-unpaced MP4|--rtsp-file PRIVATE_URL_FILE)
            [frames=5] [codec=h264] [confidence=.25] [colorspace=auto]
            [cycles=1] [warmup=0] [mode=detect] [core=Auto]
            [sessionsPerCore=1] [inFlight=0] [workers=0]
          MODEL --verify-model-hash
          MODEL --contract-compare FIXTURE BASELINE_DLL [iterations=12] [EXPECTED_SHA256]

        codec: h264|h265; colorspace: auto|601limit|601full|709limit
        mode: detect|run|compare|pipeline|replay; core: Auto|Core0|Core1|Core2|All
        See samples/RKNNSharp.Smoke/README.md for ranges, prerequisites and ownership.
        The YOLOv5 official640 example anchors must match your own model.
        --help needs no model, native library or hardware. No camera URL is accepted on the command line.
        Exit codes: 0 success, 1 operation/argument failure, 2 missing arguments.
        """;

    internal static int Run(string[] arguments)
    {
        if (arguments.Length is < 4 or > 6)
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
        // 先验证本地输入; 私有文件内容只交给采集层, 不输出到日志.
        if (!File.Exists(arguments[0]) || !File.Exists(arguments[3]))
        {
            throw new FileNotFoundException("Model or source file is missing.");
        }
        string source = rtsp ? File.ReadAllText(arguments[3]).Trim() : arguments[3];
        if (string.IsNullOrWhiteSpace(source)) { throw new ArgumentException("Source is empty."); }

        var profile = YoloV5Profile.CreateOfficial640Example();
        using var model = new RknnModel(arguments[0]);
        profile.ValidateModel(model.Inputs, model.Outputs);
        using var capture = new GStreamerCapture(source, codec, rtsp);
        GstFrame sample = capture.Pull();
        try
        {
            DmaFrame frame = GStreamerCapture.Borrow(in sample, colorSpace);
            DetectionResult result = model.Detect(frame, profile);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "single-frame", sample.PtsNs, frame.Width, frame.Height,
                result.Detections, result.Timings, result.PostprocessMs
            }));
            return 0;
        }
        finally { GStreamerCapture.Release(ref sample); }
    }
}
