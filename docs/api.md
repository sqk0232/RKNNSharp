# API 与输入契约

## 统一构造与预测

检测构造入口为 `RknnPipeline(ModelType, modelPath, config?, options?, logger?)`；另公开 `RknnModel(modelPath)` / `(modelPath, options)` 的原始张量入口及Run/RunInto重载。检测profile保持内部实现，native ABI继续为2。

`ModelType.YoloV5Detection` 要求显式 anchors; `YoloV8Detection` 目前仅支持指定 RKNN 三尺度 DFL 导出契约, 要求显式 heads/RegMax. 不保证任意 YOLOv8 导出兼容, 也不表示已支持 YOLO11. 检测 config 必须提供且匹配枚举; 普通原始张量模型使用 `RawTensor`, 不传检测 config.

```csharp
using RKNNSharp;

// 官方 640 anchors 仅为示例假设, 必须确认与自己的模型一致.
var config = new RknnPipelineConfig(classCount: 80, anchors:
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

配置构造会复制 anchors、heads、标签. 免填尺寸构造 `RknnPipelineConfig(classCount, anchors: …)` 或 `RknnPipelineConfig(classCount, heads: …)` 在加载后以模型尺寸完整校验类别、阈值、RegMax 与映射. DFL 索引须唯一且连续覆盖全部输出. 原构造 `RknnPipelineConfig(inputWidth, inputHeight, classCount, …)` 继续可用, 显式尺寸须与模型一致, 异常同时列出配置值和模型值. 构造失败会释放已创建会话.

`pipeline.InputWidth` / `InputHeight` / `BatchSize` 来自输入 TensorInfo, `InputTensor` 包含模型读出的 Dimensions、Layout、DataType、Quantization、ZeroPoint、Scale、Name. `pipeline.Config` 为模型绑定后的独立配置. 原始免尺寸配置的宽高及 batch 为 0, InputTensor 为 null, 可供不同模型实例复用. `RknnModel` 同样提供这些模型属性.

可手设 `ChannelOrder`、`ResizeMode`、`Normalization`, 默认分别为 `ChannelOrder.Rgb`、`ResizeMode.Letterbox`、`NormalizationMode.ModelDefault`. 默认直接使用现有 RGA RGB、居中 RGB 114 letterbox 及 INT8 编码路径, 不重复均值/方差归一化. Bgr、Stretch、None 和 DivideBy255 尚无对应原生路径, 在创建会话前抛 NotSupportedException. RawTensor 使用相同默认预处理和输入限制. 静态模型无需动态宽高或最大 batch 配置. 新增原生源码允许动态模型先加载并建池, 实际运行仍要求具体 batch=1、NHWC RGB affine INT8 输入; FP16/NCHW 等未实现路径会明确失败并列出实际属性.

配置和构造时的模型 metadata 对外只读. 同一 pipeline 的预测可并发借用不同会话, 每个会话同一时刻仅供一次调用使用. Dispose 等待在途调用后逐个释放全部上下文并拒绝后续调用, 可重复释放. 同步硬件调用中途不可取消.

两种检测分支都使用 `Predict(DmaFrame)` / `Predict(DmaImage)` 返回独立 `DetectionResult`. DFL 详细例子见 [DFL 检测契约](yolo-detection.md). INT8 检测在会话锁和 SafeHandle 借用期内解码, 原生指针不逃逸, 不每帧生成 float tensor.

## 会话池（SessionPoolSize）

`RknnModelOptions.SessionPoolSize` 默认 **1**. 构造时一次建好 N 个原生会话, 推理只借还已有会话, 不按帧或形状重建上下文, 避免反复创建的内存与时延开销并提高并发吞吐. 低帧率、单线程顺序调用保留 1 节省内存; 单路高帧率、多路相机, 或一帧多个模型/多个调用并发时可手动设为 **3**, 不需要多路图像源才有用. RK3588 有 3 个 NPU 核, 3 是常用调优上限, 并非 API 硬上限. 每个模型独立配置池; 池大小不会自动并行顺序代码, 所有会话沿用同一 CoreMask, 不自动分核.

沿用上面的 `config` 和 `modelPath`, 配置通过构造参数传入. SessionPoolSize 属于 `RknnModelOptions`, 不属于 `RknnPipelineConfig`:

```csharp
var options = new RknnModelOptions { SessionPoolSize = 3 };
using var pipeline = new RknnPipeline(
    ModelType.YoloV5Detection, modelPath, config, options);
