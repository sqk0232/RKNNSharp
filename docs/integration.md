# 整合与验收

## 从源码使用

本节是公开源码的操作入口. 后文“本地验收历史”中的机器路径、`.artifacts`、旧DLL、日志和归档仅记录开发时环境, 不随源码提供, 不作为本节步骤的前提; 本项目没有在此承诺预编译Release、SDK、模型或测试视频下载.

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

默认先做有限帧同步调用; 不需要旧版DLL、历史600帧片段或本地候选包. `--file-unpaced`是额外的有限吞吐模式, 请求帧数必须等于自备文件总帧数且在示例支持上限内, 不能直接把历史600套到任意视频. 错误应先依据平台、动态库、插件、颜色与模型合同定位, 不启用CPU像素/软件解码回退.

## 本地验收历史

以下余文保留原始实测过程、参数、机器路径和校验值以便追溯. 所述文件/二进制/私有输入不保证随公开源码提供, 原始命令不是读者可直接照抄的安装或部署步骤; 使用方法以本页前面的源码流程为准. 历史native为`09971b...`、adapter为`c4ff...`的组合与其更早基线分别记录, 重新构建不保证得到相同二进制hash.

核心入口是 `src/RKNNSharp/RknnModel.cs` 的 `Run` 和 `Detect`. `Run` 返回每次调用独立的 float 快照; `Detect` 在同一会话锁内运行原生 INT8、借用三个标准 NCHW 输出、验证 generation、完成 LUT 解码/NMS, 不向调用方暴露原生指针. SafeHandle 的额外引用覆盖整个借用期. `Dispose` 与全部硬件调用互斥. 原生 ABI 为 2, 结构大小保持 frame48/tensor364/run_info56/rect16.

`DmaFrame` 只描述借用的已就绪 DMA 图像. NV12 默认 `Unspecified` 会拒绝; RGB/RGBA 不需要 YUV 色域. 所有像素转换、INT8 XOR 和绘图都由硬件执行, 检测 tensor 及坐标后处理由 CPU 执行. 参见 [硬件路径](hardware.md)、[模型契约](model-contract.md) 和 [媒体路径](gstreamer.md). 不修改 decoder 参考帧, 示例的 RGA copy/draw 使用 `DmaImage` 独占目标.

## 构建与部署

本机 .NET SDK10.0.403, 目标 net10.0/C#14, ARM64 self-contained runtime10.0.14. 两个参考工程当前也为 net10.0; 本库没有第三方运行时 NuGet 依赖. 没有 Git 初始化、提交或推送, 参考工程仅只读.

直接执行以下构建、测试与发布命令:

```powershell
dotnet build RKNNSharp.slnx -c Release --nologo -p:MSBuildEnableWorkloadResolver=false -p:UseSharedCompilation=false -m:1
dotnet test tests/RKNNSharp.Tests/RKNNSharp.Tests.csproj -c Release --no-build --no-restore --logger 'trx;LogFileName=unit.trx' --results-directory .artifacts/tests -p:MSBuildEnableWorkloadResolver=false
dotnet publish samples/RKNNSharp.Smoke/RKNNSharp.Smoke.csproj -c Release -r linux-arm64 --self-contained true -o .artifacts/linux-arm64 -p:MSBuildEnableWorkloadResolver=false -p:UseSharedCompilation=false
```

当前构建 0 warning/0 error, 三条命令退出码均 0; 测试 `Passed: 21, Failed: 0, Skipped: 0`. 覆盖 ABI/stride/色域、float/INT8 解码、量化表、并列类别、邻接阈值、并发 LUT 发布、NMS/还原, 以及有界流水线乱序、取消、执行失败、消费失败排空和300请求的source引用/worker排他性. 结果 `.artifacts/tests/unit.trx`. Windows 沙箱早期 VSTest IPC、SDK workload/编译服务及 NuGet audit 受限; 经自动审查授权后在沙箱外执行相同验证成功, 当前还原启用 NuGet audit, 不以关闭审计作为验证.

板端没有系统 .NET, 使用上述约89 MB发布目录（压缩约33 MB）, 未安装系统运行时. 板端 native 构建:

```sh
sdk="$RKNN_SDK_DIR"
cmake -S native -B .artifacts/native-build -DCMAKE_BUILD_TYPE=Release \
  -DRKNN_INCLUDE_DIR="$sdk/include" -DRKNN_LIBRARY="$sdk/aarch64/librknnrt.so"
cmake --build .artifacts/native-build -j2
cmake -S samples/GStreamerNative -B .artifacts/gst-build -DCMAKE_BUILD_TYPE=Release
cmake --build .artifacts/gst-build -j2
```

