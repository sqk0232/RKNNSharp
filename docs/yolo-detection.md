# RKNN DFL 检测契约（已验证指定 YOLOv8n）

检测公开入口为 `RknnPipeline(ModelType.YoloV8Detection, modelPath, config)`, 配置统一使用 `RknnPipelineConfig`; 本文输入尺寸推断与公开 `RknnModel` 属于0.4.0新增功能, 迁移及验证边界见[0.4 说明](migration-0.4.md). 检测路径包含 INT8 借用输出和有效 profile 绑定缓存, 保留显式解码契约与 native ABI 2; [0.3 迁移历史](migration-0.3.md)保留.

已有硬件验收仅覆盖既有社区YOLOv8n `61d612e0…145b5` 的9输出契约. YOLO11只有已下载ONNX的结构分析和同契约代码, 未转换或板端验证, 不宣称已完成硬件兼容. 库包不包含模型权重. 其他YOLOv8规格、导出选项和输入规范须分别验证.

## 显式配置与调用

下面表示Rockchip优化导出的DFL三尺度格式, 不是按文件名自动识别. 实际模型必须先核对输入输出属性及对应索引.

```csharp
var config = new RknnPipelineConfig(
    classCount: 80,
    heads: [new(0, 1, 2, 8), new(3, 4, 5, 16), new(6, 7, 8, 32)],
    regMax: 16, confidenceThreshold: .25f, nmsThreshold: .45f,
    multiLabel: false, maxDetections: 300);
using var pipeline = new RknnPipeline(ModelType.YoloV8Detection, modelPath, config);
DetectionResult detected = pipeline.Predict(frame);
IReadOnlyList<Detection> detections = detected.Detections;
```

`Predict` 直接借用标准 signed INT8 输出, 类别分数先筛选, 仅反量化候选框所需的 DFL logits. affine 计算为 `(signedValue-zeroPoint)*scale`, softmax 保留减最大值和逐 bin `MathF.Exp` 的浮点顺序. 它使用与 `Decode` 相同的类别、同分顺序、NMS 和坐标恢复规则. `FloatOutputMode` 只配置 `PredictRaw`/`PredictRawInto` 的浮点转换.

Pipeline 构造时校验 metadata 并固定所选解码器. `pipeline.Decode` 仍逐次验证调用方提供的浮点输出.

`PredictRaw` 是非热路径便利 API, 每次分配各输出的独立 float 快照. 热路径使用 `Predict`, 或用 `PredictRawInto` 复用调用方缓冲区.

SDK 浮点参考可继续调用下面的路径. 默认 `SdkExact` 的 `want_float=1` 舍入可能与显式 affine 计算不同, 阈值或 NMS 边界附近的检测需要实际模型比较. 本地回归使用相同 INT8 数据显式反量化后交给 `Decode`, 按检测结果精确比较. 新增 INT8 DFL 路径尚待 RK3588 实机验证.

```csharp
Memory<float>[] buffers = pipeline.Outputs
    .Select(info => new float[checked((int)info.ElementCount)].AsMemory()).ToArray();
InferenceResult raw = pipeline.PredictRawInto(frame, buffers);
IReadOnlyList<Detection> referenceDetections = pipeline.Decode(raw).Detections;
// 消费完本次raw及其Span/Pin/下游引用后, 才可复用buffers.
```

可选 `classNames` 为类别ID顺序的完整标签列表; 省略/空列表不猜标签. 返回值仍为 `Detection.ClassId`, 调用方可用配置的ClassNames查名称. 配置防御复制head/label集合, 不随调用方改数组而变化. 同一模型仍须遵守DMA生产者同步与输入FD生命周期规则.

板端可用 Smoke 单帧参数 `dfl` 跑上面这个九输出示例。它在 codec 与 colorspace 之后，调用 DFL `Predict`，不读取模型文件名。省略时仍是 YOLOv5 `Predict`。

```sh
"$PUBLISH_DIR/RKNNSharp.Smoke" "$MODEL_PATH" --single-frame --file "$VIDEO_PATH" h264 "$COLOR_SPACE" dfl
```

## 支持与拒绝边界

- 原生输入要求仍为单输入、静态batch1、NHWC RGB affineINT8、zeroPoint=-128、scale精确float(1/255), DMA/RGA预处理契约不变. 本层没有放宽native断言.
- 本格式为3个分支、stride8/16/32. 每分支box tensor为 `[1,4*RegMax,H/stride,W/stride]` DFL logits, class tensor为 `[1,ClassCount,H/stride,W/stride]` 已sigmoid类别分数. 可选scoreSum为同尺度单通道tensor. 属性须满足profile实际验证的标准NCHW affineINT8契约; `PredictRawInto`输出已按会话模式转为float, 默认使用SDK转换, decoder不再次反量化或对class重复sigmoid. 显式PreferFastInt8可能改变舍入及阈值附近的决策, 适用限制见[API说明](api.md). 量化反变换可使分数轻微超出[0,1], 本层不据此额外裁剪或重复激活.
- scoreSum映射存在时校验shape/有效数据; 按固定Rockchip官方Python参考口径忽略它, 使用实际class分数阈值. 不声称与使用量化scoreSum预筛的其他后端在所有阈值边界等价.
- 单标签默认取最佳类别; MultiLabel显式开启. 解码使用DFL softmax期望、class-aware NMS、最大框数限制和实际LetterboxTransform恢复/裁剪. 标签、类别数和output mapping均由调用方确认.
- 量化分数并列时, NMS明确采用分支/格点/类别插入顺序的稳定策略. 原官方NumPy `argsort()[::-1]` 的同分顺序不同, 可能选择不同坐标框, 因此不宣称与原官方输出全部等价. 历史验证另用仅固定tie顺序的独立参考在原紧容差下验证; 未扩大坐标容差来掩盖差异. Run/RunInto的SDK输出张量一致性与此后处理策略无关.
- 不自动接受已解码 `[1,4+C,N]`、端到端NMS输出、YOLOv5 anchor logits、不同head数量/布局、姿态/分割/OBB/OCR. 这些需要各自输出契约与decoder, 不能仅换型号名.

上述结论仅适用于本页明确标识的实测模型及其 SDK 输出属性、Run/RunInto 和后处理契约。型号名不等于任意导出版本已验证，tensor/postprocess 一致性不替代有标注数据上的检测精度评估。


