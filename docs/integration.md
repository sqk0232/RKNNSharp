# 整合与验收

## 从源码使用

本节是从源码构建与运行的操作入口。后文仅保留特定模型、设备和软件组合的历史测量与限制；原始私有输入、开发日志和旧版二进制不随源码提供。本项目没有承诺预编译 Release、SDK、模型或测试视频下载。

| 环节 | 需要自行准备 |
|---|---|
| .NET构建主机 | Windows或Linux, 安装`global.json`要求的.NET SDK10.0.403或其允许补丁; 首次还原需访问NuGet包源 |
| 实际运行目标 | Linux ARM64 RK3588及兼容BSP, 可用NPU/RGA3、DMA32设备和访问权限; Windows/x64主机不能运行此硬件链路 |
| 核心native构建 | 在目标板使用C++17编译器、Linux开发头、CMake≥3.16; 匹配的RKNN SDK头文件及ARM64`librknnrt.so`, RGA头文件及库 |
| 采集示例额外依赖 | C11编译器、pkg-config、GStreamer≥1.20开发模块和运行插件, BSP提供的DMABUF输出`mppvideodec`; 见[媒体构建](gstreamer.md#从源码构建与首次检查) |
| 调用方资源 | 符合[模型契约](model-contract.md)的RKNN文件、已确认anchors/类别配置及DMA采集来源; 示例可用自备带时间戳H.264/H.265 MP4或私有RTSP URL文件 |

RKNN/RGA由板卡BSP供应商提供或依据[硬件依赖来源](hardware.md#从源码构建)准备, 本仓库不附这些系统组件. 实测版本是特定兼容组合, 不保证任意新旧SDK/驱动互换. 没有匹配BSP、模型或媒体输入时, 仅下载源码不足以运行硬件推理.

### 1. 发布托管代码

在源码根目录运行PowerShell命令, 输出目录由调用者输入, `dotnet publish`负责创建. 只引用核心库:

```powershell
$PUBLISH_DIR = Read-Host '请输入你选择的发布输出目录（绝对路径）'
if ([string]::IsNullOrWhiteSpace($PUBLISH_DIR)) { throw '发布输出目录不能为空' }
dotnet publish src/RKNNSharp/RKNNSharp.csproj -c Release -o "$PUBLISH_DIR"
```

输出包含`RKNNSharp.dll`和`RKNNSharp.xml`（项目已启用XML文档）. 应用可引用这两个文件, 或项目引用该csproj; 应用目标net10.0. 运行采集示例则发布:

```powershell
dotnet publish samples/RKNNSharp.Smoke/RKNNSharp.Smoke.csproj -c Release -r linux-arm64 --self-contained true -o "$PUBLISH_DIR"
```

后者带.NET运行时, 不带RKNN/RGA/MPP等系统依赖. 将整个发布目录传至目标板自选位置. 后续Bash中的`PUBLISH_DIR`指板端副本, 不能把Windows主机的路径直接用于Linux.

### 2. 从源码生成并放置native库

将源码放在目标板, 从源码根目录按[核心native构建步骤](hardware.md#从源码构建)生成`librknnsharp_native.so`; 需要采集时按[媒体构建步骤](gstreamer.md#从源码构建与首次检查)生成`librknnsharp_gst.so`. 构建位置`NATIVE_BUILD_DIR`/`GST_BUILD_DIR`均由调用者选择、由CMake创建, 不是下载自带的目录. 本仓库忽略`*.so`和构建输出, 因而必须执行这些源码步骤.

板端配置、构建、复制与运行请在同一个Bash会话中依次进行, 保留各变量. SDK目录/库文件、构建输出和发布目录均输入绝对路径, 避免切换工作目录后变量或动态库路径失效.

在板端配置发布副本位置并放置库（只用核心时跳过第二条cp）:

```sh
read -r -p '板端发布目录（绝对路径）: ' PUBLISH_DIR
: "${PUBLISH_DIR:?请先提供已传入的发布目录}"
cp "$NATIVE_BUILD_DIR/librknnsharp_native.so" "$PUBLISH_DIR/"
cp "$GST_BUILD_DIR/librknnsharp_gst.so" "$PUBLISH_DIR/"
RKNN_RUNTIME_DIR="$(dirname -- "$RKNN_LIBRARY")"
export LD_LIBRARY_PATH="$PUBLISH_DIR:$RKNN_RUNTIME_DIR${RGA_RUNTIME_DIR:+:$RGA_RUNTIME_DIR}${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
```

`RKNN_LIBRARY`是前一步配置的目标板runtime文件; 若RGA也不在系统搜索路径, 为`RGA_RUNTIME_DIR`指定其所在目录. 不复制开发者机器的RUNPATH; 移机后依赖必须在目标板可解析. 可用`ldd "$PUBLISH_DIR/librknnsharp_native.so"`及采集库的ldd检查有无`not found`. 不替换系统驱动来绕过不兼容.

### 3. 用自己的来源首次运行

设置`MODEL_PATH`为自己的兼容RKNN文件, `VIDEO_PATH`为自己的MP4, `CODEC`为h264或h265, `COLOR_SPACE`为auto/601limit/601full/709limit. auto要求caps明确声明支持的组合; 未声明时由调用者确认真实矩阵/范围, 不根据分辨率猜测. 私有URL文件变量为`RTSP_URL_FILE`, 内容仅由进程内部读取, 不放到命令行或日志.

```sh
read -r -p 'RKNN模型文件（绝对路径）: ' MODEL_PATH
read -r -p '编码h264/h265（空值使用h264）: ' CODEC
CODEC="${CODEC:-h264}"
read -r -p '色域auto/601limit/601full/709limit: ' COLOR_SPACE
: "${MODEL_PATH:?未设置模型文件}"
: "${COLOR_SPACE:?请配置来源颜色信息}"
chmod +x "$PUBLISH_DIR/RKNNSharp.Smoke"
```

文件来源:

```sh
read -r -p '带时间戳MP4文件（绝对路径）: ' VIDEO_PATH
: "${VIDEO_PATH:?未设置视频文件}"
"$PUBLISH_DIR/RKNNSharp.Smoke" "$MODEL_PATH" --file "$VIDEO_PATH" 5 "$CODEC" .25 "$COLOR_SPACE"
```

或选择RTSP来源:

```sh
read -r -p '私有URL文件（绝对路径）: ' RTSP_URL_FILE
: "${RTSP_URL_FILE:?未设置私有URL文件}"
"$PUBLISH_DIR/RKNNSharp.Smoke" "$MODEL_PATH" --rtsp-file "$RTSP_URL_FILE" 5 "$CODEC" .25 "$COLOR_SPACE"
```

默认先做有限帧同步调用; 不需要旧版DLL、历史600帧片段或预编译包. `--file-unpaced`是额外的有限吞吐模式, 请求帧数必须等于自备文件总帧数且在示例支持上限内, 不能直接把历史600套到任意视频. 错误应先依据平台、动态库、插件、颜色与模型合同定位, 不启用CPU像素/软件解码回退.

## 有界并发与性能复核

采集示例的 `pipeline` 模式由单个 producer 取得帧，每个 worker 独占一个 `RknnModel` 和可写 DMA 目标。总在途许可覆盖排队、执行及等待按序消费的结果；`FrameId`、`SourceFrameId` 与 PTS 随原始输入传递，不能用完成次序或最新帧替代。退出时先停止入口、排空队列并等待同步硬件调用返回，再归还 sample 和关闭 capture。

默认配置是三个独立会话、总在途 6。`sessionsPerCore=2` 可选择六会话、总在途 12，核掩码依次为 1/2/4/1/2/4。可选 worker 数为 1/3/6，总容量必须在 worker 数至 24 之间。更多会话提高吞吐时，也可能增加同核排队和单请求延迟，不能把总 FPS 的倒数当作请求延迟。

实际参数范围、`pipeline`/`replay` 和基线对照入口见[样例说明](../samples/RKNNSharp.Smoke/README.md#有限帧诊断)。`replay` 重复处理有限 DMA 来源，不包含持续 VPU 解码；`--file-unpaced` 单遍处理自备有限视频，帧数须与文件实际总帧数一致。私有媒体、兼容旧版 DLL 和模型均由调用者准备，不是仓库附带资源。

模型几何、可选 SHA256 校验、独立 `Run` 快照与 `RunInto` 缓冲复用见 [API 契约](api.md)。媒体所有权与关闭顺序见[媒体边界](gstreamer.md#并行消费者的媒体边界)。可公开的模型参数、分阶段耗时和完整链路吞吐集中在[测试数据](../README.md#测试数据)，其中各版本、模型和计时范围分别列出。

## 验证边界

本库与样例不提供 CPU 图像处理或软件解码回退。实际部署需重新核验自己的 BSP、模型、来源色域及设备权限。公开的历史数值不是目标域准确率、mAP、其他导出模型的数值一致性或长期热稳定性保证；没有相机时钟到主机时钟映射时，也不能把处理耗时当作曝光到结果的延迟。