将 `librknnsharp_native.so`、`librknnsharp_gst.so` 与 self-contained 发布文件一起放到本次 `/tmp/rknnsharp-20261029/integrated/`. 运行前 `chmod +x RKNNSharp.Smoke`, 并设置 `LD_LIBRARY_PATH` 包含此目录及匹配的 RKNN runtime 目录; 本次 CMake 构建的 native 库已有板端 SDK 绝对 RUNPATH. 不替换驱动、模型或系统库. 升级时必须同时更新匹配 ABI 的库和 sample, `replay` 还依赖 GStreamer adapter 新增 pause 导出.

## 同步调用及真实流验证

命令格式:

```text
RKNNSharp.Smoke MODEL (--file MP4|--rtsp-file PRIVATE_URL_FILE) frames codec confidence colorspace cycles warmup mode [core]
```

`colorspace` 为 auto/601limit/601full/709limit. auto 仅接受 caps 明确给出的支持组合; 当前相机 color_space=0, 因此测试明确传入601full. H.264 SPS VUI 证明 full range, BT.601 矩阵仍为测试假设. `$RTSP_URL_FILE` 仅由远端进程读取, 不输出、下载或修改. 所有日志不含 URL/认证信息.

```sh
cd /tmp/rknnsharp-20261029/integrated
export LD_LIBRARY_PATH="$PWD"
./RKNNSharp.Smoke $MODEL_PATH --rtsp-file $RTSP_URL_FILE 60 h264 .25 601full 1 5 run
./RKNNSharp.Smoke $MODEL_PATH --rtsp-file $RTSP_URL_FILE 60 h264 .25 601full 1 5 detect
./RKNNSharp.Smoke $MODEL_PATH --rtsp-file $RTSP_URL_FILE 10 h264 .25 601full 1 2 compare
```

上述真实流命令均退出0. 原始布局2560×1440 NV12, 两平面同 FD, pitch2560, UV offset3686400, allocation7372800, heightStride1440. 实际 letterbox640×360, scaleX=scaleY=.25, padLeft0/padTop140. 原先完整30帧测试295检测, 每帧非空, 三轮释放后FD82/82/82（DMA0/device12/socket4/pipe10）, Dispose后调用拒绝且重复Dispose安全. 此冷启动平均38.744 ms只作历史记录, 不作为优化对照基线.

float/raw 同帧对照含2 warmup+10 measured=12帧. 检测数量、ClassId和顺序一致, 最大 confidence 绝对差2.9802322e-8, 源坐标最大差0.00012207031像素; 验收限为1e-5和.01像素, 阈值始终.25. runtime float反量化与规范 `(q-zp)*scale` 在全2,142,000元素最大差约9.54e-7, 不声称bit exact. 紧贴confidence/IoU阈值的输入可能受浮点舍入影响, `Run+Decode`保留为浮点基线. 默认anchors及类别来源仍未独立确认, 非空/坐标有效并不证明模型精度.

## 单会话性能对照

同一 ABI2、相同模型和阈值, 每条路径预热5帧后测量60帧. 相机内容随时间变化, draw框数也会变化; 同输入正确性由前述对照单独验证. 下表为平均值, 单位ms:

| 阶段 | float Run+Decode | INT8 Detect |
|---|---:|---:|
| RGA preprocess | 4.288 | 4.183 |
| NPU | 17.123 | 17.138 |
| runtime outputs | 2.498 | .912 |
| managed输出复制/借用校验 | 2.661 | .0068 |
| decode+NMS | 3.441 | .585（p50 .220） |
| RGA source copy/CSC | 2.237 | 2.291 |
| RGA draw | .731 | .804 |
| 完整处理平均 | 33.130 | 26.080 |
| 完整处理p50 / p95 | 32.503 / 35.600 | 25.474 / 28.770 |
| 每帧托管分配 | 8,577,653 bytes | 7,733 bytes |
| 60帧Gen2 GC次数 | 24 | 0 |

处理计时包含托管数组分配/复制、RGA copy/draw, 不含等待RTSP/VPU的pull时间、记录日志和输出统计. 首次分段基线另存 `benchmark-baseline.log`（平均32.384）, 主对照使用同ABI的 `benchmark-run-abi2.log` / `benchmark-detect-abi2.log`. 不把NPU17.1ms倒数当作完整链路FPS.

## 三会话有界流水线

```sh
./RKNNSharp.Smoke $MODEL_PATH --rtsp-file $RTSP_URL_FILE 60 h264 .25 601full 3 5 pipeline
```

`BoundedOrderedPipeline.cs` 实现单capture producer、三个固定会话（Core0/1/2）、容量3的Wait channel和总在途6个permit. permit在结果按序消费后归还, 因而乱序pending也有上界. 每次输入为唯一 `NativeSampleLease` 引用owner; worker完成该帧硬件处理后释放sample, 结果保留同一个FrameId、SourceFrameId、PTS及检测. 失败/取消仍为每个已入队ID产生完成状态, 停止新入口并排空, join后才关闭capture. worker的owned RGB图像只作该帧的硬件绘框目标, 示例不把可复用buffer交给异步下游.

