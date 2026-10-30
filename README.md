# RKNNSharp

面向 **Linux ARM64 / RK3588** 的 C# RKNN 推理库。接收采集层已就绪的 DMA 帧，通过 RGA 预处理、NPU 推理，返回 tensor 或检测结果。核心托管库不依赖 GStreamer、OpenCV、Python 或 PyTorch；仓库提供独立的 GStreamer/MPP 采集样例。

硬件路径为 `VPU/MPP → NV12 DMA → RGA resize/CSC/INT8 → NPU → CPU tensor 后处理`。RGA 存在必要的硬件拷贝，不宣称全链路无拷贝。绘框在独占 DMA 图像上进行，不修改解码器参考帧；不提供 CPU 图像处理或软件解码回退。

## 环境与范围

| 项目 | 要求 |
| --- | --- |
| 构建主机 | Windows 或 Linux，.NET SDK **10.0.403**（`global.json` 允许同 feature band 的更新补丁） |
| 托管代码 | `net10.0` / C# 14；核心库无第三方托管包依赖 |
| 运行目标 | Linux ARM64 RK3588、兼容 BSP、NPU/RGA3、DMA32 设备及访问权限 |
| 已记录的硬件组合 | Runtime 1.5.2 的早期 DMA/媒体链路；Runtime 2.3.2 的 Run/RunInto、YOLOv5 和指定 YOLOv8n；RKNPU 0.9.8 / RGA 1.10.1，详见[硬件约束](docs/hardware.md) |
| 采集样例额外依赖 | GStreamer ≥1.20、BSP 的 `mppvideodec`、原生采集 adapter |
| 自备输入 | 符合[模型契约](docs/model-contract.md)的 `.rknn`，以及 DMA 来源或 H.264/H.265 MP4/私有 RTSP URL 文件 |

Windows/x64 可构建、运行托管测试和样例帮助，不能运行硬件推理。不同 BSP、运行库和模型必须重新验证。仓库不附模型、视频、第三方 SDK 或预编译 `.so`，也没有承诺预编译 NuGet/Release 下载。

库没有按 Runtime 版本字符串固定拒绝其他版本，但会校验 ABI、模型和缓冲契约。编译头文件须与目标 Runtime 匹配，模型 Toolkit、Runtime 和 BSP 驱动须兼容；三者版本号无需数字相同。上述记录不代表所有 2.x、其他模型或全部媒体链路均已验证。

## 快速开始

在源码根目录执行，首次还原需访问 NuGet 包源：

```sh
dotnet build RKNNSharp.slnx -c Release
dotnet test tests/RKNNSharp.Tests/RKNNSharp.Tests.csproj -c Release --no-build
dotnet run --project samples/RKNNSharp.Smoke -c Release --no-build -- --help
```

这些步骤不访问真实设备。测试使用 xUnit/VSTest；通过托管测试不代表板端硬件链路通过。

应用可以直接引用项目：

```sh
dotnet add path/to/YourApp.csproj reference src/RKNNSharp/RKNNSharp.csproj
```

`path/to/YourApp.csproj` 替换为自己的 `net10.0` 应用项目。核心调用入口：

```csharp
using RKNNSharp;

using var model = new RknnModel(modelPath);
// 示例使用官方 YOLOv5 640 anchors，必须先确认与自己的模型一致。
var profile = YoloV5Profile.CreateOfficial640Example();
profile.ValidateModel(model.Inputs, model.Outputs);
// frame 由采集层提供；调用方持有 sample/FD，且生产者已完成写入。
DetectionResult result = model.Detect(frame, profile);
foreach (Detection detection in result.Detections)
    Console.WriteLine($"{detection.ClassId}: {detection.Confidence:F3} {detection.Box}");
// 同步调用返回后才可释放或复用输入 sample。
```

`modelPath` 和 `frame` 来自调用方。完整的 DMA 描述符、绘框、`Run`/`RunInto` 缓冲复用和并发契约见 [API 说明](docs/api.md)。DFL 三尺度输出使用 [YoloDetectionProfile](docs/yolo-detection.md)，不能仅凭 YOLO 名称推断模型兼容。

