# 0.2.0 → 0.3.0 迁移

0.3.0 统一了模型构造和检测入口，属于破坏性托管 API 更新。升级包后需要修改源码并重新编译；原生 ABI 继续为 **2**，使用包内匹配的桥接库。不要将旧托管 DLL 和新 API 混合部署。

## 官方版本功能与兼容性

本表对比官方 NuGet 0.2.0 的公开 API，而非开发过程中的中间命名。

| 功能/契约 | 官方 0.2.0 | 0.3.0 | 迁移动作 |
| --- | --- | --- | --- |
| 构造入口 | `RknnModel` | `RknnPipeline` + `ModelType` | 替换类型与构造；保留可选 `RknnModelOptions` |
| V5 检测 | `YoloV5Profile` + `Detect` | `RknnPipelineConfig` anchors + `YoloV5Detection` + `Predict` | 显式保留原 anchors/阈值/类别数 |
| 指定 RKNN DFL | `YoloDetectionConfig` / `YoloDetectionOutputFormat` / `YoloDetectionProfile` | 统一 config heads/RegMax + `YoloV8Detection` + `Predict` / `Decode` | 保留输出索引、stride、RegMax 与阈值，不按模型名称猜格式 |
| 原始输出 | `Run` / `RunInto` | `PredictRaw` / `PredictRawInto` | 普通张量模型选 `RawTensor`，不传检测 config |
| DFL 检测内部路径 | SDK float 输出后解码 | 标准 INT8 借用输出后解码 | `Predict` 避免完整 float 张量；float 参考用 `PredictRawInto` + `Decode` |
| 配置/metadata | profile 验证 | 配置防御复制；构造加载后验证 metadata；缓存有效绑定 | 非法配置更早抛异常；失败构造释放 session |
| NMS | 稳定检测顺序 | 原地候选排序，保留显式同分次序 | 不承诺其他框架的同分选择完全相同 |
| 源 DMA 导入 | 逐次导入 | 每 session 有界 LRU，最多 8 项/64 MiB | 仍须持有输入 owner 至同步返回；缓存不延长采集 owner 生命周期 |
| DMA/结果/选项公开契约 | 已提供 | 共享类型签名保留 | 快照与 Into 别名语义不变，失败后 Into 全部输出无效 |
| native ABI / 平台 | ABI 2；net10.0/Linux ARM64/RK3588 | 同前，桥接含缓存与 GCC 构建修复 | 使用本版本配套桥接库；其他 BSP 重验 |
| 日志 | `Microsoft.Extensions.Logging.Abstractions` 10.0.0 | 同依赖；可选 logger/LoggerFactory，默认空日志 | logger 与 factory 由调用方管理生命周期 |
| 媒体采集 | 仓库独立 GStreamer/MPP adapter | 原始 caps 色域校验修复 | adapter 仍不包含在核心 NuGet；不能按高度推断未知色域 |

以下五个官方 0.2.0 类型不再公开：`RknnModel`、`YoloDetectionConfig`、`YoloDetectionOutputFormat`、`YoloDetectionProfile`、`YoloV5Profile`。没有公开兼容重载。`ModelType`、`RknnPipeline`、`RknnPipelineConfig` 为新增类型；官方 0.2.0 中没有 `ModelType`，开发中间枚举名不属于已发布兼容性历史。

## 调用映射

统一构造为 `new RknnPipeline(modelType, modelPath, config, options, logger)`。可省略 options/logger；`RawTensor` 必须省略 config。当前枚举仅有 `YoloV5Detection`、`YoloV8Detection`、`RawTensor`。

V5 将原 `YoloV5Profile` 的输入尺寸、类别数、anchors、阈值、最大检测数传入 `RknnPipelineConfig`，选择 `YoloV5Detection`，把 `Detect(frame, profile)` 改为 `Predict(frame)`。DFL 将原配置的 heads、RegMax、阈值和类别名传入统一 config，选择 `YoloV8Detection`；`Predict` 走 INT8 检测，`Decode(pipeline.PredictRawInto(frame, buffers))` 保留浮点参考用法。可编译 V5 示例见 [NuGet 使用说明](../NUGET_README.md#最小调用)，DFL 输出映射见 [DFL 检测契约](yolo-detection.md)。

`PredictRaw` 返回独立快照，适合诊断与低频调用。`PredictRawInto` 每个 head 的 caller-owned `Memory<float>` 仅分配一次，消费完成再复用；返回结果引用同一缓冲，不能跨下一次写入保留。检测 config 在构造时复制，修改原集合不会改变已创建实例。一个 pipeline 的预测、Decode、Dispose 互斥；多实例仍需调用方管理共享输入 owner 与输出缓冲区同步。

## 验证与回退

先确认原模型满足输入/输出契约，再验证同一帧的检测、全部输出 head、阈值附近行为和失败恢复。已有指定 V5/DFL 模型的有限 DMA/硬件解码、缓存、profile 更替和资源循环验证只覆盖记录的 RK3588/BSP；不代表任意模型精度、mAP、实时重连或长期稳定性。

本轮性能对照使用优化前提交 `f4f60c00fe4030a4befbbeef228fac31a2eade2a`；官方 0.2.0 tag 指向另一个提交，不能将该对照标成版本性能比较。环境、模型 SHA256 与统计边界见 [本轮优化前/后](../README.md#本轮优化前后)。NMS 单项分配量和每项优化独立收益未测；638×640/左 padding 1 在当前 BSP 上被 RGA 拒绝，636×640/左 padding 2 已通过，不扩大支持范围。

回退时将应用源码和 RKNNSharp 包一同恢复到已验证的 0.2.0 调用方式，并重新发布配套资产；只换 DLL 无法恢复已移除的托管调用。模型、厂商库和 BSP 依赖保持与各自已验证部署匹配。
