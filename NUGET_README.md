# RKNNSharp 0.3.0

面向 **.NET 10 / Linux ARM64 / RK3588** 的 C# 推理库。应用提供已就绪的 DMA 图像，库通过 RGA 预处理和 RKNN NPU 推理，返回检测结果或浮点张量。适合已有硬件采集链路的工业视觉应用。

包包含托管 DLL、XML 文档、MIT 许可证及 Linux ARM64 的自有原生桥接库；依赖 `Microsoft.Extensions.Logging.Abstractions >= 10.0.0`，不配置日志时使用空日志。**采集、解码、模型、厂商运行库、驱动及 .NET 运行时不在包内**。GStreamer/MPP adapter 是[仓库样例](https://github.com/sqk0232/RKNNSharp/tree/main/samples)，不是 NuGet 的采集 API。

## 安装与运行条件

在自己的 `net10.0` 应用目录执行：

```sh
dotnet add package RKNNSharp --version 0.3.0
dotnet publish -c Release -r linux-arm64 --self-contained false
```

将整个发布目录部署到板端，安装 .NET 10 运行时，确保外置 `librknnrt.so`、`librga.so` 及其依赖能被动态加载器找到。SDK 会选择包内 `runtimes/linux-arm64/native/librknnsharp_native.so`，无需另行编译桥接库。

已测环境为 RK3588 LubanCat-5 V2、Linux AArch64/kernel 6.1.84、Ubuntu 22.04.5/glibc 2.35、RKNN Runtime 2.3.2 / RKNPU driver 0.9.8、RGA 1.10.1_[3] / driver 1.3.7。桥接库使用 ABI **2**。运行用户需要访问 `/dev/rga`、NPU 节点及 `/dev/dma_heap/system-uncached-dma32`；设备权限按 BSP 配置，不要求以 root 运行。其他 BSP、RK3588S、运行库及模型组合需自行验证，Windows/x64 不能运行硬件推理。

## 最小调用

下面代码可直接作为应用中的一个 C# 文件编译。采集层取得符合契约的 `DmaFrame` 后调用 `FrameDetection.DetectFrame(modelPath, frame)`。宽高自动从模型读取，示例的 80 类、三尺度 YOLOv5 raw-logit 输出和所列 anchors 须与实际模型匹配。

```csharp
using System;
using RKNNSharp;

public static class FrameDetection
{
    public static DetectionResult DetectFrame(string modelPath, DmaFrame frame)
    {
        var config = new RknnPipelineConfig(classCount: 80, anchors:
        [
            [new(10, 13), new(16, 30), new(33, 23)],
            [new(30, 61), new(62, 45), new(59, 119)],
            [new(116, 90), new(156, 198), new(373, 326)]
        ]);
        using var pipeline = new RknnPipeline(
            ModelType.YoloV5Detection, modelPath, config);
        DetectionResult result = pipeline.Predict(frame);
        foreach (Detection detection in result.Detections)
            Console.WriteLine($"{detection.ClassId}: {detection.Confidence:F3} {detection.Box}");
        return result;
    }
}
```

这是单次调用的生命周期示例；连续推理时在循环外创建一个 `RknnPipeline`，全部调用结束后再释放。检测结果包含类别、置信度、源图坐标框、letterbox 变换与阶段耗时。调用为同步操作，中途不可取消，同一实例的预测并发借用构造时建立的固定池，默认 1 个，可通过 RknnModelOptions.SessionPoolSize 调整，释放等待在途调用。

加载后通过 `pipeline.InputWidth`、`InputHeight`、`BatchSize` 和 `InputTensor` 查看模型属性，`pipeline.Config` 为绑定模型的独立配置。通道顺序 `ChannelOrder.Rgb`、缩放 `ResizeMode.Letterbox` 和归一化 `NormalizationMode.ModelDefault` 均有默认值。ModelDefault 沿用精确 affine INT8 量化契约，不额外施加均值/方差。BGR、Stretch、None、DivideBy255 当前会明确拒绝。原有显式宽高构造保留，尺寸不匹配时异常同时列出配置值和模型值。

`DmaFrame` 借用采集层 FD，不拥有它。生产者必须完成写入，调用方须持有 sample/FD 至同步返回，随后才可复用或释放。输入仅支持线性、单 FD、offset=0 的 NV12/RGB888/RGBA8888；NV12 必须显式声明支持的矩阵和范围。原生模型输入要求静态 batch=1、NHWC RGB affine INT8、zero point=-128、scale 精确为 `float(1/255)`。不提供 CPU 图像处理或软件解码回退。

## 会话池（SessionPoolSize）

`RknnModelOptions.SessionPoolSize` 默认 **1**。构造时一次建好 N 个原生会话，推理只借还已有会话，不按帧或形状重建上下文，用于提高并发吞吐、避免反复创建的内存与时延开销。低帧率、单线程顺序调用使用 1 节省内存；单路高帧率、多路相机或一帧多个模型/多个调用并发时，可手动设为 **3**，不要求有多路图像源。RK3588 有 3 个 NPU 核，3 是常用调优上限，并非 API 限制。每个模型有自己的池；多个模型分别配置，池大小不会让顺序代码自动并行。所有会话沿用同一 `CoreMask`，不自动分核。

检测仍用 `RknnPipelineConfig` 配置 anchors/heads，池大小则通过 `RknnModelOptions` 传入构造参数。沿用上面最小调用的 `config`、`modelPath`：

```csharp
var options = new RknnModelOptions { SessionPoolSize = 3 };
using var pipeline = new RknnPipeline(
    ModelType.YoloV5Detection, modelPath, config, options);
```

原始张量也可用 `new RknnModel(modelPath, options)`。允许正 `int`（1 至 `int.MaxValue`），小于 1 时在创建原生会话前抛出 `ArgumentOutOfRangeException`；没有额外的软件上限，但较大值仍受设备资源限制，创建失败会清理已建会话。

首个会话加载模型，其余经 `rks_dup_context` 调用 `rknn_dup_context` 复制上下文，共享权重；每个会话的输入输出缓冲互相隔离、可并发。调用方须保持输入 DMA 就绪且存活，并为并发调用提供独占输出缓冲。池用尽时调用方阻塞等待会话归还，没有超时或取消参数。

`Dispose` 开始后拒绝新调用，唤醒并拒绝排队调用，借用时抛出 `ObjectDisposedException`；等在途推理全部归还后逆序销毁全部会话，复制会话先于主会话，每个只销毁一次，可重复释放。旧 `librknnsharp_native.so` 若缺少 `rks_dup_context` 导出，构造时逐个独立创建其余会话，不共享权重、内存更高，通过 `options.LoggerFactory` 在启用 Warning 日志时记录一次警告（默认空日志不输出）；推理期仍不重建。真实复制错误会使构造失败并清理已建会话。

静态板测条件：RK3588、RKNN Runtime 2.3.2、驱动 0.9.8，YOLOv5s / YOLOv8n INT8，复用单张 512×384 RGB888 图片（模型输入 640×640）。干净性能测试未挂载诊断 `LD_PRELOAD`，每组预热 100 + 正式 300 帧 × 3 轮，合并 900 个正式样本；FPS 为总帧数/总用时，均值为单次调用耗时，含预处理、推理、检测后处理，不含采集/解码等待及模型构造。

| 模型 | 池 1 / 单线程 FPS | 单帧均值 ms | 池 3 / 三线程 FPS | 单帧均值 ms |
| --- | ---: | ---: | ---: | ---: |
| YOLOv5s INT8 | 50.82 | 19.67 | 138.72 | 21.57 |
| YOLOv8n INT8 | 52.34 | 19.10 | 148.00 | 20.18 |

并行吞吐提高不等于单请求延迟降低，不能取总 FPS 的倒数作为延迟；轮间有波动，其他模型/设备需重测。同一设备、Runtime、驱动及静态模型的另组验证中，池 3 共享权重相比逐会话独立创建，按 dma-buf 统计节省 DMA 内存约 16.2 MiB（YOLOv5s）/ 6.5 MiB（YOLOv8n）；池 1/3、单线程/多线程的 1210 次结果比较全部一致；150 次创建/推理/释放循环后上下文与 DMA 内存不累积。有限验证不代表动态维或长期运行均通过。

## 模型与输出选择

| `ModelType` | 配置与调用 | 支持边界 |
| --- | --- | --- |
| `YoloV5Detection` | `RknnPipelineConfig(…, anchors: …)`；`Predict` | 三个标准 NCHW affine INT8 raw-logit head，显式 anchors |
| `YoloV8Detection` | `RknnPipelineConfig(…, heads: …, regMax: …)`；`Predict` | 指定 RKNN 三尺度 DFL 契约，stride 8/16/32；不代表任意 YOLOv8/YOLO11 导出兼容 |
| `RawTensor` | 不传检测 config；`PredictRaw` / `PredictRawInto` | 仍受原生输入契约限制，不提供 `Predict` / `Decode` 检测 |

`PredictRaw` 每次返回独立 float 快照。`PredictRawInto` 写入调用方独占缓冲区；旧结果引用相同有效段，下一次写入会覆盖，任意失败后全部输出视为不完整。检测实例也可用 `Decode` 解码浮点输出作为参考。默认保持 SDK float 转换；显式快速转换可能有舍入差及阈值附近决策差异。

## 从 0.2.0 迁移

0.3.0 是破坏性托管 API 更新，需修改源码并重新编译，没有旧入口兼容层。

| 官方 0.2.0 | 0.3.0 |
| --- | --- |
| `RknnModel` | `RknnPipeline(ModelType, modelPath, config?, options?, logger?)` |
| `YoloV5Profile` + `Detect` | anchors 配置 + `YoloV5Detection` + `Predict` |
| `YoloDetectionConfig` / `YoloDetectionOutputFormat` / `YoloDetectionProfile` | heads/RegMax 配置 + `YoloV8Detection` + `Predict` / `Decode` |
| `Run` / `RunInto` | `PredictRaw` / `PredictRawInto`；普通张量模型选择 `RawTensor` |
| DMA、结果、选项契约；原生 ABI 2 | 保留；Logging.Abstractions 依赖继续存在 |

上述表格保留0.3.0迁移历史；新增公开RknnModel(modelPath)及options重载用于原始张量，检测profile保持内部实现，历史映射见[0.3迁移指南](https://github.com/sqk0232/RKNNSharp/blob/main/docs/migration-0.3.md)。

本轮包含 DFL INT8 路径、profile 缓存、NMS 原地排序及源 DMA 导入缓存；[优化前后测量](https://github.com/sqk0232/RKNNSharp/blob/main/README.md#本轮优化前后)只代表所列模型和设备，包含前后处理、不含采集解码，不是官方 0.2.0 对 0.3.0 的版本跑分。不承诺任意模型加速、mAP 或长期稳定性。

## 失败与支持

- 平台或动态加载失败：检查 Linux ARM64、桥接库、厂商库及传递依赖；ABI/导出错误时核对托管与原生资产是否配套。
- 模型或帧被拒绝：检查量化、layout、stride、size、色域、输出映射和 RGA geometry；硬件不支持时明确失败，不自动降级。
- 不带模型、类别名或通用自动识别；调用方负责模型来源、转换配置和相应许可。

继续阅读[API 与缓冲生命周期](https://github.com/sqk0232/RKNNSharp/blob/main/docs/api.md)、[模型契约](https://github.com/sqk0232/RKNNSharp/blob/main/docs/model-contract.md)、[DFL 配置](https://github.com/sqk0232/RKNNSharp/blob/main/docs/yolo-detection.md)、[硬件部署](https://github.com/sqk0232/RKNNSharp/blob/main/docs/hardware.md)及[更新记录](https://github.com/sqk0232/RKNNSharp/blob/main/CHANGELOG.md)。反馈问题请使用 [GitHub Issues](https://github.com/sqk0232/RKNNSharp/issues)，提供版本、BSP/运行库、模型元数据和脱敏错误信息。
