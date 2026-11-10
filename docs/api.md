# API 与输入契约

## 统一构造与预测

0.3.0 只导出一个模型构造入口 `RknnPipeline(ModelType, modelPath, config?, options?, logger?)`. 这是破坏性托管 API 变更: 旧模型与 profile 入口不再公开, 不提供兼容重载; native ABI 继续为 2. 官方 0.2.0 包不包含此新入口, 五个移除类型与调用映射见[迁移指南](migration-0.3.md).

`ModelType.YoloV5Detection` 要求显式 anchors; `YoloV8Detection` 目前仅支持指定 RKNN 三尺度 DFL 导出契约, 要求显式 heads/RegMax. 不保证任意 YOLOv8 导出兼容, 也不表示已支持 YOLO11. 检测 config 必须提供且匹配枚举; 普通原始张量模型使用 `RawTensor`, 不传检测 config.

```csharp
using RKNNSharp;

// 官方 640 anchors 仅为示例假设, 必须确认与自己的模型一致.
var config = new RknnPipelineConfig(640, 640, 80, anchors:
[
    [new(10, 13), new(16, 30), new(33, 23)],
    [new(30, 61), new(62, 45), new(59, 119)],
    [new(116, 90), new(156, 198), new(373, 326)]
]);
using var pipeline = new RknnPipeline(ModelType.YoloV5Detection, modelPath, config);

// 来自采集层: 持有 sample/FD, 且生产者已完成写入.
var frame = new DmaFrame(
    FileDescriptor: dmaFd, Width: 2560, Height: 1440,
    Format: PixelFormat.Nv12, RowStrideBytes: 2560, HeightStride: 1440,
    SizeBytes: allocationBytes, ColorSpace: ColorSpace.Bt601Full);
DetectionResult result = pipeline.Predict(frame);
foreach (Detection item in result.Detections)
{
    Console.WriteLine($"{item.ClassId}: {item.Confidence:F3} {item.Box}");
}
using var annotated = DmaImage.Allocate(frame.Width, frame.Height);
annotated.CopyFrom(frame);
annotated.DrawRectangles(result.Detections);
annotated.WithFrame(output =>
{
    // 同步交给支持 DMA 的下游; 回调结束后不得保留描述符.
    return 0;
});
// 同步调用返回后才可释放输入 sample/FD.
```

配置构造会复制 anchors、heads、标签并校验尺寸、类别、阈值、RegMax 与映射. DFL 索引须唯一且连续覆盖全部输出. Pipeline 在创建会话前拒绝无效枚举/配置组合, 加载后校验检测 metadata; 后续失败会释放已创建会话. 配置和模型 metadata 对外只读. 同一 pipeline 的预测、Decode 和 Dispose 互斥, Dispose 等待当前调用结束并拒绝后续调用, 可重复释放. 不同实例独立; 同步硬件调用中途不可取消.

两种检测分支都使用 `Predict(DmaFrame)` / `Predict(DmaImage)` 返回独立 `DetectionResult`. DFL 详细例子见 [DFL 检测契约](yolo-detection.md). INT8 检测在会话锁和 SafeHandle 借用期内解码, 原生指针不逃逸, 不每帧生成 float tensor.

## 原始张量与浮点参考

```csharp
// 普通非检测模型也使用同一构造入口; 输出不要求匹配 YOLO.
using var rawPipeline = new RknnPipeline(ModelType.RawTensor, modelPath);
InferenceResult snapshot = rawPipeline.PredictRaw(frame);
```

`RawTensor` 仍要求原生支持的输入契约; 不支持检测 `Predict` / `Decode`, 调用会抛 `NotSupportedException`. 检测 pipeline 也可调用 `PredictRaw` / `PredictRawInto`, 再用该实例的 `Decode(result)` 获取浮点检测参考. `PredictRaw` 每次返回独立 float 快照, 无需 Dispose; 输出已按会话模式转换为 float, 不再次反量化.

```csharp
// 每个模型/并发 worker 独占一组缓冲区, 只分配一次.
Memory<float>[] outputs = pipeline.Outputs
    .Select(info => new float[checked((int)info.ElementCount)].AsMemory()).ToArray();
InferenceResult current = pipeline.PredictRawInto(frame, outputs);
DetectionResult reference = pipeline.Decode(current);
// 消费完 current 及其 Span/Pin/下游引用后, 才可复用 outputs.
current = pipeline.PredictRawInto(nextFrame, outputs);
```

`PredictRawInto` 同时接受 DmaFrame/DmaImage. head 数须精确匹配, 每头长度至少为 ElementCount; 返回 Values 仅引用有效段, 不写未使用尾部. 允许 ArrayPool 大容量和同一数组中的不重叠有效切片, 固定后的实际地址有效段重叠会在硬件提交前拒绝. 自定义 MemoryManager 必须保证 Pin/GetSpan 一致, 不得提供隐蔽物理别名. 所有 memory 必须支持 Pin; 同步返回或异常都会释放取得的 Pin, 不释放调用方 owner.

返回值与调用方缓冲区别名, 下次写入、归还池或释放 owner 会影响旧结果. 消费完成后才可复用, 调用期间集合须不可变且独占, 包括跨模型共享时的同步. 任意失败后全部输出视为不完整. 新 native 直接写入, 旧 ABI2 库可在原生推理后复制, 结果契约相同而性能可能不同.

## 核心、SHA256、调度与日志