借鉴点来自只读参考源码, 未复制整套框架或CPU图像路径:

- Framework `OrderedConcurrentPipeline.cs:49` 的执行/buffer permit, `:78` 的Sequence有序汇集和finally释放; `ParallelWorkflow.cs:571` 的保留采集帧契约. 本示例保留原PTS, 不将主机时钟时间冒充媒体PTS.

真实流180帧（三轮各60）全部完成绘框与精确ID/PTS配对, 每轮sample acquired=released=61（含一个共享只读warmup样本, 每worker5次暖机）, 最大在途6/3/3, 全部会话释放后FD86/86/86. completed FPS分别50.43/55.03/56.50, 对应原始PTS输入51.63/55.03/56.83 FPS. 后两轮queue平均.084/.076ms, 已主要受输入速率限制. ordered latency平均38.13/28.67/31.51ms, p95为95.89/33.56/32.97ms; 首轮保留首次日志/JIT抖动, 不隐去异常值. 三核NPU单request仍约17.7–17.9ms, 并发改善吞吐而不使单request变为三分之一.

后续六帧同源重放曾发现并行数值差异, 失败日志 `replay-6source-300.log` / `replay-diagnostic.log` 保留. 独立native复现证明来源DMA内容未变、固定输入的并行NPU一致, 问题出在并发RGA预处理的自动硬件/插值调度. 修复通过逐操作参数限定两个等价RGA3核并显式双线性插值; 不使用全局调度状态, 不串行化NPU. 270次native真实帧对照的signed输入和三路raw输出逐字节一致, 旧串行参考未变. 缺少支持的RGA3即失败, 不进行RGA2 resize或CPU回退. 最终.NET重放如下, 数值判据保持严格, 没有靠放宽容差通过.

原生纯NPU三独立context另测得161.15 requests/s, 单request中位数18.11–18.61ms. 该测试不包含预处理、后处理、copy/draw或VPU/网络, 详见硬件文档; 不能作为本示例端到端吞吐. `replay` 使用相同六个真实DMA帧测量不受相机节拍限制的处理吞吐, 与真实视频交付率分别报告.

## 上一轮同源DMA重放与真实流回归

```sh
./RKNNSharp.Smoke $MODEL_PATH --rtsp-file $RTSP_URL_FILE 300 h264 .25 601full 1 5 replay
./RKNNSharp.Smoke $MODEL_PATH --rtsp-file $RTSP_URL_FILE 30 h264 .25 601full 3 5 pipeline
./RKNNSharp.Smoke $MODEL_PATH --rtsp-file $RTSP_URL_FILE 5 h264 .25 601full 1 2 compare
```

最终native SHA256 `e0b09bd19034900c9697a04059c9ee27b1d81bc1113c1fc53dbb4d2df50d8d15`, adapter SHA256 `4063e3489063eb1744dcfdbb5a70d8d1042d1f6ade4956bb2f74f00467382374`. 所有最终调用退出0. DLL/XML/两库的远端和本地产物hash一致, 见 `final-deployment-sha256.txt`.

重放保留六个真实2560×1440只读GstSample, 用adapter的同步PAUSED阻止后续解码产帧, 直到全部worker结束才释放. PAUSED不代表RTCP/keepalive等后台活动绝对为零. 每轮单Core0和三Core0/1/2使用同六个来源与相同profile, 各预热5次/worker后测量300个请求; 三轮之间重新获取六个样本. RequestId与SourceFrameId分开记录, 后者是重复来源而不是新的视频帧. 计时包含RGA预处理、NPU、raw输出获取、INT8解码/NMS、RGA源copy/CSC及绘框, 不包含持续VPU/网络或逐请求JSON输出; 有限结果在计时结束后统一记录.

| 试次 | 单会话 requests/s | 三会话 requests/s | 单会话处理平均ms | 三会话处理平均/p95 ms | 三会话有序完成平均ms |
|---|---:|---:|---:|---:|---:|
| 1 | 34.871 | 104.161 | 28.542 | 28.622 / 31.590 | 56.996 |
| 2 | 34.073 | 100.031 | 29.202 | 29.774 / 32.903 | 59.305 |
| 3 | 36.058 | 101.986 | 27.606 | 29.230 / 32.325 | 58.203 |

三轮共1,800个测量请求, 同SourceFrameId的全部Detection字段按严格 `SequenceEqual` 一致（含count/ClassId/order/score/box）, 不使用近似容差. 每轮单/三会话检测总数分别800/800、800/800、900/900. 三会话总在途上限6、单会话2; 每模式300请求均完成并回收, 六个来源每轮6/6释放, 最终FD86/86/86. “处理时间”从worker开始到draw完成, “有序完成”从请求获准入队到按序消费, 包含有界队列等待, 不包含上游等待admission permit. 日志 `replay-final-1.log`、`replay-final-2.log`、`replay-final-3.log`.

