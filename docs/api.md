# API 与输入契约

## 调用

```csharp
using RKNNSharp;

using var model = new RknnModel(modelPath); // 调用方提供模型文件路径.
// 仅示例: 官方默认 anchors 未从本模型恢复, 生产调用应传入已确认配置.
var profile = YoloV5Profile.CreateOfficial640Example();

// 输入来自你的采集层. 完整持有 sample/FD, 且生产者写入完成后才调用.
var frame = new DmaFrame(
    FileDescriptor: dmaFd, Width: 2560, Height: 1440,
    Format: PixelFormat.Nv12, RowStrideBytes: 2560, HeightStride: 1440,
    SizeBytes: allocationBytes, ColorSpace: ColorSpace.Bt601Full);

DetectionResult result = model.Detect(frame, profile);
foreach (Detection item in result.Detections)
{
    Console.WriteLine($"{item.ClassId}: {item.Confidence:F3} {item.Box}");
}

using var annotated = DmaImage.Allocate(frame.Width, frame.Height);
annotated.CopyFrom(frame);           // RGA DMA copy/CSC
annotated.DrawRectangles(result.Detections); // RGA, owned writable buffer
annotated.WithFrame(output =>
{
    // 同步交给支持 DMA 的下游; 回调结束后不得保留描述符.
    return 0;
});
// 现在才可释放输入 sample/FD.
```

`model.Run(frame)` 返回独立的 float 输出 tensor、实际 letterbox transform 和硬件耗时, 可实现其他模型专用后处理. 输出已按会话模式转换为 float, 不再次反量化; 现有构造默认使用SDK转换. 同一模型实例的 `Run`/`Dispose` 串行, 不同实例独立. 调用是同步的, 硬件调用中途不可取消.

### 复用 float 输出缓冲区

`RunInto(DmaFrame, IReadOnlyList<Memory<float>>)` 和对应的 `DmaImage` 重载允许调用方复用输出存储, 返回类型仍为 `InferenceResult`. `Run` 继续返回每次独立、长期有效的快照, 无需 Dispose.

```csharp
// 每个模型/并发worker独占一组缓冲区; 此处只分配一次.
Memory<float>[] outputs = model.Outputs
    .Select(info => new float[checked((int)info.ElementCount)].AsMemory()).ToArray();

InferenceResult current = model.RunInto(frame, outputs);
var detections = profile.Decode(current.Outputs, current.Transform);
// 必须消费完current（及其Span/Pin/下游引用）后, 才能用outputs运行下一帧.
current = model.RunInto(nextFrame, outputs); // 覆盖上一次Into结果的Values.
```

缓冲区head数须精确匹配, 每头长度至少为 `ElementCount`; 返回 `Values` 只引用精确有效段, 未使用尾部不写入. 允许ArrayPool大容量和同一数组中的不重叠有效切片, 固定后实际地址的有效区间重叠会在硬件提交前拒绝. 自定义MemoryManager必须保证Pin/GetSpan一致, 调用方不得提供不同虚拟映射指向同一物理存储的隐蔽物理别名. 所有memory必须支持Pin; 全部pin成功并检查地址/范围后才执行推理, 每次同步返回或异常都会释放已取得的pin, 不释放调用方的owner.

`RunInto` 的返回值与调用方缓冲区别名: 下次写入、归还ArrayPool或释放MemoryOwner会影响旧Into结果, 因此消费完本次结果后再复用/归还/释放. 调用期间缓冲集合不可变且须独占, 包括跨模型并发共享时的同步. 任意失败时不返回结果, 全部输出内容视为不完整; 后续成功调用可重新覆盖恢复. `Run`/`RunInto`/`Detect`/`Dispose` 在同一会话内互斥. 新native使用直接写入; 旧ABI2库可退回原生Run后复制到同一调用方缓冲区, 结果契约相同但性能可能不同.

### 显式可选的快速浮点转换

```csharp
using var model = new RknnModel(modelPath, NpuCoreMask.Core0,
    expectedModelSha256: null, outputMode: FloatOutputMode.PreferFastInt8);
```

旧的两个构造签名及默认值保持不变, 均使用 `FloatOutputMode.SdkExact`. 仅新增四参重载, 避免与原第三参SHA256的 `default` 字面量调用产生歧义. `OutputMode` 返回会话请求的策略. `SdkExact` 使用当前RKNN SDK的转换, 不承诺不同SDK版本之间逐bit一致.

`PreferFastInt8` 只影响 `Run`/`RunInto`. 原生逐head核验标准NCHW、连续affine INT8、shape/元素数/size一致、合法zero point和有限正scale后可用快速仿射转换; 不满足条件的head仍由SDK转换. 这是一项允许快速转换的请求, 不保证所有head均走快速路径. `Detect`仍使用原有INT8路径, 快照、调用方slice/Pin、异常和会话并发契约不变.

快速模式可能因浮点舍入而与SDK输出不同, 包括SDK在量化zero point附近的非零残留; 不保证逐bit一致, 不保证任意置信度、分数排序或NMS IoU边界处所有检测决策相同. 本次固定模型/输入的误差记录不能作为任意模型/scale的通用上界, 不宣称普遍只差几个ULP. 对精度敏感的模型与阈值应分别验证; 需要SDK浮点参考时保留 `SdkExact` 会话. 新native保留ABI2并增加可选配置导出; 旧native继续支持默认模式, 显式快速请求缺少导出时构造抛 `NotSupportedException` 并释放已创建会话, 不会静默改回默认.

