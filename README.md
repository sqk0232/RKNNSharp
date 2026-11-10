# RKNNSharp

[![NuGet](https://img.shields.io/nuget/v/RKNNSharp.svg)](https://www.nuget.org/packages/RKNNSharp/)

面向 **Linux ARM64 / RK3588** 的 C# RKNN 推理库。接收采集层已就绪的 DMA 帧，通过 RGA 预处理、NPU 推理，返回 tensor 或检测结果。核心托管库不依赖 GStreamer、OpenCV、Python 或 PyTorch；仓库提供独立的 GStreamer/MPP 采集样例。

硬件路径为 `VPU/MPP → NV12 DMA → RGA resize/CSC/INT8 → NPU → CPU tensor 后处理`。RGA 存在必要的硬件拷贝，不宣称全链路无拷贝。绘框在独占 DMA 图像上进行，不修改解码器参考帧；不提供 CPU 图像处理或软件解码回退。

## 环境与范围

| 项目 | 要求 |
| --- | --- |
| 构建主机 | Windows 或 Linux，.NET SDK **10.0.403**（`global.json` 允许同 feature band 的更新补丁） |
| 托管代码 | `net10.0` / C# 14；依赖 `Microsoft.Extensions.Logging.Abstractions` 10.0.0，默认空日志 |
| 运行目标 | Linux ARM64 RK3588、兼容 BSP、NPU/RGA3、DMA32 设备及访问权限 |
| 已记录的硬件组合 | Runtime 2.3.2 / RKNPU 0.9.8 / RGA 1.10.1 的指定 V5/DFL、DMA 缓存和有限硬件解码；早期记录另含 Runtime 1.5.2，详见[硬件约束](https://github.com/sqk0232/RKNNSharp/blob/main/docs/hardware.md) |
| 采集样例额外依赖 | GStreamer ≥1.20、BSP 的 `mppvideodec`、原生采集 adapter |
| 自备输入 | 符合[模型契约](https://github.com/sqk0232/RKNNSharp/blob/main/docs/model-contract.md)的 `.rknn`，以及 DMA 来源或 H.264/H.265 MP4/私有 RTSP URL 文件 |

Windows/x64 可构建、运行托管测试和样例帮助，不能运行硬件推理。不同 BSP、运行库和模型必须重新验证。源码不附模型、视频或第三方 SDK；NuGet 包的内容和构建方式见下节，可用版本见 [NuGet 包页面](https://www.nuget.org/packages/RKNNSharp/)。

库没有按 Runtime 版本字符串固定拒绝其他版本，但会校验 ABI、模型和缓冲契约。编译头文件须与目标 Runtime 匹配，模型 Toolkit、Runtime 和 BSP 驱动须兼容；三者版本号无需数字相同。上述记录不代表所有 2.x、其他模型或全部媒体链路均已验证。

## 源码开发快速开始

NuGet 使用者请先阅读[消费者快速开始](https://github.com/sqk0232/RKNNSharp/blob/main/NUGET_README.md)，其中提供安装、可编译调用及运行条件。以下步骤供仓库开发与样例验证使用。

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

`path/to/YourApp.csproj` 替换为自己的 `net10.0` 应用项目。0.3.0 使用下列统一入口（相对官方 0.2.0 为破坏性 API 更新）：

```csharp
using RKNNSharp;

public static class FrameDetection
{
    public static DetectionResult DetectFrame(string modelPath, DmaFrame frame)
    {
        // 官方 640 anchors 仅为示例假设，须确认与自己的模型一致。
        var config = new RknnPipelineConfig(640, 640, 80, anchors:
        [
            [new(10, 13), new(16, 30), new(33, 23)],
            [new(30, 61), new(62, 45), new(59, 119)],
            [new(116, 90), new(156, 198), new(373, 326)]
        ]);
        using var pipeline = new RknnPipeline(ModelType.YoloV5Detection, modelPath, config);
        DetectionResult result = pipeline.Predict(frame);
        foreach (Detection detection in result.Detections)
            Console.WriteLine($"{detection.ClassId}: {detection.Confidence:F3} {detection.Box}");
        return result;
    }
}
```

普通非检测模型使用 `new RknnPipeline(ModelType.RawTensor, modelPath)`，通过 `PredictRaw` 或 `PredictRawInto` 获取张量；此分支不提供检测解码。旧模型构造和 profile API 不再公开。

采集层准备 `modelPath` 和 `frame` 后调用此方法，持有 sample/FD 至同步返回。连续推理应将 pipeline 创建移到循环外，全部调用结束后释放。完整的 DMA 描述符、绘框、`PredictRaw`/`PredictRawInto` 缓冲复用和并发契约见 [API 说明](https://github.com/sqk0232/RKNNSharp/blob/main/docs/api.md)。DFL 三尺度输出使用 [ModelType.YoloV8Detection](https://github.com/sqk0232/RKNNSharp/blob/main/docs/yolo-detection.md)，不能仅凭 YOLO 名称推断模型兼容。

`PredictRaw`/`PredictRawInto` 默认保留 SDK float 转换。可通过统一构造入口的 `RknnModelOptions.OutputMode` 显式选择 `FloatOutputMode.PreferFastInt8`，允许合适的标准 INT8 head 使用快速转换，其他 head 仍由 SDK 转换；`Predict` 不受影响。该模式可能有浮点舍入差，不能保证 SDK 逐 bit 一致或任意阈值附近的检测决策相同；使用前应验证自己的模型与阈值。旧 native 继续支持默认模式，缺少可选配置导出时显式快速请求会报不支持。调用示例和精度限制见 [可选模式契约](https://github.com/sqk0232/RKNNSharp/blob/main/docs/api.md#显式可选的快速浮点转换)。

## NuGet 安装与本地打包

0.3.0 包仅面向 **.NET 10** 应用；没有提供 .NET 6/8 资产。核心包包含 `RKNNSharp.dll`、XML 文档、MIT 许可证、消费者专用 `NUGET_README.md`，以及 `runtimes/linux-arm64/native/librknnsharp_native.so`，依赖 Logging.Abstractions 10.0.0。包本身不包含 .NET 运行时、模型、厂商 RKNN/RGA 库、驱动或 GStreamer 采集 adapter。

从 nuget.org 安装到自己的应用，再发布 Linux ARM64 输出：

```sh
dotnet add path/to/YourApp.csproj package RKNNSharp --version 0.3.0
dotnet publish path/to/YourApp.csproj -c Release -r linux-arm64 --self-contained false
```

上述应用路径需替换为自己的项目；选择依赖框架部署时，目标板必须已安装 .NET 10 运行时。SDK 会根据 `linux-arm64` 选择并复制包内原生资产，使用方无需自行复制本项目的桥接库，仍需配置外置厂商库的动态加载路径和设备权限。

从源码本地打包时，默认使用仓库内受版本管理的已验收 Linux ARM64 自有桥接库，在源码根目录执行：

```sh
dotnet pack src/RKNNSharp/RKNNSharp.csproj -c Release -o publish/nuget
```

默认输入为 `native/prebuilt/linux-arm64/librknnsharp_native.so`，pack 校验其 SHA256 `fc31bf63de75a64d40b5432448475bacf2e1512c84aa194ca66bbc66f56c3f6a`；[来源记录](https://github.com/sqk0232/RKNNSharp/blob/main/native/prebuilt/linux-arm64/provenance.json)绑定 ABI 2、受测源码 hash 与环境。更新源码后须重新构建和验收，并一并更新预置资产、来源记录和预期 hash。自建桥接可用 `-p:RKNNSharpNativeLibraryPath=/absolute/path/librknnsharp_native.so` 指定，此 override 只检查文件存在，不要求匹配预置 hash，调用方负责来源及硬件验证。缺库或默认 hash 不符时打包失败。消费本地包时，在安装命令末尾添加 `--source publish/nuget`，并确保该包源路径相对于执行目录有效。

原生包构建基线为 Linux ARM64、GCC 11.4、Ubuntu 22.04.5/glibc 2.35，硬件为 RK3588。桥接库直接符号要求为 GLIBC ≥2.33、GLIBCXX ≥3.4.21、CXXABI ≥1.3.9；这不等于整个厂商依赖链在这些最低版本上都已验证。已测组合使用 RKNN Runtime 2.3.2 / NPU driver 0.9.8、RGA 1.10.1_[3] / driver 1.3.7；这不是所有 Linux ARM64、其他 glibc/BSP、RK3588S 或 RKNN 版本均已验证的保证。桥接库不带构建机 RPATH/RUNPATH。发布前应使用实际 NuGet 消费项目在目标板验证加载、ABI 2 及有限推理，不把开发设备的绝对 SDK 路径打入包。

## 官方 0.2.0 → 0.3.0 功能与兼容性

| 项目 | 官方 0.2.0 | 0.3.0 |
| --- | --- | --- |
| 模型入口 | `RknnModel`，独立 profile | `RknnPipeline`、`RknnPipelineConfig`、新 `ModelType` |
| 检测 | `Detect`、profile 解码 | `YoloV5Detection` / 指定 DFL 的 `YoloV8Detection`，统一 `Predict` / `Decode` |
| 普通张量 | `Run` / `RunInto` | `RawTensor`，`PredictRaw` / `PredictRawInto` |
| 内部热路径 | DFL float 解码，逐次源导入 | DFL INT8、profile 绑定缓存、NMS 原地排序、每 session 有界源 DMA 导入缓存 |
| 平台/原生契约 | net10.0、Linux ARM64/RK3588、ABI 2 | 保留；分发匹配的已验桥接库 |
| 托管依赖 | Logging.Abstractions 10.0.0 | 保留，默认空日志 |

移除的五个公开类型及具体调用映射见[迁移指南](https://github.com/sqk0232/RKNNSharp/blob/main/docs/migration-0.3.md)。这是源码和二进制不兼容的托管 API 变化，需要重新编译；官方 0.2.0 没有 `ModelType`，不要把开发中间枚举名称当作版本迁移历史。

## 在目标板运行样例

在构建主机发布自包含示例，输出目录可自行调整：

```sh
dotnet publish samples/RKNNSharp.Smoke -c Release -r linux-arm64 --self-contained true -o publish/linux-arm64
```

将整个输出目录传到目标板。按[核心原生构建](https://github.com/sqk0232/RKNNSharp/blob/main/docs/hardware.md#从源码构建)和[采集 adapter 构建](https://github.com/sqk0232/RKNNSharp/blob/main/docs/gstreamer.md#从源码构建与首次检查)生成 `librknnsharp_native.so`、`librknnsharp_gst.so`，放入板端发布目录，并配置 RKNN/RGA 动态库搜索路径。自包含仅包含 .NET 运行时。

在板端 Bash 设置自己的绝对路径与来源色域后，先运行单帧示例：

```sh
read -r -p '板端发布目录: ' PUBLISH_DIR
read -r -p '兼容的 RKNN 模型: ' MODEL_PATH
read -r -p '自备 MP4 文件: ' VIDEO_PATH
read -r -p '来源色域 auto/601limit/601full/709limit: ' COLOR_SPACE
: "${PUBLISH_DIR:?}" "${MODEL_PATH:?}" "${VIDEO_PATH:?}" "${COLOR_SPACE:?}"
"$PUBLISH_DIR/RKNNSharp.Smoke" "$MODEL_PATH" --single-frame --file "$VIDEO_PATH" h264 "$COLOR_SPACE"
```

默认单帧选择 `ModelType.YoloV5Detection`，调用统一的 `Predict`。已确认模型符合 [DFL 九输出示例](https://github.com/sqk0232/RKNNSharp/blob/main/docs/yolo-detection.md) 时，在色域后追加 `dfl`，选择 `ModelType.YoloV8Detection`，仍调用 `Predict`：

```sh
"$PUBLISH_DIR/RKNNSharp.Smoke" "$MODEL_PATH" --single-frame --file "$VIDEO_PATH" h264 "$COLOR_SPACE" dfl
```

H.265 输入将 `h264` 改成 `h265`。`auto` 要求 caps 明确声明支持的色域；否则由调用方确认矩阵和范围。RTSP 使用 `--rtsp-file` 替换 `--file`，文件内容为完整私有 URL；不要把 URL 或凭据写进命令行、日志或仓库。

样例输出单帧 PTS、检测结果和阶段耗时，不保存或显示图像。完整参数、退出码、有限帧诊断及流水线入口见[样例说明](https://github.com/sqk0232/RKNNSharp/blob/main/samples/RKNNSharp.Smoke/README.md)；完整部署步骤见[整合说明](https://github.com/sqk0232/RKNNSharp/blob/main/docs/integration.md#从源码使用)。

## 本轮优化前/后

2026-11-10 在 RK3588 LubanCat-5 V2（Linux AArch64/kernel 6.1.84、RKNN Runtime 2.3.2 / driver 0.9.8、RGA 1.10.1_[3]、.NET 10.0.13）对比本轮优化前提交 `f4f60c00fe4030a4befbbeef228fac31a2eade2a` 与优化后的受测工作树。该基线与官方 0.2.0 tag 的提交不同，本节不作为 0.2.0 → 0.3.0 的版本性能表。

两侧每模型各 3 个独立进程，每进程预热 100 次、计量 300 次，合计各 900 样本；固定 `taskset -c 4`、NPU Core0，复用同一已完成 MPP 解码的 NV12 帧。组合调用包含 RGA 预处理、NPU、输出获取/校验和检测后处理，**不含采集/解码等待**、模型加载、绘框或显示。DFL 基线为 `RunInto` + float 解码，本轮为统一入口的 INT8 `Predict`；这是组合路径比较，未隔离每个优化的独立收益。

| 模型/计时项 | 优化前均值 ms | 优化后均值 ms | 均值变化 | 优化前/后 P95 ms |
| --- | ---: | ---: | ---: | ---: |
| 指定 YOLOv5s 组合调用 | 19.254 | 18.900 | -1.84% | 19.512 / 19.023 |
| 指定 YOLOv8n DFL 组合调用 | 19.601 | 16.894 | -13.81% | 22.539 / 17.840 |
| V5 NPU `rknn_run` | 17.058 | 17.056 | -0.01% | 17.127 / 17.089 |
| DFL NPU `rknn_run` | 14.723 | 14.633 | -0.62% | 16.216 / 15.430 |

NPU 时间基本不变，收益主要在预处理导入、输出和 CPU 后处理的组合路径；不宣传纯 NPU 加速或任意模型均有同样收益。模型 SHA256 分别为 V5 `5484cccbcc30292a167b2f9d7cf7cb06210decf4343e41f59e35c0ee41785f1e`、DFL `61d612e06215a23fe5ca64dc45f13b52dd46630457de459dc72dab31a60145b5`，均为下文记录的 640×640 模型。静态复用帧每次得到各自预期的 5 个目标，不构成标注集 mAP 或运动场景精度结论。

优化前/后桥接库 SHA256 为 `fb6fb9f0f40323b49b6547f1464893698ce0e0a139c57ff98340df5f9bb76836` / `fc31bf63de75a64d40b5432448475bacf2e1512c84aa194ca66bbc66f56c3f6a`；受测托管 DLL SHA256 为 `3b106037c8a8abef057af63342cefc411352aeedd067094113ccf6dd893eaab1` / `6841dcc2ae25f57c543247bca8c39e7c0f9f23203b887f96eec4b76c9a5a8413`。这些 hash 标识本次实测对象，后续版本元数据重包会改变托管 DLL hash，不能称为所有包的通用成绩。NMS 单独分配量、长稳、实时 RTSP 重连、竞品比较未测。

## 测试数据

下列保留 **2026-10-29** 的历史性能测量，不代表 0.3.0 的最终包跑分。耗时单位为 **ms**，表中保留三位小数；模型体积使用十进制 **1 MB = 1,000,000 字节**。不同模型、Runtime、版本和输入不能直接混作一次横向性能比较；吞吐的倒数不是单请求延迟。

### 模型与输入

| 已测模型标识 | RKNN 文件大小 | 模型输入（宽×高） | batch / layout / 类型 | 已知来源与缺项 |
| --- | ---: | --- | --- | --- |
| YOLOv5s（提供文件 `yolov5s_for_rk3588.rknn`） | 8,502,205 字节 / 8.50 MB | 640×640 RGB | 1 / NHWC `[1,640,640,3]` / affine INT8 | 内嵌编译器 1.5.0；名称来自提供文件，原始训练/导出版本、权重来源与许可未完整记录 |
| 指定社区 YOLOv8n（`yolov8n-i8-3588.rknn`） | 4,327,755 字节 / 4.33 MB | 640×640 RGB | 1 / NHWC `[1,640,640,3]` / affine INT8 | 既有社区 RKNN 文件；未证明与官方示例 ONNX 同源，原始训练/转换配置和权重许可未完整记录 |

两者输入 zero point 均为 -128，scale 为精确 `float(1/255)`。这里的 INT8 是模型输入类型；调用方提供的 RGB 图像缓冲是 uint8 像素，随后由 RGA 按[输入契约](https://github.com/sqk0232/RKNNSharp/blob/main/docs/model-contract.md#input-and-preprocessing)转换，二者不能混称。YOLOv5s 为三个标准 NCHW INT8 raw-logit head；此 YOLOv8n 为三尺度九输出的 DFL/类别/可选 score-sum 契约，见[检测格式](https://github.com/sqk0232/RKNNSharp/blob/main/docs/yolo-detection.md)。

模型身份 SHA256：

- YOLOv5s：`5484cccbcc30292a167b2f9d7cf7cb06210decf4343e41f59e35c0ee41785f1e`。
- 指定 YOLOv8n：`61d612e06215a23fe5ca64dc45f13b52dd46630457de459dc72dab31a60145b5`。

| 测量组 | 实际源尺寸/格式 | 送入模型前的 resize / padding |
| --- | --- | --- |
| A：YOLOv5s 真实流；C：完整视频流水线 | 2560×1440 NV12，H.264 解码后的 DMA 帧 | resize 640×360；scaleX=scaleY=0.25；左 padding 0、上 padding 140，目标 640×640 |
| B：指定 YOLOv8n | 预先准备的 640×640 RGB uint8 文件，1,228,800 字节 | 640×640，scale 1，无 padding；仍执行真实 RGA 路径；原始照片尺寸/制备过程未完整记录 |

B 使用 RGB 文件身份 `f279bac6224e70fc19badd6d3fa4f44365d8fd5fe13e80b1865ede45cc0cb4b8`。C 使用有限视频 600 帧、2560×1440、10.029667 秒，每轮单遍读至 EOS；素材不随源码提供。这些记录不构成模型精度、mAP 或任意输入数值一致性的证明。

### 设备、版本与统计口径

硬件为 **RK3588（非 RK3588S）/ LubanCat-5 V2**，Linux AArch64、kernel 6.1.84，RKNPU driver 0.9.8、RGA 用户库 1.10.1_[3]。早期环境记录为 Ubuntu 22.04.5；下表分组列出 Runtime。未锁定 CPU affinity 或更改频率策略，不能排除调度、温度或内存带宽波动。

| 组 | Runtime / 会话 | 预热及测量 |
| --- | --- | --- |
| A | 1.5.2；1 context，Auto mask=0 | 每条路径预热 5 帧、测量 60 帧，1 轮 |
| B | 2.3.2；Core0 mask=1 | 每轮独立进程预热 10 次、测量 60 次；3 轮合计 180 |
| C | 1.5.2；6 context masks=1/2/4/1/2/4，总在途 12 | 每会话预热 5 次，另开文件测量 600 帧；同进程 3 轮 |

以上使用的是当时已测历史版本，当前代码之后有更新，与上节本轮优化对照的输入和计时边界不同。B 的 P95 对合并 180 个原始样本使用 nearest-rank；C 逐轮分别列出，未平均分位数。A 的 P95 沿用当时 60 帧统计。

### A：YOLOv5s 真实流单会话耗时

confidence=0.25、NMS=0.45，官方 640 anchors 仅为明确的示例假设，未证明是该模型原始 anchors。Run 路径执行 float 解码，Detect 执行 raw INT8 检测；两条真实流随时间变化，不是同一帧逐像素性能对照。

| 路径 / 计时项 | 平均 ms | P95 ms |
| --- | ---: | ---: |
| run / NPU rknn_run | 17.123 | 17.247 |
| run / 完整处理 | 33.130 | 35.599 |
| detect / NPU rknn_run | 17.138 | 17.325 |
| detect / 完整处理 | 26.080 | 28.770 |

`rknn_run` 仅为同步 NPU 调用；“完整处理”包含 RGA 预处理、NPU、输出获取、托管复制/校验、检测解码/NMS、RGA copy/draw，**不含** RTSP/VPU 的 pull 等待、模型加载、逐帧日志和输出统计。它不是曝光到检测结果的延迟，也不是含持续解码的整段吞吐。

### B：指定社区 YOLOv8n 单会话耗时

confidence=0.25、NMS=0.45、单标签、最多 300 框，该版本默认使用 SDK float 转换（尚无 `FloatOutputMode` 选项），调用 `RunInto → YoloDetectionProfile.Decode`。调用方输出存储一次分配 payload 4,872,000 字节，不计入每次耗时；每次校验、Pin/Unpin 与元数据处理均计入 API。

| 计时项 | 平均 ms | P95 ms |
| --- | ---: | ---: |
| RGA 输入准备 | 1.156 | 1.243 |
| NPU rknn_run | 14.650 | 15.175 |
| SDK 输出获取/释放 | 1.676 | 1.857 |
| 托管校验/Pin/Unpin及输出元数据 | 0.021 | 0.034 |
| RunInto 完整 API | 17.700 | 18.350 |
| DFL 后处理 | 3.654 | 3.546 |
| RunInto + DFL 后处理 | 21.355 | 22.164 |

不含一次性文件读取、模型/DMA 初始化、相机、绘框或业务系统处理。DFL 后处理平均值高于 P95 是少量长尾所致，原始最大值为 18.194 ms；没有去掉这些样本。上述同 SDK 输出的后处理对照不替代原训练模型的精度验证；并列分数采用本库稳定顺序，不能保证与官方参考默认排序的全部框相同。

### C：YOLOv5s 完整 VPU 视频流水线

同一有限 H.264 视频、confidence=0.25、NMS=0.45，Detect raw INT8 路径。每轮六会话、总在途 12，600 帧全部完成、620 个检测并到达 EOS；预热独立打开文件，不计作测量帧。BT.601 Full 是显式配置，矩阵为测试假设，不能由分辨率推断。

| 轮次 | 完整吞吐 FPS | 单请求处理平均 / P95 ms | 有序完成平均 / P95 ms |
| --- | ---: | ---: | ---: |
| 1 | 127.704 | 46.360 / 50.554 | 92.355 / 98.763 |
| 2 | 128.638 | 46.275 / 49.904 | 92.158 / 98.423 |
| 3 | 128.207 | 46.554 / 49.999 | 92.538 / 98.712 |

完整吞吐从测量 producer 开始至所有结果按序消费，包含 **VPU 解码及等待、RGA 预处理、NPU、INT8 后处理、owned copy/draw、有界调度**；不含模型创建、独立预热、RTSP 网络、编码/显示及计时后日志写出。“单请求处理”从 worker 开始至绘框完成；“有序完成”从该帧取得准入后至按序消费，包含排队，不含 pull 前等待许可，也不是相机拍摄延迟。多会话的同核 NPU 调用包含排队等待，不能当作纯硬件执行变快或变慢的证据。

### 自行复核

先按[源码部署](https://github.com/sqk0232/RKNNSharp/blob/main/docs/integration.md#从源码使用)准备依赖、自己的兼容模型和媒体，再按[样例参数](https://github.com/sqk0232/RKNNSharp/blob/main/samples/RKNNSharp.Smoke/README.md#有限帧诊断)选择单会话、有限视频或重放模式。现有入口包括 [Program.cs](https://github.com/sqk0232/RKNNSharp/blob/main/samples/RKNNSharp.Smoke/Program.cs)、[ParallelSmoke.cs](https://github.com/sqk0232/RKNNSharp/blob/main/samples/RKNNSharp.Smoke/ParallelSmoke.cs) 和 [core_benchmark.cpp](https://github.com/sqk0232/RKNNSharp/blob/main/native/tests/core_benchmark.cpp)。

表中专项 API/DFL 基准使用当时的独立测量程序，未作为公共工具保留；现有样例是调用与阶段测量入口，不保证能原样复现专项表格的全部控制条件。复核时记录模型摘要、输入尺寸/身份、运行库/驱动、mask、预热和样本数，并保持计时边界一致。没有对应 P95、原始训练/导出版本、校准集或许可记录时，应标记未记录；不要由 FPS 推算延迟，不把自己的模型结果套用为本表数据。

## 限制与排错

- `DmaFrame` 只借用 FD，不延长生产者生命周期；仅支持线性、单 FD、offset=0 的 NV12/RGB888/RGBA8888。NV12 必须明确色域。
- 当前原生输入只接受静态 NHWC RGB INT8、zeroPoint=-128、scale=`float(1/255)`。YOLOv5 helper 要求三个标准 NCHW INT8 raw-logit head 和显式 anchors，不猜类别名称。
- 硬件调用同步且中途不可取消；同一模型实例的推理与释放串行。`PredictRaw` 返回独立快照，`PredictRawInto` 结果随调用方缓冲区下一次写入而改变。
- `DllNotFoundException`：检查运行平台、两个自编译 `.so` 及其 `ldd` 依赖；`EntryPointNotFoundException`：检查托管库与原生 ABI 是否匹配。
- 采集失败：检查 `gst-inspect-1.0 mppvideodec`、输入编码与线性 DMA 布局。色域拒绝时核实来源，不按分辨率猜测。
- 模型契约拒绝：核对输入量化、布局、输出 head 和后处理配置，不以更改阈值绕过。

`docs/` 中的硬件性能和数值记录是开发时特定模型/BSP的历史观察，原始私有素材与日志不随仓库发布。主机验证与硬件验证应分别判断；不宣称任意模型的精度、导出数值 parity 或 YOLO11 的硬件兼容。

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