第1轮代表性阶段平均ms（single / 3worker）: RGA预处理4.484/4.700, NPU17.213/18.248, runtime输出1.470/1.125, 解码/NMS .437/.183, RGA copy2.719/2.717, draw1.950/1.493. 并发改变吞吐, NPU单请求仍约18.25ms, 并未变成 `1000/104.161` ms.

最终真实流短回归90帧（30×3轮）全部完成精确FrameId/PTS配对和RGA绘框, 检测207个, 每轮sample31/31释放（含warmup来源）, FD86/86/86. 完成率48.94/55.19/55.09 FPS, 对应PTS输入52.07/55.60/55.51 FPS. 首轮有序完成p95 107.66ms包含首次日志/JIT抖动, 后两轮为30.21/30.20ms; 不隐去冷启动尾部. 见 `rtsp-final-pipeline.log`. 新库同帧float/raw再次对照7帧通过: 最大score差5.96e-8, 最大源坐标差.00012207像素, 仍使用原有1e-5/.01像素限值, 见 `rtsp-final-compare.log`.

最终进程检查未发现本任务残留RKNNSharp/原生probe/采集进程. 无相机凭证被打印、下载或写入交付包.

## 上一轮直接引用产物

实际文件位于 `.\.artifacts\release\RKNNSharp.dll`、`RKNNSharp.xml` 和 `librknnsharp_native.so`. 引用DLL时保留同目录XML以启用IntelliSense; 将匹配`.so`放到Linux ARM64应用输出目录或其native搜索路径. 应用目标net10.0, 系统RKNN Runtime1.5.2与RGA1.10.1保持外置. 核心引用不需要GStreamer/MPP, 仅采集示例需要adapter及媒体依赖. `SHA256SUMS.txt`记录本次版本; 完整打包路径见README.

## 证据与未验证项

远端证据根 `/tmp/rknnsharp-20261029/integrated/`, 对应本地 `.artifacts/integration/` 保留完整脱敏日志; 构建/测试/发布日志在 `.artifacts/` 根, native专项证据在 `.artifacts/hardware/`, GStreamer证据见其样例evidence目录. 日志保留输出tensor统计/检测/耗时, 不保留认证内容或输入图像像素.

没有原模型ONNX/PyTorch、标签/anchors确证或标注数据集, 未进行mAP/原导出数值parity验收. 相机色彩矩阵、普通用户设备权限、长期热稳定性、其他模型和其他板卡仍需验证. 没有驱动/频率/模型修改、CPU图像回退、视频编码、UI预览或真实设备控制操作.

## 上一轮120 FPS 完整流水线

本轮沿用模型、640输入、.25阈值、anchors和RGA3插值策略. 核心DLL未修改; 仅sample增加每核两个独立会话的有界供给, native将同色矩形合并为同步RGA job. 每worker独占一个RknnModel和owned RGB DMA图像, 不共享可写缓冲, 也不跳过copy/draw. 没有CPU图像读取/转换/绘制、降分辨率、异步旧结果复用或隐式实例池.

`PipelineConfiguration` 默认仍为3worker/6在途. `sessionsPerCore=2` 默认6worker/12在途, masks为1/2/4/1/2/4. 显式worker数只允许1/3/6, 总容量不能小于worker数且不超过24; 实机接受配置为1/2、3/6、6/12. 总permit在有序结果消费后归还, 覆盖等待及乱序结果; worker完成图像硬件访问后可先释放其独占sample. 后续下游如果需要图像, 必须另行设计owned输出租期, 当前示例不将复用图像暴露给异步消费者.

### 构建及可复现命令

```powershell
dotnet build RKNNSharp.slnx -c Release
dotnet test tests/RKNNSharp.Tests/RKNNSharp.Tests.csproj -c Release --no-build
dotnet publish samples/RKNNSharp.Smoke -c Release -r linux-arm64 --self-contained true -o .artifacts/candidates/120fps/linux-arm64
```

最终build/tests/publish均退出0, 0 warning/0 error, xUnit 24 passed/0 failed. 新增覆盖六worker与12/24容量下的source身份和worker排他性, 以及core映射、容量限制. 取消/失败/慢首帧排空原测试保留. 实际命令和退出码在 `.artifacts/candidates/120fps/commands.log`, TRX在同目录`tests/unit.trx`. 最初配置表达式缺少括号导致CS0019, 已修复, 初次退出1记录保留, 末次三条验证退出0.

板端独立候选目录为 `/tmp/rknnsharp-20261029/integrated/candidate-120`, 不覆盖上一轮应用或归档. 相机素材仅保存在板端 `/tmp/rknnsharp-20261029/gstreamer/camera-fixture/camera-2k-h264.mp4`, SHA256 `f5cd1e3ce46ae79d827007b262ed0b7560ac8e4c0428c060c9c8788fb8696244`. 它是同相机600个H264 packet/600帧、2560×1440、首帧关键帧、约10.03秒的有限压缩文件, 无重编码, 未下载或打包素材.