```csharp
using var pipeline = new RknnPipeline(ModelType.YoloV5Detection, modelPath, config,
    new RknnModelOptions
    {
        CoreMask = NpuCoreMask.Core0,
        ExpectedModelSha256 = expectedModelSha256,
        OutputMode = FloatOutputMode.PreferFastInt8
    }, logger: logger);
```

基础选项默认 Auto、null SHA256、SdkExact 和无调用线程调度. `Core1`/`Core2` 可显式选择; 多核 mask 不保证缩短一次推理, 库不隐式创建会话池. SHA256 为 null 时完全跳过 hash 读取; 非空须为 64 位十六进制摘要, 大小写均接受, 不匹配会在创建 native 会话前失败. 这不是签名或原子加载, 不证明 anchors、类别或精度. 调用方拥有注入 ILogger/LoggerFactory 的生命周期, 默认使用 NullLogger; 禁用日志不创建消息字符串.

## 显式可选的快速浮点转换

`SdkExact` 使用当前 RKNN SDK 转换, 不承诺不同 SDK 版本之间逐 bit 一致. `PreferFastInt8` 仅影响 `PredictRaw`/`PredictRawInto`: 原生逐 head 核验标准 NCHW、连续 affine INT8、shape/元素数/size、zero point 和有限正 scale 后允许快速转换, 其余 head 仍由 SDK 转换. INT8 `Predict` 不受此选项影响.

快速模式可能因舍入与 SDK 输出不同, 包括 zero point 附近的非零残留, 不保证任意置信度、排序或 NMS IoU 边界处所有检测决策相同. 固定模型误差不是通用上界; 使用前需验证自己的模型和阈值. 旧 native 支持默认模式; 显式快速请求缺少可选配置导出时构造抛 NotSupportedException 并释放会话, 不会静默改回默认.

SDK 浮点参考使用默认 SdkExact 的 `pipeline.Decode(pipeline.PredictRaw(frame))`. INT8 与 SDK 浮点舍入差异使它们不保证 bit exact. DFL 同分 NMS 采用稳定插入顺序, 可能与官方 NumPy 原始同分排序选中不同坐标框. 本次只迁移入口, 不改推理或解码算法; 新入口尚未做 RK3588 实机/精度/性能验收.

## 输入和模型边界

- 运行目标为 Linux ARM64 RK3588; .NET构建主机可使用Windows或Linux, SDK版本由 `global.json` 指定, 目标框架 `net10.0`, 仅依赖 Microsoft.Extensions.Logging.Abstractions. 核心原生运行需要板端 RKNN/RGA 驱动和匹配版本库; GStreamer/MPP 仅为采集示例依赖, 详见 [硬件约束](hardware.md).
- DMA 帧仅支持线性、单 FD、offset=0 的 NV12/RGB888/RGBA8888. `RowStrideBytes` 为字节, NV12 UV 起始于 `RowStrideBytes*HeightStride`. 不支持 AFBC、分离 FD、任意平面 offset 或 CPU byte[] 输入. 描述符验证不证明 FD 的真实有效性; 不支持时抛异常, 无软件回退.
- 借用的 `DmaFrame` 不拥有 FD. 调用方负责生产者同步和直到返回前的存活/排他访问. 保存或复制描述符不延长 sample 生命周期. `DmaImage` 用 `SafeHandle` 管理 owned DMA; `WithFrame` 仅允许同步借用.
- NV12 色域必须明确配置, 默认 `Unspecified` 会被拒绝. 示例可使用 caps 声明或显式 override; 相机 SPS VUI 证明 full range, 未声明色彩矩阵, 历史测试中 BT.601 full 的矩阵部分仅为显式测试假设, 尚无相机色彩基准验证.
- 当前原生输入只接受静态 NHWC RGB INT8、zeroPoint=-128、scale精确等于`float(1/255)`; RGA 生成 signed input, `pass_through=1`. 不支持任意模型归一化或动态 shape.
- `PredictRaw`和`Predict`核对native返回的整数resize、居中padding、静态模型目标尺寸与实际两轴scale, 合同不一致时失败. 有效返回值原样使用; 公开`LetterboxTransform`仍允许独立两轴缩放和一般padding.
- 推理 resize/CSC 要求 RK3588 RGA3, 通过逐操作参数允许两个已验证等价的 RGA3 核并显式使用双线性插值. 缺少或不兼容 RGA3 时失败, 不回退到 RGA2 resize 或 CPU. INT8 bitwise ROP 仍使用独立的硬件操作.
- YOLOv5 helper 仅接受三路 affine INT8 标准 NCHW head, raw logits → sigmoid 一次, 显式 anchors, class-aware NMS. 输出只有 ClassId, 不猜类别名称. 详细模型来源、限制和公式见 [模型契约](model-contract.md).
- owned 图像分配尺寸 1..4096, NV12 宽高为偶数. RGB stride 向 16 像素对齐. 分配 NV12 时必须显式指定色域; RGB 不需要 YUV 色域. 复制的 NV12 源/目标色域须一致. 绘框限制 4096 个、线宽 1..16; NV12 线宽必须为偶数, 坐标外扩至偶数边界. 极小框改用 RGA 填充标记, 单像素边可能外扩为图内 2x2/2xN; 小填充框原点恰为1时还会向左/上扩到0以满足BSP限制. 正常框仍按线宽描边, 同色框以同步 RGA job 分批提交.




