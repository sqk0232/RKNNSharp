# Changelog

本文件按 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 格式记录版本变更。

## [Unreleased]

## [0.3.0] - 2026-11-10

### Changed

- **破坏性托管 API 更新**: 新增 `RknnPipeline(ModelType, modelPath, config?, options?, logger?)` 和 `RknnPipelineConfig`。`ModelType` 当前为 `YoloV5Detection`、`YoloV8Detection`、`RawTensor`；检测统一使用 `Predict`，张量使用 `PredictRaw` / `PredictRawInto`，浮点参考使用 `Decode`。
- 官方 0.2.0 的 `RknnModel`、`YoloDetectionConfig`、`YoloDetectionOutputFormat`、`YoloDetectionProfile`、`YoloV5Profile` 不再公开，无兼容层；详见[迁移指南](docs/migration-0.3.md)。官方 0.2.0 没有 `ModelType`，开发中间枚举名不构成已发布枚举改名。
- 检测配置防御复制、构造前置校验及加载后 metadata 验证；构造失败释放 session，预测、解码与释放互斥。profile 缓存有效模型绑定，替换失败保留原绑定。
- DFL `Predict` 使用标准 affine INT8 借用输出，先筛类别再反量化候选 logits；NMS 原地候选排序并保持显式同分次序。`PredictRaw` 独立快照与 `PredictRawInto` 调用方缓冲别名契约保持不变，文档补充复用及失败边界。
- 原生源 DMA 导入使用每 session 有界 LRU（最多 8 项/64 MiB），核对 FD 对应 allocation 身份与布局，失败时清空，释放时回收导入引用。
- 默认打包使用受版本管理、已验收的 Linux ARM64 桥接库并校验 SHA256；NuGet 改用消费者专用 `NUGET_README.md`，GitHub README 保留开发资料。CI 增加默认 pack 验证。

### Fixed

- 源 DMA 缓存内部类型移入具名 namespace，修复 GCC 11.4 的 `-Werror=subobject-linkage` 构建失败。
- 独立 GStreamer adapter 从原始 caps 校验 colorimetry，避免归一化或默认推断掩盖未知/非法色域；未知来源仍需调用方确认矩阵和范围。

### Compatibility and validation