以下为实际调用参数; 在候选目录设置 `LD_LIBRARY_PATH` 为当前目录和板端已验证RKNN runtime目录, 每次记录DLL及两个so的SHA256到对应日志首部:

```sh
# 每次新取同六个只读来源, 暂停capture后依次1/3/6会话各300请求.
./RKNNSharp.Smoke $MODEL_PATH --rtsp-file $RTSP_URL_FILE 300 h264 .25 601full 1 5 replay Auto 2 12
# 同片单遍: workers依次1/3/6, sessionsPerCore为1/1/2, inFlight为2/6/12.
./RKNNSharp.Smoke $MODEL_PATH --file-unpaced /tmp/rknnsharp-20261029/gstreamer/camera-fixture/camera-2k-h264.mp4 600 h264 .25 601full 1 5 pipeline Auto 2 12 6
# 六会话真实流短回归, 三轮各30帧.
./RKNNSharp.Smoke $MODEL_PATH --rtsp-file $RTSP_URL_FILE 30 h264 .25 601full 3 5 pipeline Auto 2 12 6
```

`--file-unpaced`仅用于pipeline, 原`--file`行为保持. adapter使用MPP硬解、sync=false/drop=false/max-buffers=1, 不按PTS墙钟节拍播放, 无循环. 首先单独打开文件的首帧, 所有会话各暖机5次, 然后归还sample并关闭capture; 再次打开同片从SourceFrameId=0测到599, 最终必须观察EOS且600帧全部完成, 少帧或多帧均失败. 因此每轮sample601/601包含一个独立warmup来源, 性能分母为600, 没有将重复暖机计入新帧数. FrameId/SourceFrameId与PTS分别记录. 12个独立sample同时持有/逆序释放已由媒体专项证明.

### 供给与绘框收益分开验证

保留原e0b09 native不变时, 首次同六源1/3/6会话各300请求为36.866/105.151/128.350 requests/s, 900请求全部严格相同, 见`evidence/replay-e0-136.log`. 六会话提高NPU供给, 允许其前后处理与其他会话推理重叠; 每核同时排队两个请求, 因而单个native推理调用的等待增加. 没有据吞吐反算单请求时间.

native同步job8绘框专项的像素/行padding对照通过, 各次RGA操作仍同步完成后才返回. 普通框像素保持一致; 原点恰好1的极小填充框向左/上扩到0, 修复旧BSP拒绝, 详见硬件文档. 最后六context专项540请求的完整signed输入及三个raw输出逐字节一致, 来源hash不变, 见`.artifacts/hardware/concurrency-six-session-final.log`. 核心Detect不变.

### 三次严格同源重放

最终native09971b/adapterc4ff组合, 三次独立采集六个来源, 每次模式各300请求, 共2700请求. 全部Detection字段严格`SequenceEqual`, 每模式300请求都释放, 每次来源6/6释放, 最终FD86/86/86. 检测总数按试次为450/300/300, 同一试次三个模式完全相同. 日志`evidence/replay-final-120-{1,2,3}.log`:

| 试次 | 1会话 req/s | 3会话 req/s | 6会话 req/s | 6会话处理平均/p95 ms | 6会话有序完成平均/p95 ms |
|---|---:|---:|---:|---:|---:|
| 1 | 37.102 | 106.003 | 129.250 | 46.254 / 50.932 | 91.645 / 98.142 |
| 2 | 38.049 | 106.904 | 128.548 | 46.397 / 49.299 | 92.037 / 97.364 |
| 3 | 35.589 | 103.526 | 130.088 | 45.855 / 48.978 | 90.861 / 96.812 |

本表暂停capture, 不包含持续解码或网络; 每秒请求可重复来源, 不能叫独立新视频帧. 单会话处理平均26.156–27.964ms, 六会话吞吐提升伴随单请求处理和排队延迟增加. 上一轮纯NPU三context161.15req/s仍为不含预后处理/绘框的独立历史指标.

### 持续VPU解码完整600帧

先以旧e0 native单会话在同片处理600帧, 37.085fps、620检测, 作为独立基线`evidence/file-e0-single.log`. 随后更换09971b native, 各模式都与该旧基线逐帧严格核对FrameId、SourceFrameId、PTS、ClassId/顺序/score/box及DrawCompleted, 没有数值容差. 历史比较脚本不随源码保留.

