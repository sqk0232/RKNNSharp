# Changelog

本文件按 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 格式记录版本变更。

## [0.2.0] - 未发布

### Added

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

- 原生 ABI 仍为 **2**，公共 API 无变化。
- README 性能数值保留 2026-10-29 历史数据，待 0.2.0 板端同口径重测后更新。

## [0.1.0] - 2026-10-30

### Added

- 首次发布 .NET 10 / C# 14 的 RKNNSharp 托管库及 Linux ARM64 原生桥接库，面向 RK3588 的 DMA 帧、RGA 预处理与 NPU 推理。
- 提供 `Run` / `RunInto`、YOLOv5 与显式 DFL 检测解码、DMA 图像分配/复制/绘框，以及可选快速 INT8 浮点转换模式。
- 提供 GStreamer/MPP 采集 adapter、Smoke 样例、托管测试和硬件诊断入口。
- 提供 MIT 许可证、使用与部署文档，以及标明测量条件的历史测试数据。
- 支持 NuGet 打包，包含托管程序集、XML 文档、README、许可证和 Linux ARM64 桥接库；厂商库、驱动、模型及 .NET 运行时由使用方准备。

[0.2.0]: https://github.com/sqk0232/RKNNSharp/compare/v0.1.0...release/0.2.0
[0.1.0]: https://github.com/sqk0232/RKNNSharp/releases/tag/v0.1.0