```

`RknnModel(modelPath, options)` 同样接受该配置. 允许正 int, 即 1..int.MaxValue; 小于 1 时在创建原生会话前抛出 `ArgumentOutOfRangeException`. 没有额外的软件上限, 仍受设备资源限制, 创建失败会清理已建会话.

主会话通过 `rks_create` 加载, 其余槽经 `rks_dup_context` 调用 `rknn_dup_context` 复制上下文并共享权重. 每个会话的输入输出缓冲互相隔离, 可以并发; 调用方仍须保持输入 DMA 的生产者同步和存活, 并为并发调用提供独占输出缓冲. 池用尽时调用方阻塞等待归还, 没有超时或取消参数.

`Dispose` 开始后拒绝新调用并唤醒排队调用, 两者借用时抛出 `ObjectDisposedException`; 等在途推理全部归还后, 按构造逆序销毁全部会话, 复制会话先于主会话. 每个会话只销毁一次, 重复释放安全. 旧 `librknnsharp_native.so` 缺少 `rks_dup_context` 导出时, 只在构造期逐个独立创建其余会话, 不共享权重、内存更高, 通过 `options.LoggerFactory` 在启用 Warning 日志时记录一次警告, 默认空日志不输出. 推理期仍不重建. 真实复制错误会使构造失败并清理已建会话.

静态板测条件: RK3588、RKNN Runtime 2.3.2、驱动 0.9.8, YOLOv5s / YOLOv8n INT8, 复用单张 512×384 RGB888 图片, 模型输入 640×640. 性能数据未挂载诊断 LD_PRELOAD, 每组预热 100 + 正式 300 帧 × 3 轮, 合并 900 正式样本. FPS 为总帧数/总用时; 单帧均值为每次调用耗时, 包含预处理、推理和检测后处理, 不含采集/解码等待与模型构造.

| 模型 | 池 1 / 单线程 FPS | 单帧均值 ms | 池 3 / 三线程 FPS | 单帧均值 ms |
| --- | ---: | ---: | ---: | ---: |
| YOLOv5s INT8 | 50.82 | 19.67 | 138.72 | 21.57 |
| YOLOv8n INT8 | 52.34 | 19.10 | 148.00 | 20.18 |

并行吞吐不等于单请求延迟降低, 不能把总 FPS 的倒数当作延迟; 轮间存在波动, 其他模型/设备需重测. 同一设备、Runtime、驱动及静态模型的另组验证中, 池 3 共享权重相对逐会话独立创建, 按 dma-buf 统计节省 DMA 内存约 16.2 MiB (YOLOv5s) / 6.5 MiB (YOLOv8n); 池 1/3、单线程/多线程共 1210 次结果比较全部一致; 150 次创建/推理/释放循环后, 原生上下文与 DMA 内存不累积. 这些有限测试不代表动态维或长期运行均通过.

## 动态形状

动态调用显式传入本次 NHWC 形状, 例如 `pipeline.PredictRaw(frame, new uint[] { 1, 640, 640, 3 })`. `Predict`、`PredictRawInto` 及 DmaImage 重载也接受形状, `RknnModel.Run` / `RunInto` 同样支持. 借出会话后, `rks_set_input_shapes` 封装官方 `rknn_set_input_shapes`, 刷新当前输入/输出属性并按原有严格契约准备 DMA. 相同成功形状复用已有绑定. 形状变化可能重新分配该会话的输入 DMA 和输出缓冲区, 上下文始终复用. 缺少设置形状导出会明确失败. 未指定形状时使用构造时读到的形状, 未解析的动态维须显式传形状.

`Inputs`、`Outputs`、尺寸属性保留构造时快照. 动态模型的检测配置在执行时按本次具体形状绑定, `pipeline.Config` 保留原配置. `InferenceResult.Inputs` 和每个输出的 `Info` 携带本次实际属性, 可供并发结果解码使用. 动态 `PredictRawInto` 的缓冲区须按请求形状的实际输出预先准备, 可先用独立快照查询该形状输出. 每个并发调用独占自己的输出缓冲区. 显式配置宽高仍须匹配实际执行形状.

V5内置检测要求raw logits。动态model-zoo yolov5s_relu概率头使用RawTensor和应用显式概率解码，不改变产品默认语义；有限验证不代表mAP、任意模型或长期稳定性。

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

基础选项默认 Auto、null SHA256、SdkExact 和无调用线程调度. `Core1`/`Core2` 可显式选择; 多核 mask 不保证缩短一次推理, 构造时建立固定会话池, 默认 1 个, 可通过可选高级项 SessionPoolSize 设置正整数. 所有池槽沿用相同 CoreMask, 不自动分核. SHA256 为 null 时完全跳过 hash 读取; 非空须为 64 位十六进制摘要, 大小写均接受, 不匹配会在创建 native 会话前失败. 这不是签名或原子加载, 不证明 anchors、类别或精度. 调用方拥有注入 ILogger/LoggerFactory 的生命周期, 默认使用 NullLogger; 禁用日志不创建消息字符串.

## 显式可选的快速浮点转换

`SdkExact` 使用当前 RKNN SDK 转换, 不承诺不同 SDK 版本之间逐 bit 一致. `PreferFastInt8` 仅影响 `PredictRaw`/`PredictRawInto`: 原生逐 head 核验标准 NCHW、连续 affine INT8、shape/元素数/size、zero point 和有限正 scale 后允许快速转换, 其余 head 仍由 SDK 转换. INT8 `Predict` 不受此选项影响.

快速模式可能因舍入与 SDK 输出不同, 包括 zero point 附近的非零残留, 不保证任意置信度、排序或 NMS IoU 边界处所有检测决策相同. 固定模型误差不是通用上界; 使用前需验证自己的模型和阈值. 旧 native 支持默认模式; 显式快速请求缺少可选配置导出时构造抛 NotSupportedException 并释放会话, 不会静默改回默认.

SDK浮点参考使用默认SdkExact的 `pipeline.Decode(pipeline.PredictRaw(frame))`。INT8与SDK浮点舍入不保证bit exact。DFL同分NMS保留稳定插入顺序，调用方须验证阈值附近及同分选择。

## 输入和模型边界

- 运行目标为 Linux ARM64 RK3588; .NET构建主机可使用Windows或Linux, SDK版本由 `global.json` 指定, 目标框架 `net10.0`, 仅依赖 Microsoft.Extensions.Logging.Abstractions. 核心原生运行需要板端 RKNN/RGA 驱动和匹配版本库; GStreamer/MPP 仅为采集示例依赖, 详见 [硬件约束](hardware.md).
- DMA 帧仅支持线性、单 FD、offset=0 的 NV12/RGB888/RGBA8888. `RowStrideBytes` 为字节, NV12 UV 起始于 `RowStrideBytes*HeightStride`. 不支持 AFBC、分离 FD、任意平面 offset 或 CPU byte[] 输入. 描述符验证不证明 FD 的真实有效性; 不支持时抛异常, 无软件回退.
- 借用的 `DmaFrame` 不拥有 FD. 调用方负责生产者同步和直到返回前的存活/排他访问. 保存或复制描述符不延长 sample 生命周期. `DmaImage` 用 `SafeHandle` 管理 owned DMA; `WithFrame` 仅允许同步借用.
- NV12 色域必须明确配置, 默认 `Unspecified` 会被拒绝. 示例可使用 caps 声明或显式 override; 相机 SPS VUI 证明 full range, 未声明色彩矩阵, 历史测试中 BT.601 full 的矩阵部分仅为显式测试假设, 尚无相机色彩基准验证.
- 当前原生输入只接受静态 NHWC RGB INT8、zeroPoint=-128、scale精确等于`float(1/255)`; RGA 生成 signed input, `pass_through=1`. 动态模型每次执行同样校验当前形状的该契约, 不支持任意模型归一化.
- `PredictRaw`和`Predict`核对native返回的整数resize、居中padding、静态模型目标尺寸与实际两轴scale, 合同不一致时失败. 有效返回值原样使用; 公开`LetterboxTransform`仍允许独立两轴缩放和一般padding.
- 推理 resize/CSC 要求 RK3588 RGA3, 通过逐操作参数允许两个已验证等价的 RGA3 核并显式使用双线性插值. 缺少或不兼容 RGA3 时失败, 不回退到 RGA2 resize 或 CPU. INT8 bitwise ROP 仍使用独立的硬件操作.
- YOLOv5 helper 仅接受三路 affine INT8 标准 NCHW head, raw logits → sigmoid 一次, 显式 anchors, class-aware NMS. 输出只有 ClassId, 不猜类别名称. 详细模型来源、限制和公式见 [模型契约](model-contract.md).
- owned 图像分配尺寸 1..4096, NV12 宽高为偶数. RGB stride 向 16 像素对齐. 分配 NV12 时必须显式指定色域; RGB 不需要 YUV 色域. 复制的 NV12 源/目标色域须一致. 绘框限制 4096 个、线宽 1..16; NV12 线宽必须为偶数, 坐标外扩至偶数边界. 极小框改用 RGA 填充标记, 单像素边可能外扩为图内 2x2/2xN; 小填充框原点恰为1时还会向左/上扩到0以满足BSP限制. 正常框仍按线宽描边, 同色框以同步 RGA job 分批提交.