| 当前配置 | 完成帧 | 完整FPS | 处理平均/p95 ms | 有序完成平均/p95 ms |
|---|---:|---:|---:|---:|
| 1会话/在途2 | 600 | 37.628 | 26.461 / 27.893 | 52.965 / 55.783 |
| 3会话/在途6 | 600 | 104.233 | 28.425 / 30.909 | 56.786 / 61.171 |
| 6会话/在途12, 试次1 | 600 | 127.249 | 46.474 / 50.423 | 92.594 / 99.713 |
| 6会话/在途12, 试次2 | 600 | 126.935 | 46.689 / 51.356 | 93.011 / 100.314 |
| 6会话/在途12, 试次3 | 600 | 128.185 | 46.241 / 50.449 | 92.198 / 99.413 |

五个当前模式/试次全部600/600、620检测, 与旧e0完全相同, 收到EOS且每轮sample601/601, FD80. 日志`evidence/file-final-{1,3,6}.log`及`file-final-6-repeat-{2,3}.log`. 总计当前3000测量帧逐帧严格对照旧600帧基线, 不靠候选自身相互一致作为唯一依据. 同一进程的RTSP三轮资源回归另见下段, 不用不同进程首次库加载的FD差推断泄漏.

五次本地对照的baseline/candidate文件名、600帧、exactIdentityPtsDetections=true和退出码0另保存为`.artifacts/candidates/120fps/comparison-results.json`, 不需重新运行硬件即可审查.

600帧计时从测量producer开始到全部结果按序消费, 包含VPU解码/其等待、RGA预处理、NPU、输出、INT8后处理、owned copy/draw和有界调度; 有限逐帧JSON在计时结束后统一输出. 不含独立warmup/模型创建、RTSP网络、编码/显示或最终日志写出. 单请求处理从worker开始至draw完成; 有序完成从sample拉出后的准入时间至结果消费, 包含队列, 不含pull前等待permit或原始相机拍摄延迟.

600帧代表性平均阶段ms（单会话 / 六会话试次1）: RGA预处理4.415/5.258, native推理调用17.270/34.959, runtime输出1.407/1.837, CPU检测后处理.300/.290, RGA copy2.383/3.423, RGA draw.497/.563. 六会话native推理调用包含同核其他会话排队, 不是单核模型执行变快或变慢的独立测量. 提升来自供给重叠; 最终完整吞吐126.9–128.2fps, 不将其倒数当作7.8ms请求延迟.

真实RTSP短回归90帧三轮全部有序/绘框, 检测32/30/33, sample31/31每轮, 释放后FD86/86/86. 完成53.213/54.436/55.371fps, 原PTS输入55.246/55.426/55.686fps. 原相机约60fps, 本结果不代表RTSP每秒产生120个新帧. 处理平均30.304/28.444/27.091ms, 有序完成p95为44.565/45.585/29.759ms, 早期JIT及排队尾部照实保留. 日志`evidence/rtsp-final-120.log`.

### 当前交付与回退

