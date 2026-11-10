# 0.3.0 → 0.4.0迁移

本说明描述0.4.0源码与API. 0.3.0 的已发布行为仍见 [0.3 迁移历史](migration-0.3.md). 原有 `RknnPipeline` 构造签名、ModelType 枚举值、显式尺寸构造和 DMA 所有权契约保留. 目标框架仍为 net10.0 / C# 14, 硬件运行目标为经过各自验收的 Linux ARM64 / RK3588 组合.

## 输入与检测配置

原有 `new RknnPipelineConfig(640, 640, 80, anchors: ...)` 可继续使用, 显式尺寸须与实际输入一致. 新增 `new RknnPipelineConfig(classCount: 80, anchors: ...)` 和 `heads` 形式, 从模型读取宽高及 batch. 未绑定配置的尺寸和 batch 为 0, InputTensor 为 null; 静态 pipeline.Config 是绑定后的独立配置, 动态模型保留原配置并在每次执行时绑定实际形状.

`InputWidth`、`InputHeight`、`BatchSize`、`InputTensor` 与 `Inputs` / `Outputs` 提供构造时 metadata. 新前处理选项默认 ChannelOrder.Rgb、ResizeMode.Letterbox、NormalizationMode.ModelDefault. 当前仍只执行原有 RGB、居中 RGB114 letterbox、affine INT8 路径; Bgr、Stretch、None、DivideBy255 明确拒绝, 不增加额外归一化或软件回退.

## 固定会话池与原始张量

在原有 pipeline 构造参数 options 中设置 `new RknnModelOptions { SessionPoolSize = 3 }`. 默认 1 保持顺序调用方式; 正整数池大小在构造时建立全部会话, 不让顺序代码自动并行, 所有槽沿用同一 CoreMask. 多实例分别配置池, 调用方负责输入 DMA 就绪/存活及各并发调用的独占输出缓冲.

首个会话加载模型, 后续复制上下文共享权重, 输入/输出资源独立. 旧 ABI2 桥接若缺少 rks_dup_context, 构造时独立创建其余会话并通过可选日志记一次警告, 内存占用更高; 真实复制失败使构造失败并清理已建会话. 池用尽同步等待, 没有超时或取消参数. Dispose 拒绝新调用和排队调用, 等全部在途调用归还后逆序释放, 可重复调用.

新增公开 `new RknnModel(modelPath)` / `(modelPath, options)`, 通过 Run / RunInto 取得原始张量. 这是本版本新增的公开入口, 不恢复 0.2.0 的检测 profile API; 检测继续使用 RknnPipeline.

## 动态形状和输出语义

Predict / PredictRaw / PredictRawInto 以及 RknnModel.Run / RunInto 新增显式 NHWC 形状重载, DmaFrame 与 DmaImage 均可使用. 例如 `pipeline.PredictRaw(frame, new uint[] { 1, 480, 480, 3 })`. 必须先确认模型声明支持该形状. 上下文复用; 输入 DMA 与输出绑定可随形状重新准备. 缺少形状导出时明确失败, 不创建替代上下文.

构造时 metadata 不随并发调用回写. 每个 InferenceResult.Inputs 和输出 Info 携带本次属性快照, Decode 按该结果的实际尺寸绑定. Into 缓冲须按请求形状的输出预先准备且各调用独占; 任意失败后视全部输出为不完整. 未指定形状时使用构造时形状, 未解析动态维需显式给出具体形状.

每次执行仍要求 batch=1、NHWC RGB affine INT8、zeroPoint=-128、scale 精确 float(1/255) 及原有 DMA/stride 条件. ABI 仍为 2, 新桥接增加三个可选导出: rks_dup_context、rks_is_dynamic、rks_set_input_shapes. 使用与本版本配套的桥接; 不能将动态加载成功等同于任意输入契约可运行.

V5 内置检测仍接受三头 raw logits, 不根据数值范围猜语义. 已有动态 model-zoo yolov5s_relu 是概率输出, 应选择 RawTensor 并由应用显式解码概率. 应用侧捕获/后处理合同已修正; 不据此改变产品默认 logits 或宣称该模型内置 Predict 可用. 同分 NMS 顺序须由调用方定义并验证.

## 验证边界与回退

官方0.3.0和候选的真实包已在同一Runtime2.3.2/RGA1.10.1_[3]上对指定静态V5/V8n及固定512×384 bus帧作有限对照: Raw/Into与旧新3/9头hash一致, Transform一致, 均4 person/1 bus; 每模型每版本3×300正式性能样本未观察到一致回退. 候选动态概率模型640/480/320、池1/3的Raw/Into、180次并发全头/metadata/Transform独立快照对照、512拒绝后640恢复、5次池3建销均通过有限板验. 候选V8n同一进程590.001秒内34598次Predict满足检测类别/数量、有限分数及有效框门槛并正常退出. pipeline/fixture释放后DMA分别为1/0; `/dev/dri/card0`和`/dev/rga`两个设备句柄与0.3.0同阶段一致, 有限循环未新增累积, 仍持有至进程退出, 具体归属层未唯一确认. 本任务退出后占用已核查释放, 不据此宣称全部原生资源归零、无泄漏或长期稳定性.

RGA自然/合成三路内容像素零差, 源DMA、padding和signed/XOR检查一致. 相对理想half-pixel的差异仍在, 精确vendor数学合同未匹配; 同Runtime旧新实际包的输出/几何一致未发现新增包装/版本回归, 不因理想参考差异单独判定新增产品缺陷, 也不宣称完整像素精度通过. 缺独立GT标注, mAP、任意模型/BSP和长期稳定性未验. 本版本已完成上述指定范围的有限技术验收; 正式发布须使用通过验收的确定提交、对应CI及重建资产; 条件见[候选验收](../README.md#040-有限验证). 本机托管测试不替代板端验收.

回退到 0.3.0 时一并恢复包、配套桥接和应用源码: 移除新增池/shape/前处理选项及 RknnModel 公共入口, 检测恢复显式尺寸配置. 不混用不同版本托管和原生资产. 0.3.0 已发布版本不重发; 正式 0.4.0 发布以通过验收门槛的确定提交和重建资产为准.