`model.Detect(frame, profile)` 使用原生预分配的标准 INT8 输出, 在会话锁和 SafeHandle 借用期内完成 sigmoid 查表/NMS, 只返回独立检测结果, 不每帧分配 float tensor. 三个 head 各用自己的量化参数; signed INT8 不含 NaN/Inf, 查表构建时验证全部可能的反量化值. `profile.Decode` 的浮点输出 finite 校验仍完整保留. 两条路径使用相同阈值和几何公式, runtime 浮点舍入差使其不保证 bit exact; 需要浮点基线时用默认SDK会话调用 `var raw = model.Run(frame); profile.Decode(raw.Outputs, raw.Transform);`.

构造函数可显式指定 `new RknnModel(modelPath, NpuCoreMask.Core0)`、`Core1` 或 `Core2`; 默认 `Auto`. 设置多核 mask 不保证缩短本模型的一次推理, 库不隐式创建实例池.

原有构造函数签名保留. 需要加载前内容预检时使用 `new RknnModel(modelPath, NpuCoreMask.Auto, expectedModelSha256)`: 第三参为null时不读hash, 否则须为64位十六进制SHA256, 大小写均接受; 不匹配会在创建native会话前失败. 这是一次启动期预检, 不是签名或原子加载, 不证明anchors、类别或精度.

RKNN三尺度DFL检测使用新增的 `YoloDetectionConfig` / `YoloDetectionProfile`, 在 `RunInto` 后解码明确的输出契约. 本轮仅已板验指定的YOLOv8n RKNN模型; YOLO11未板验, 不计入兼容验收. 不按型号猜测导出格式; 配置示例、输入输出契约与不支持项见 [YOLO检测兼容层](yolo-detection.md). 现有YOLOv5 `Detect` 路径保持不变.

新解码器对量化同分NMS采用稳定选择顺序, 可能与官方NumPy原同分排序选中不同坐标框; 不宣称两者所有输出全等价. 独立参考核验明确区分原官方和固定稳定tie策略. 默认SdkExact的Run/RunInto仍保留SDK浮点参考; 显式PreferFastInt8的舍入与边界限制见上文.

## 输入和模型边界

- 运行目标为 Linux ARM64 RK3588; .NET构建主机可使用Windows或Linux, SDK版本由 `global.json` 指定, 目标框架 `net10.0`, 无第三方托管运行依赖. 核心原生运行需要板端 RKNN/RGA 驱动和匹配版本库; GStreamer/MPP 仅为采集示例依赖, 详见 [硬件约束](hardware.md).
- DMA 帧仅支持线性、单 FD、offset=0 的 NV12/RGB888/RGBA8888. `RowStrideBytes` 为字节, NV12 UV 起始于 `RowStrideBytes*HeightStride`. 不支持 AFBC、分离 FD、任意平面 offset 或 CPU byte[] 输入. 描述符验证不证明 FD 的真实有效性; 不支持时抛异常, 无软件回退.
- 借用的 `DmaFrame` 不拥有 FD. 调用方负责生产者同步和直到返回前的存活/排他访问. 保存或复制描述符不延长 sample 生命周期. `DmaImage` 用 `SafeHandle` 管理 owned DMA; `WithFrame` 仅允许同步借用.
- NV12 色域必须明确配置, 默认 `Unspecified` 会被拒绝. 示例可使用 caps 声明或显式 override; 相机 SPS VUI 证明 full range, 未声明色彩矩阵, 本次 BT.601 full 的矩阵部分仅为显式测试假设, 尚无相机色彩基准验证.
- 当前原生输入只接受静态 NHWC RGB INT8、zeroPoint=-128、scale精确等于`float(1/255)`; RGA 生成 signed input, `pass_through=1`. 不支持任意模型归一化或动态 shape.
- `Run`和`Detect`核对native返回的整数resize、居中padding、静态模型目标尺寸与实际两轴scale, 合同不一致时失败. 有效返回值原样使用; 公开`LetterboxTransform`仍允许独立两轴缩放和一般padding.
- 推理 resize/CSC 要求 RK3588 RGA3, 通过逐操作参数允许两个已验证等价的 RGA3 核并显式使用双线性插值. 缺少或不兼容 RGA3 时失败, 不回退到 RGA2 resize 或 CPU. INT8 bitwise ROP 仍使用独立的硬件操作.
- YOLOv5 helper 仅接受三路 affine INT8 标准 NCHW head, raw logits → sigmoid 一次, 显式 anchors, class-aware NMS. 输出只有 ClassId, 不猜类别名称. 详细模型来源、限制和公式见 [模型契约](model-contract.md).
- owned 图像分配尺寸 1..4096, NV12 宽高为偶数. RGB stride 向 16 像素对齐. 分配 NV12 时必须显式指定色域; RGB 不需要 YUV 色域. 复制的 NV12 源/目标色域须一致. 绘框限制 4096 个、线宽 1..16; NV12 线宽必须为偶数, 坐标外扩至偶数边界. 极小框改用 RGA 填充标记, 单像素边可能外扩为图内 2x2/2xN; 小填充框原点恰为1时还会向左/上扩到0以满足BSP限制. 正常框仍按线宽描边, 同色框以同步 RGA job 分批提交.