直接引用文件在 `.\.artifacts\candidates\120fps\release\`:

| 文件 | SHA256 |
|---|---|
| RKNNSharp.dll | c80efd5019ffc5c71e5c3f4a1a3840db16045b721adeba2528b04664a0bdb570 |
| RKNNSharp.xml | 4096cf90cb31616fa008d2791bed3bf144a4c3c024599ec268da52e5db296546 |
| librknnsharp_native.so | 09971b537e2798947794bb24cb5a00973cc63089e69814b77287a7bf2f15c95e |

系统RKNN/RGA依赖外置, 不打包模型、相机素材或凭证. 核心DLL/XML与旧版hash相同. 完整sample另含adapterc4ff6358d437925673fb099c4051167f82736162c2439485a1e55afd751fa6ef及.NET10.0.14运行时, unpaced需要这个新adapter导出, 缺失时清楚失败, 不能混用旧so.

同级`RKNNSharp-linux-arm64-library-120fps.zip`和`RKNNSharp.Smoke-linux-arm64-120fps.tar.gz`为当前包. 原 `.artifacts/`两个已验收包未覆盖, 并原样复制到`.artifacts/accepted-20261029/`, SHA256分别cd3186e20c8e4fa91fe2b669ae6f039283108064cb7bcb65477a4e4dff0983a4和6c077cd6c35ad0f3b11fdf7595a3d874965d79eab3c7bba2651ff1a4438c0c98. 回退可直接使用该原应用/库组合; 未改系统驱动/运行库, 无系统回退步骤.

本轮证据在`.artifacts/candidates/120fps/evidence/`, `final-metrics-120.json`保留所有统计, `evidence-120.tar.gz`为脱敏日志及匹配DLL/XML/两库快照, 不含输入素材. 结束时进程检查无本任务Smoke、native probe或采集进程. 相机矩阵仍为601Full显式测试假设, 未新增精度、长时间热稳定性或其他模型/板卡兼容结论.

## 几何合同、可选SHA256与Run快照

本轮按A→B→C顺序实施, 每阶段完成本地构建/单元测试及固定真实DMA输入对照后才进入下一阶段. 未修改native生产代码/ABI、RGA插值/舍入、模型、anchors、阈值、NMS、六会话/在途12或系统驱动. Framework仅只读参考. 所有新产物位于`.artifacts/candidates/contracts-run/`, 上轮120fps目录及归档hash保持不变.

### A: native返回的几何合同

`NativeLetterboxGeometry.Create`为内部方法, `Run`和`Detect`共用. 读取静态NHWC目标H/W及现有`NativeRunInfo.ResizeWidth/Height`, 检查min2、目标内整数尺寸、居中padding和actual scale. 复算只采用当前native的double ratio、half-away舍入及min2, 不改为ToEven/min1, 不增加偶数尺寸、等比两轴或禁止上采样的限制. scale必须精确等于`float(resizeDimension)/sourceDimension`, 原值原样构造返回transform. 公开`LetterboxTransform`及`profile.Decode`的一般几何语义不收紧.

本地31 tests通过（原24+A七项）, 0 warning/0 error. 覆盖256×101的half tie→253、640×637奇数padding、641×479两轴实际比例、极细min2仅数学验证、非方形NHWC目标384×640以及字段篡改/NaN/Inf/1ULP拒绝. 非方形用例为模型专项只读复核后的补充; 不把极细数学用例宣称为RGA可接受输入.

阶段A对原120fps DLL, 固定六个paused真实2K DMA来源、各模型warmup5次后12次调用. 25,704,000个float元素逐bit完全相同, transform原样相同, `Detection.SequenceEqual`通过. 保留首个Run快照后继续其他来源调用, 旧值不变且新旧数组引用不同. RGA owned copy/draw全部成功、sample6/6释放, 退出0. 日志`stage-a/contract-a.log`; 本地构建/测试证据为`stage-a/{build,tests,commands}.log`与`stage-a/tests/unit.trx`, 不使用旧根目录tests.log替代.

### B: 明确的可选模型摘要重载

原`RknnModel(string modelPath, NpuCoreMask coreMask = Auto)`二进制/源码签名保留, 转发null. 新增非可选三参重载:

```csharp
using var model = new RknnModel(modelPath, NpuCoreMask.Core0, expectedModelSha256);
```

null直接跳过hash读取; 空/非法64位hex抛`ArgumentException`, 大小写均接受. 非匹配摘要抛`InvalidDataException`, 验证发生在平台检查和native会话创建前. 仅启动时读取模型主文件一次, 不做逐帧hash/路径缓存. 不是签名、原子加载或anchors/类别/精度证明; 验证stream关闭后native仍按路径加载, 存在检查与加载间的文件变更窗口.

本地37 tests通过（A31+B六项）, 0 warning/0 error, 包括null不检查不存在的文件、旧ctor/default参数反射合同、非法摘要先拒绝、大小写、同路径新内容重新核验、错误摘要在native前失败及stream及时关闭. Linux实机对真实模型验证lower/upper/null/legacy构造、错误摘要拒绝和之后恢复, 重复三轮释放后FD47/47/47. 日志`stage-b/hash-b.log`.

阶段B同时对stage-a DLL做同六个held来源12次对照, 开启已知真实模型摘要. 25,704,000个float元素逐bit一致, Detection/transform严格一致、旧快照不变、数组独立, copy/draw全部成功, 来源6/6释放, 退出0, 见`stage-b/contract-b.log`. 此阶段尚未修改Run数组分配.

### C: Run独立float快照的未初始化分配

唯一生产逻辑改动是`new float[count]`改为`GC.AllocateUninitializedArray<float>(count)`. 原生`rks_copy_output`要求精确元素数并完整覆盖; 所有head成功后才发布`InferenceResult`, 失败只丢弃方法内局部数组. 不增加池或Dispose合同, 仍返回每次独立的float快照. `Detect`仍为原INT8借用/LUT路径.

本地37 tests通过、0 warning/0 error. 与stage-b DLL隔离对照时采用同六个paused来源, 预热5次后60次测量, 每次交替旧/新调用顺序. 128,520,000个float元素逐bit一致, Detection严格相同, 旧Run快照保持且数组独立, RGA copy/draw成功, 来源6/6释放, 退出0. 另对原120fps DLL做12次对照, 再有25,704,000个float元素和全部Detection严格一致. 见`stage-c/evidence/contract-c-vs-b.log`和`contract-c-vs-120.log`.

主性能对照只比较stage-b→stage-c, 不混入几何/hash更新:

| Run托管分配/复制阶段 | stage-b | stage-c |
|---|---:|---:|
| 平均ms | 2.821 | 2.567 |
| p50 ms | 1.442 | 1.417 |
| p95 ms | 8.966 | 8.513 |
| 每调用分配 | 约8.568 MB | 约8.568 MB |

计时取两版各自核心返回的`ManagedOutputsMs`, 排除反射/JSON/逐元素比较, 仍含其内部可能出现的GC等待. 分配统计基线多出少量反射诊断对象, 两侧大数组分配量不变. 这是一次60次交替测量的小幅观察收益, 不宣称保证性加速或`Detect`吞吐提升. 位比较仅涉及输出tensor, 未由CPU读取图像像素.

### 诊断命令与最终整合

`ContractComparison.cs`只在sample中使用隔离`AssemblyLoadContext`加载阶段前DLL, 原有二参ctor可被旧调用方找到. 新旧模型对同一个有效DMA lease同步执行, 比较float字节、transform、Detection和快照独立性; 不将诊断反射或baseline路径放入核心API.

实际板端工作目录`/tmp/rknnsharp-20261029/integrated/contracts-run/stage-{a,b,c}`. 同模型和同相机600帧文件沿用前节, 没有复制/下载图像素材. 各阶段命令示例:

```sh
# A: baseline=原120fps DLL, B: baseline=../stage-a/RKNNSharp.dll, C: baseline=../stage-b/RKNNSharp.dll.
./RKNNSharp.Smoke $MODEL_PATH --contract-compare /tmp/rknnsharp-20261029/gstreamer/camera-fixture/camera-2k-h264.mp4 ../stage-b/RKNNSharp.dll 60 5484cccbcc30292a167b2f9d7cf7cb06210decf4343e41f59e35c0ee41785f1e
./RKNNSharp.Smoke $MODEL_PATH --verify-model-hash
./RKNNSharp.Smoke $MODEL_PATH --file-unpaced /tmp/rknnsharp-20261029/gstreamer/camera-fixture/camera-2k-h264.mp4 600 h264 .25 601full 3 5 pipeline Auto 2 12 6
```

仍使用同一09971b native和c4ff adapter, `LD_LIBRARY_PATH`按前节配置. 所有阶段及最终命令退出0. 最终同一进程三轮六会话/总在途12, 每轮warmup独立重开文件, 600个测量帧全部完成、620检测、EOS=true、sample601/601释放. 1,800帧与原120fps `file-final-6.log`逐项FrameId/SourceFrameId/PTS/Detection/DrawCompleted严格一致, 无容差. 本地逐帧复核再核对三个cycle日志, 均600帧true/退出0; 结果见`stage-c/evidence/comparison-results.json`.

| 轮次 | 完整含VPU FPS | 处理平均/p95 ms | 有序完成平均/p95 ms |
|---|---:|---:|---:|
| 1 | 127.704 | 46.360 / 50.554 | 92.355 / 98.763 |
| 2 | 128.638 | 46.275 / 49.904 | 92.158 / 98.423 |
| 3 | 128.207 | 46.554 / 49.999 | 92.538 / 98.712 |

释放后同进程FD82/82/82稳定. 不用它与上一版不同进程FD80的差判断泄漏. 计时仍含持续VPU/预处理/NPU/INT8后处理/owned copy/draw, 不含RTSP网络、编码、显示和计时后日志. 本轮未重新测试真实RTSP帧率, 上一轮相机交付率保留为历史证据.

最终依次执行上文的 `dotnet build`、`dotnet test`、`dotnet publish` 命令, 输出目录为 `.artifacts/candidates/contracts-run/final/linux-arm64`, build/test/publish均退出0、37 passed/0 failed/0 skipped、0 warning. 全命令记录在各阶段及`final/commands.log`, TRX在对应`tests/unit.trx`. 最终产物与板端已测DLL/XML/应用/两so的SHA逐件相同; 无本任务遗留Smoke/native probe/采集进程.

### 当前包与回退

直接引用`.\.artifacts\candidates\contracts-run\final\release\`的DLL/XML及匹配native, 系统RKNN/RGA依赖外置:

| 文件 | SHA256 |
|---|---|
| RKNNSharp.dll | 81b783600cafe1682e1f9bd71069c08792204b6015414c390e87eb1becc74ebe |
| RKNNSharp.xml | ca109c17b0e61ae0657edf14a36beda8660a8f4927bd37912982505951b43f71 |
| librknnsharp_native.so（未改） | 09971b537e2798947794bb24cb5a00973cc63089e69814b77287a7bf2f15c95e |

同级`RKNNSharp-linux-arm64-library-contracts-run.zip`与`RKNNSharp.Smoke-linux-arm64-contracts-run.tar.gz`是本轮交付包, sample另含原c4ff adapter及.NET10运行时. 不含模型/相机素材/凭证. 两个120fps归档保持原SHA363cf7d5b93a75a4402154c82e59ab78ccd80013514e3cf833bb7be5aef8b0dd与9ab97b572a595900a9cd78e9f24294405d5de775e66fea5c962be78ae7935256, 可直接换回该应用/库组合, 无系统回退步骤.

相机矩阵/anchors来源、模型精度、长期热稳定性及其他BSP/模型的验证边界不变. 本轮没有新的CPU图像路径或普通float输出池.
