# RKNNSharp.Smoke

Linux ARM64 RK3588 的采集与推理示例。主机上可运行 `--help`，实际推理需 RKNN/RGA、GStreamer/MPP、两个自编译 native 库和自己的兼容模型/媒体输入。准备步骤见[仓库 README](../../README.md#在目标板运行样例)及[源码部署](../../docs/integration.md#从源码使用)。不附模型、凭据、视频或 native 二进制。

## 先运行一帧

```sh
./RKNNSharp.Smoke --help
./RKNNSharp.Smoke "$MODEL_PATH" --single-frame --file "$VIDEO_PATH" h264 "$COLOR_SPACE"
./RKNNSharp.Smoke "$MODEL_PATH" --single-frame --rtsp-file "$RTSP_URL_FILE" h264 "$COLOR_SPACE"
```

变量由调用者设置为板端文件路径和真实色域；不要把完整 RTSP URL 放到命令行。私有文件仅由进程读取，内容为单行完整 URL。单帧入口位于 [SingleFrameSample.cs](SingleFrameSample.cs)，使用实际公开 API：创建 `RknnModel` → 校验 YOLOv5 profile → 采集 → 借用 `DmaFrame` → `Detect` → 输出独立检测结果 → `finally` 释放 sample → 释放 capture/model。

样例采用 `YoloV5Profile.CreateOfficial640Example()` 的官方640 anchors，只有输入输出属性符合还不足以证明 anchors/类别语义正确；须确认自己的模型契约，其他模型使用自己的 profile。单帧不绘框或显示，不保存像素。输出一行 JSON，包含 PTS、尺寸、检测和阶段耗时。按输入节拍读取第一个可用帧，不是低延迟相机应用模板。

`--single-frame` 接受 4 至 6 个参数（不含程序名）：`MODEL --single-frame SOURCE_MODE SOURCE_FILE [codec=h264] [colorspace=auto]`。`SOURCE_MODE` 只允许 `--file` 或 `--rtsp-file`；codec 为 h264/h265；colorspace 为 auto/601limit/601full/709limit。auto 仅接受 caps 明确声明的支持组合，不按分辨率猜测色域。

## 有限帧诊断

```text
MODEL (--file MP4|--file-unpaced MP4|--rtsp-file PRIVATE_URL_FILE)
  [frames=5] [codec=h264] [confidence=.25] [colorspace=auto]
  [cycles=1] [warmup=0] [mode=detect] [core=Auto]
  [sessionsPerCore=1] [inFlight=0] [workers=0]
```

参数是位置参数，要指定后面的项需依次给出前面的项。

| 参数 | 范围/说明 |
| --- | --- |
| frames | 1..300；`--file-unpaced` 为 1..600 |
| cycles / warmup | 1..5 / 0..30 |
| mode | detect：优化 INT8 检测；run：float 输出后解码；compare：同帧比较两条路径；pipeline：有界多会话；replay：有限 DMA 来源重放 |
| core | Auto/Core0/Core1/Core2/All；pipeline/replay 按 worker 显式分配，不使用此参数作调度策略 |
| sessionsPerCore | 1 或 2 |
| workers | 0 自动，或 1/3/6；不能超过 sessionsPerCore×3 |
| inFlight | 0 自动为 workers×2，或 workers..24 |

`--file-unpaced` 只用于 pipeline，frames 必须与自备有限文件总帧数相符。它衡量解码、推理和有序消费的吞吐，不能代替相机网络延迟。诊断模式还会复制到独占 DMA 图像绘框并检查生命周期，不属于最小应用调用开销。详细性能口径见[整合历史记录](../../docs/integration.md#本地验收历史)。

特殊诊断入口：

```text
MODEL --verify-model-hash
MODEL --contract-compare FIXTURE BASELINE_DLL [iterations=12] [EXPECTED_SHA256]
```

这两个入口需要板端环境/自备输入；contract-compare 还依赖自备兼容基线 DLL 与对应依赖，不是首次运行前提。

## 生命周期、退出与错误

采集帧 FD 归 GStreamer 所有，禁止手动 close；必须持有 sample 直到同步 RGA/NPU 调用结束，再执行 `GStreamerCapture.Release`。sample 不能按值复制成多个可释放 owner。`using` 保证模型与采集器释放；同步硬件调用中途不可取消。诊断循环由有限帧数/轮次结束，pipeline 有界排队并在退出时释放借用帧。

退出码：0 为成功（`--help` 也为0）；1 为运行失败或参数错误；2 为缺少基本调用参数。通用错误仅输出异常类型，避免意外记录凭据；不输出完整参数、私有文件内容或源 URL。排错时先核对输入和[部署前置条件](../../docs/integration.md#从源码使用)，不要以开启软件解码回退规避不支持的布局。

本次文档和入口修改只做主机验证；没有执行新一轮板端推理、媒体采集、精度或性能验证。