- 继续为 `net10.0` / Linux ARM64 / RK3588、原生 **ABI 2**；共享 DMA/张量/结果/选项公开签名保留。依赖 `Microsoft.Extensions.Logging.Abstractions` 10.0.0；厂商库、驱动、模型、运行时及采集 adapter 不在核心包内。
- 当前源码已完成指定 V5/DFL 的有限 RK3588/RKNN 2.3.2/RGA 1.10.1 硬件回归、缓存/失败恢复、非空检测及有限硬件解码验证。性能仅标为[本轮优化前/后](README.md#本轮优化前后)：组合调用 V5 19.254→18.900 ms、DFL 19.601→16.894 ms，每模型每前后各 900 样本，包含前后处理、不含采集解码，NPU 基本不变；优化前基线不是官方 0.2.0 tag。
- 未验证 NMS 单独分配量、各优化独立收益、标注集 mAP、实时 RTSP 重连或长期稳定性。当前 BSP 拒绝 638×640/左 padding 1，支持的 636×640/左 padding 2 已通过对照；不扩大模型/BSP支持承诺。

## [0.2.0] - 2026-10-31

### Added

- 新增 `RknnModelOptions`、可选 `CallerThreadPlacement` 与可注入 `ILoggerFactory` 日志. 默认调度与原构造函数行为保持不变.
- 使用 `static_assert` 固定原生 ABI 结构体大小及关键字段偏移，防止与托管布局漂移。
- 新增 Windows/Linux 的 .NET 10 托管构建与测试 CI。
- Smoke 单帧模式新增 `dfl` 参数，支持 `RunInto` 与 `YoloDetectionProfile` 的 DFL 解码路径。
- NuGet 打包生成 `.snupkg` 符号包，并在 GitHub Actions 中启用 CI 构建属性。

### Changed

- NV12 绘框按每帧声明的色域转换 RGB 颜色；BT.601 full / BT.709 limited 的绘框颜色行为发生变化。
- 跨格式复制/CSC 固定使用 RGA3 核，同格式复制继续交由驱动调度；相较原先调度到 RGA2 的路径，转换像素可能变化。
- Letterbox RGB 预处理仅填充边框，减少不必要的整帧填充，并在批处理不可用时使用同步硬件路径。
- 检测解码采用向量化有限值扫描和类别比较，减少每帧排序及临时分配；YOLOv5 增加 objectness logit 快速拒绝，保留完整 NaN/Inf 检查与阈值附近原有计算。

### Fixed

- `rknn_outputs_get` 失败时仍调用 `rknn_outputs_release`，保留获取及释放错误，避免遗留 Runtime 转换资源。
- DMA32 分配错误保留底层失败原因，区分 RGB 与 INT8 分配阶段。

### Compatibility

- 原生 ABI 仍为 **2**，现有公共 API 签名保持兼容，新增可选配置 API。
- README 性能数值保留 2026-10-29 历史数据，待 0.2.0 板端同口径重测后更新。
- 板端发布门槛: B 组开启大核选项后, 相较 v0.1.0 延迟变慢不得超过 5%; A 组默认调度结果仅记录并写入发版说明, 不阻塞发布. 本机托管测试不能替代板端测量.
- `CpuMask` 为 `ulong`, 仅表示 CPU0–63. `FromCpus` 对负数或大于 1023 的编号抛参数异常, 对 64–1023 明确抛 `NotSupportedException`; 容量发现遇到无法表示的最大容量 CPU 时也明确失败, 不截断位移.

> **调用线程大小核调度（RK3588）**
> 0.2.0 降低了每帧的 CPU 后处理开销（同一 CPU 核上 RunInto+DFL 比 0.1.0 快约 9%）。但在默认调度下，推理调用线程大部分时间在等待 NPU，Linux EAS 可能据此把它放到 Cortex-A55 小核上，导致 `rknn_outputs_get`、反量化和后处理变慢；实测不绑核时延迟可能比 0.1.0 高 10–35%，而且各次运行之间波动较大。默认行为保持不变。对延迟敏感的场景，可以通过 `new RknnModel(path, new RknnModelOptions { CallerThreadPlacement = CallerThreadPlacement.HighestCapacityCores() })`，让每次 `Run`/`RunInto`/`Detect` 调用期间临时把调用线程限制在 `cpu_capacity` 最高的核上（RK3588 实测为 CPU6–7：其 capacity 为 1024，CPU4–5 为 1003，不在选取范围内），调用结束后自动恢复原亲和性，不影响线程池里的其他任务。设置失败时默认只记一条 Warning 并按原方式继续运行；设 `OnFailure = PlacementFailureMode.Throw` 则改为抛异常。本选项不修改 governor、频率或任何系统设置；如果大核已被其他负载占满，请先评估 CPU 预算。
>
> **板端实测（RK3588，librknnrt 2.3.2，YOLOv8n，100 次预热 + 300 次计量，三轮中位数，单位 ms，对比 v0.1.0 默认调度）**
> - B 组（开启 `HighestCapacityCores()`，发版门槛）：RunInto 均值 17.707 / P95 18.375（v0.1.0 为 18.062 / 18.922，-2.0% / -2.9%）；RunInto+DFL 均值 18.965 / P95 19.635（v0.1.0 为 20.882 / 21.628，-9.2% / -9.2%）；计量窗内 A55 占比 0%，轮间离散 ≤2.2%，调用后亲和性均恢复。
> - A 组（默认调度，仅记录）：三轮中有两轮正常（RunInto 约 17.7–17.8、RunInto+DFL 约 19.0–19.1），一轮被调度到 A55（A55 占比约 78%，RunInto 20.84 / RunInto+DFL 25.48）。默认路径仍可能偶发落到小核。
> - D 组（CPU4–7 各有一个 `nice 19` 忙循环）：开启选项 RunInto 约 17.55 / RunInto+DFL 约 18.81，A55 计量占比 0%；默认调度 RunInto 约 21.62 / RunInto+DFL 约 27.65，全部落在 A55。
> - 限制：该选项只使用 capacity 最高的核（RK3588 上仅 CPU6–7 两个核）。多个推理线程同时开启时会在这两个核上竞争，此场景尚未测试，请按实际并发数评估。

## [0.1.0] - 2026-10-30

### Added

- 首次发布 .NET 10 / C# 14 的 RKNNSharp 托管库及 Linux ARM64 原生桥接库，面向 RK3588 的 DMA 帧、RGA 预处理与 NPU 推理。
- 提供 `Run` / `RunInto`、YOLOv5 与显式 DFL 检测解码、DMA 图像分配/复制/绘框，以及可选快速 INT8 浮点转换模式。
- 提供 GStreamer/MPP 采集 adapter、Smoke 样例、托管测试和硬件诊断入口。
- 提供 MIT 许可证、使用与部署文档，以及标明测量条件的历史测试数据。
- 支持 NuGet 打包，包含托管程序集、XML 文档、README、许可证和 Linux ARM64 桥接库；厂商库、驱动、模型及 .NET 运行时由使用方准备。

[0.3.0]: https://github.com/sqk0232/RKNNSharp/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/sqk0232/RKNNSharp/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/sqk0232/RKNNSharp/releases/tag/v0.1.0