`Run`/`RunInto` 默认保留 SDK float 转换。可通过四参构造显式选择 `FloatOutputMode.PreferFastInt8`，允许合适的标准 INT8 head 使用快速转换，其他 head 仍由 SDK 转换；`Detect` 不受影响。该模式可能有浮点舍入差，不能保证 SDK 逐 bit 一致或任意阈值附近的检测决策相同；使用前应验证自己的模型与阈值。旧 native 继续支持默认模式，缺少可选配置导出时显式快速请求会报不支持。调用示例和精度限制见 [可选模式契约](docs/api.md#显式可选的快速浮点转换)。

## 在目标板运行样例

在构建主机发布自包含示例，输出目录可自行调整：

```sh
dotnet publish samples/RKNNSharp.Smoke -c Release -r linux-arm64 --self-contained true -o publish/linux-arm64
```

将整个输出目录传到目标板。按[核心原生构建](docs/hardware.md#从源码构建)和[采集 adapter 构建](docs/gstreamer.md#从源码构建与首次检查)生成 `librknnsharp_native.so`、`librknnsharp_gst.so`，放入板端发布目录，并配置 RKNN/RGA 动态库搜索路径。自包含仅包含 .NET 运行时。

在板端 Bash 设置自己的绝对路径与来源色域后，先运行单帧示例：

```sh
read -r -p '板端发布目录: ' PUBLISH_DIR
read -r -p '兼容的 RKNN 模型: ' MODEL_PATH
read -r -p '自备 MP4 文件: ' VIDEO_PATH
read -r -p '来源色域 auto/601limit/601full/709limit: ' COLOR_SPACE
: "${PUBLISH_DIR:?}" "${MODEL_PATH:?}" "${VIDEO_PATH:?}" "${COLOR_SPACE:?}"
"$PUBLISH_DIR/RKNNSharp.Smoke" "$MODEL_PATH" --single-frame --file "$VIDEO_PATH" h264 "$COLOR_SPACE"
```

H.265 输入将 `h264` 改成 `h265`。`auto` 要求 caps 明确声明支持的色域；否则由调用方确认矩阵和范围。RTSP 使用 `--rtsp-file` 替换 `--file`，文件内容为完整私有 URL；不要把 URL 或凭据写进命令行、日志或仓库。

样例输出单帧 PTS、检测结果和阶段耗时，不保存或显示图像。完整参数、退出码、有限帧诊断及流水线入口见[样例说明](samples/RKNNSharp.Smoke/README.md)；完整部署步骤见[整合说明](docs/integration.md#从源码使用)。

## 限制与排错

- `DmaFrame` 只借用 FD，不延长生产者生命周期；仅支持线性、单 FD、offset=0 的 NV12/RGB888/RGBA8888。NV12 必须明确色域。
- 当前原生输入只接受静态 NHWC RGB INT8、zeroPoint=-128、scale=`float(1/255)`。YOLOv5 helper 要求三个标准 NCHW INT8 raw-logit head 和显式 anchors，不猜类别名称。
- 硬件调用同步且中途不可取消；同一模型实例的推理与释放串行。`Run` 返回独立快照，`RunInto` 结果随调用方缓冲区下一次写入而改变。
- `DllNotFoundException`：检查运行平台、两个自编译 `.so` 及其 `ldd` 依赖；`EntryPointNotFoundException`：检查托管库与原生 ABI 是否匹配。
- 采集失败：检查 `gst-inspect-1.0 mppvideodec`、输入编码与线性 DMA 布局。色域拒绝时核实来源，不按分辨率猜测。
- 模型契约拒绝：核对输入量化、布局、输出 head 和后处理配置，不以更改阈值绕过。

`docs/` 中的硬件性能和数值记录是开发时特定模型/BSP的历史观察，原始私有素材与日志不随仓库发布。本次源码入口的主机验证与硬件验证应分别判断；不宣称任意模型的精度、导出数值 parity 或 YOLO11 的硬件兼容。

## 源码导航

| 路径 | 内容 |
| --- | --- |
| `src/RKNNSharp` | C# API、生命周期、DMA 描述符、检测后处理 |
| `native` | RKNN/RGA C ABI 桥接与硬件诊断源码 |
| `samples/RKNNSharp.Smoke` | 单帧入门和有限帧/流水线诊断 |
| `samples/GStreamerNative` | 独立 GStreamer/MPP DMA 采集 adapter |
| `tests/RKNNSharp.Tests` | 不需要硬件的托管契约与算法测试 |
| `tools/model-probe` | 读取模型内嵌元数据的开发期探针 |

第三方运行库、插件和模型由使用方依据其来源许可取得；公开源码不包含这些资产。
