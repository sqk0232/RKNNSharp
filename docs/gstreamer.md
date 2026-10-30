# GStreamer 采集样例与实机证据

本 adapter 位于 `samples/GStreamerNative`，独立于 RKNN 推理库。它只把 VPU 解码得到的线性 NV12 DMABUF 描述符借给调用者；没有 CPU 像素映射、软件解码、预处理或绘图分支。

## 从源码构建与首次检查

在Linux ARM64 RK3588目标板准备C11编译器、CMake≥3.16、pkg-config和GStreamer开发文件. CMake实际要求`gstreamer-1.0>=1.20`、`gstreamer-app-1.0`、`gstreamer-video-1.0`、`gstreamer-allocators-1.0`四个pkg-config模块. 这些开发文件从目标系统/BSP的软件包来源取得; 不将下面本地环境中的包版本或路径视为通用安装命令.

运行时还需对应parser/demux/RTSP/RTP元素及BSP的`mppvideodec`. 普通GStreamer安装或只有MPP库并不保证带有该插件. 它必须支持本源码配置的`dma-feature`、`arm-afbc`、`format`、`ignore-error`属性, 并真正输出线性NV12 DMA-BUF和所需GstVideoMeta; 版本名字相同也不能替代实际内存检查. 从板卡BSP供应商取得兼容媒体插件, 缺失时不要换软件解码器来规避.

先检查依赖, 再在源码根目录构建（输出目录由调用者选择并输入绝对路径, CMake创建；与后续集成步骤保持同一个Bash会话）：

```sh
pkg-config --modversion gstreamer-1.0 gstreamer-app-1.0 gstreamer-video-1.0 gstreamer-allocators-1.0
gst-inspect-1.0 mppvideodec
read -r -p '采集adapter构建输出目录: ' GST_BUILD_DIR
: "${GST_BUILD_DIR:?未设置构建输出目录}"
cmake -S samples/GStreamerNative -B "$GST_BUILD_DIR" -DCMAKE_BUILD_TYPE=Release
cmake --build "$GST_BUILD_DIR" --target rknnsharp_gst rknnsharp_gst_probe --parallel 2
```

板端默认单配置构建生成`$GST_BUILD_DIR/librknnsharp_gst.so`和`$GST_BUILD_DIR/rknnsharp_gst_probe`. 前者放入自己C#应用发布目录; probe仅用于有限采集检查. 它不需要RKNN模型或推理库, 但完整C#示例还需从`native/`生成核心so, 见[部署流程](integration.md#从源码使用). 发布目录、SDK和动态库搜索路径由调用者配置, 不依赖私有任务目录中的已有二进制.

首次可用自备带时间戳的MP4做有限检查, 设置`VIDEO_PATH`与匹配的`CODEC`后运行`"$GST_BUILD_DIR/rknnsharp_gst_probe" file "$VIDEO_PATH" "$CODEC" 5`. RTSP时C#示例可直接使用调用者自备的`RTSP_URL_FILE`, 内容由进程内部读取. 若使用native probe, RTSP参数是已配置的环境变量名, 不是URI; 不把带凭证URI写到命令行、日志或仓库.

## 本地验收记录说明

以下是2026-10-29的板端版本、布局与历史实验记录. 私有相机URL文件、600帧MP4、任务目录和已编译so不随源码提供, 不是首次运行的必要输入. 读者应使用自己的来源并按以上步骤构建; 历史hash不代表可以从公开Release取得这些二进制. 文中未上传的日志仅说明当时验收, 不作为源码使用依赖.

## 已验证环境

2026-10-29，RK3588 / Linux aarch64 / Ubuntu 22.04.5：

| 项目 | 实际版本/来源 |
| --- | --- |
| GStreamer core/tools | 1.20.3，Ubuntu 包 |
| app/video/allocators | 1.20.1，板端 Rockchip 修改的 base 包 |
| `rockchipmpp` | 插件声明 1.14.4；包 `gstreamer1.0-rockchip1=1.14-4+git240417.0aa6bcc+j` |
| 插件文件 | `/usr/lib/aarch64-linux-gnu/gstreamer-1.0/libgstrockchipmpp.so` |
| 编译器 | GCC 11.4.0；使用已安装开发头和 pkg-config，无安装/系统修改 |

可复查命令：`uname -sm`、`gst-inspect-1.0 --version`、`gst-inspect-1.0 mppvideodec`、`dpkg-query -W gstreamer1.0-rockchip1`、`pkg-config --modversion gstreamer-1.0 gstreamer-app-1.0 gstreamer-video-1.0 gstreamer-allocators-1.0`。

## Pipeline 与内存条件

H.264 RTSP 路径如下，H.265 使用对应 depay/parser。URL 通过 `rtspsrc.location` 属性传入，不拼进 pipeline 描述。

```text
rtspsrc protocols=tcp latency=100 drop-on-latency=true tcp-timeout=5000000
  -> application/x-rtp,media=video,encoding-name=H264
  -> rtph264depay -> h264parse
  -> mppvideodec dma-feature=true arm-afbc=false format=NV12 ignore-error=false
  -> video/x-raw,format=NV12
  -> appsink max-buffers=1 drop=true enable-last-sample=false
             wait-on-eos=false sync=true
```

`latency` 由 API 调用者配置，单位 ms，上限 10000。TCP timeout 为 5 秒，TEARDOWN timeout 为 1 秒。appsink 保留时钟同步；有限队列允许慢消费者丢弃旧帧。没有无限缓冲或隐式重连；错误/EOS 交给调用者决定是否关闭并重建会话。

这个板端插件的 pad template 宣称支持 `memory:DMABuf`，但实测强制该 caps feature 会以零帧结束；实际协商为普通 `video/x-raw`，而每个输出 `GstMemory` 确实为 DMABUF。adapter 因此以每帧实际内存为准：必须有两平面的 `GstVideoMeta`、NV12、偶数尺寸、正且足够的 pitch、线性非 AFBC、无额外裁剪。逐平面检查 `gst_is_dmabuf_memory` 和分配边界，任一不符就失败；没有系统内存回退。`gst_buffer_find_memory` 的 skip 与 `gst_memory_get_sizes` 的 offset 相加，得到相对于 fd 起点的真实 plane offset。没有调用 `gst_buffer_map` 或 `gst_video_frame_map`。

官方接口说明可参见 [DMABUF allocator](https://gstreamer.freedesktop.org/documentation/allocators/gstdmabuf.html) 和 [GstVideoMeta](https://gstreamer.freedesktop.org/documentation/video/gstvideometa.html)。本实现只使用板端 1.20 已提供的接口；官方说明不能替代上述板端测试。

## C ABI 与生命周期

以 `samples/GStreamerNative/rknnsharp_gst.h` 为唯一声明。arm64 自然对齐 `rkgst_frame` 共 112 字节：

| 顺序 | 类型/字段 | 含义 |
| --- | --- | --- |
| 0 | `uint32 struct_size,width,height,format` | 格式为 NV12 FOURCC `0x3231564e` |
| 16 | `uint32 n_planes,color_space` | 固定两平面；颜色空间见下文 |
| 24 | `int32 fd[2],stride[2]` | 借用 fd 与字节 pitch；fd 可能重复 |
| 40 | `uint64 offset[2],allocation_size[2]` | 相对于各 fd 起点；分配尺寸可能大于可见像素尺寸 |
| 72 | `uint64 pts_ns,dts_ns,running_time_ns,arrival_monotonic_ns` | 时间戳及 pull 时观察到的本机单调时间 |
| 104 | `void* token` | 持有 `GstSample` 引用，不供调用者解引用/释放 |

`rkgst_open_rtsp(url,codec,latency_ms,...)` 异步启动；`codec` 只接受 `h264`/`h265`。`rkgst_open_file(path,codec,...)` 读取带时间戳的 MP4，经 qtdemux 后走同一 VPU 解码器。裸 H.264 不属于此文件接口的契约。

`rkgst_pull`：1=帧，0=超时，2=EOS，-1=错误。首次 frame 必须全零，持有上一帧时再次 pull 会拒绝。调用者必须保持 frame/token 存活，直到同步 RGA/NPU 工作全部返回；随后调用 `rkgst_release(&frame)`，它释放 sample 并清空结构。fd 始终由 GStreamer 所有，调用者不能 close。全部 frame 释放后调用 `rkgst_close`。capture 是单消费者接口，pull/close 不得并发。

对接 `rks_frame` 前，整合层还须要求：两个 fd 相同、Y offset=0、两平面 pitch 相同、UV offset 可被 pitch 整除，计算 `height_stride=UV_offset/pitch`，并检查容量和底层桥接支持条件。真实流已符合这些条件。decoder buffer 可能仍是编解码器参考帧，禁止原地绘制；先用 RGA 复制至调用者独占 DMA 图像，再绘框。

## 颜色空间：范围与矩阵分别取证

frame 的 `color_space`：0=未声明/不支持完整组合，1=BT.601 limited，2=BT.601 full，3=BT.709 limited。只有 raw caps 显式包含可支持的 matrix/range 才返回非零；不会把 `GstVideoInfo` 的尺寸默认值当作相机事实。

真实摄像头 raw caps 与 h264parse 输出 caps 均不含 `colorimetry`，所以实际 frame 返回 0。保留下来的 H.264 SPS VUI 却明确为 `video_full_range_flag=1`、`colour_description_present_flag=0`：**full range 有协议证据，颜色矩阵未知**。最终集成选用的 BT.601 Full 是显式测试配置，其中 BT.601 矩阵是测试假设，不能说整个组合由 SPS 证明。

`inspect_sps.py` 只解析 baseline/main/extended SPS 压缩头，派生尺寸与已捕获 caps 交叉核对；不解码图像。可本地重现：

```text
python samples/GStreamerNative/inspect_sps.py CAPS_FILE OUTPUT_DIRECTORY
```

`CAPS_FILE` 替换为自行捕获的 h264parse 输出 caps 文本文件，`OUTPUT_DIRECTORY` 指定新的输出目录。脚本生成 Annex B SPS/PPS 与 JSON，不附带摄像头协议快照。协议字段定义可对照 [GStreamer H.264 parser 与其链接的 H.264 标准](https://gstreamer.freedesktop.org/documentation/codecparsers/gsth264parser.html)。

## 构建、有限帧测试与凭证

按前述源码构建后, 可用调用者自备视频执行有限测试：

```sh
timeout 15s "$GST_BUILD_DIR/rknnsharp_gst_probe" file "$VIDEO_PATH" h264 40
```

fixture 生成命令（测试图在生成阶段由 videotestsrc 创建；应用像素路径使用 VPU 解码，不把此图当真实目标语义证据）：

```sh
timeout 15s gst-launch-1.0 -e videotestsrc num-buffers=30 ! video/x-raw,format=NV12,width=640,height=480,framerate=10/1 ! mpph264enc gop=10 ! h264parse ! mp4mux ! filesink location="$GENERATED_VIDEO_PATH"
```

用户授权的真实配置 `$RTSP_URL_FILE` 是单行完整 URI。只在远端进程内读取并直接调用 API，或把它放入子进程环境后运行 `probe rtsp ENVIRONMENT_VARIABLE_NAME h264 30`。不要把 URI 作为进程命令行、pipeline 字符串、日志或本地文件；不下载/打印/修改原文件。probe 的 RTSP 参数是环境变量名。真实凭证更新后已成功验证；早期 401 是旧凭证，不应自动反复认证。临时凭证文件已清理。原文件权限由用户管理，本任务没有修改。

API bus error 只返回来源元素、domain、code 和识别出的 401，不返回原始错误/debug 文本。外层测试也要关闭含 URL 的调试日志并脱敏输出，避免外部插件直接打印凭证。

## 实际结果与边界

| 验证 | 结果 |
| --- | --- |
| GCC 编译 | `-Wall -Wextra -Werror` 通过 |
| 本地 MP4 | 30/30 帧，0 timeout，PTS 单调，EOS |
| 本地布局 | 640×480；同 fd；pitch=640；offset=0/307200；allocation=614400 |
| 真实 RTSP | 30 帧，0 timeout，exit=0；2560×1440、60 fps caps |
| 真实布局 | 同 fd；pitch=2560；offset=0/3686400；allocation=7372800；height_stride=1440 |
| 慢消费者 | 每次持有 250ms：13 帧、12 个 PTS 跨帧、0 timeout、EOS，有限队列丢旧帧 |
| 所有权/失败 | 持有期间 fd 有效；未释放重复 pull 被拒绝；重复 release 安全；缺文件报错 |
| 资源观察 | 上述本地验证进程 peak RSS=26224 KiB，仅短时快照 |

历史本地 MP4 测试中，厂商插件在起始/结束曾打印 PTS/missing-frame 警告，仍实际读取全部 30 帧且正常 EOS。原始媒体与诊断日志不随源码提供。

`arrival_monotonic_ns` 是应用 pull 观察时间，不是曝光时间。`running_time_ns` 是 sample segment 对 PTS 的转换；两者不可直接相减当端到端延迟。未建立相机时钟到主机时钟映射，未测曝光到推理结果延迟、长期内存趋势、实际丢帧计数、断流恢复、H.265 或录像/输出编码。PTS 跨帧只能证明本次慢消费跳过帧，不能单独区分网络、解码与应用队列各自丢帧数量。本 adapter 不自动重连，也不包含推理、检测后处理和输出编码；完整链路验收由整合样例完成。

## 并行消费者的媒体边界

adapter 不把 capture 限制为“全局只能有一帧在途”。`rkgst_pull` 检查的是传入的那个结构的 token；单一采集线程每次提供独立的全零结构，即可借出多个独立 sample。板端已持有六帧后才归还：六个不同 token、六个不同 DMABUF fd、递增 PTS；再由三个消费者线程乱序归还，并成功继续采集。该测试只运行 VPU 与生命周期操作，没有占用 NPU；三 NPU 会话调度由整合样例负责。

每个 sample 应由一个引用型独占 lease 对象管理，交给一个 worker；不要把含 token 的 C 结构按值复制成多个可释放 owner，也不要对同一结构并发调用 pull/release。一次 `rkgst_release` 只归还该 sample，不访问 capture 对象；不同 sample 可以由不同消费者线程归还。若某帧需要广播多个消费者，必须在上层共享一个引用计数 owner，最后一个消费者才执行原生 release；当前三 worker 分工是不同帧，不需要广播。

建议当前媒体交接约定如下：

1. 总在途许可在 pull **之前**获取；否则采集线程已拉出、尚未入队的额外 lease 会使计数超过声明上限。Channel 容量 3 与总在途 6 是两种计数：队列中的 owner、执行中的 owner、等待有序消费的 owner 都计入总在途。appsink 的一个内部缓冲和 MPP 编解码器引用池不计入这六个应用 owner，因此不能把六当作整个 pipeline 的内存上限。
2. 采集成功时分配应用 `FrameId`，同时保留原始 `pts_ns` 和本机观察时间。结果对象携带同一 owner/FrameId/PTS；worker 完成次序不决定输入帧身份。PTS 可能缺失或跨帧，不能用“取当前最新帧”匹配已完成检测。需要按输入顺序交付时，用应用提交序号重排结果；超时、丢弃和错误也必须结清其对应 owner/许可。
3. 入队失败、被替换的旧帧、取消清队列、worker/后处理异常都必须释放各自尚未转移的 lease。硬件同步调用尚未返回时不能释放。若要在后处理释放 decoder sample 之前绘框，先完成 RGA 硬件复制，绘制对象为独占 DMA 图像；释放来源帧后只可保留独占输出与元数据。
4. 关闭顺序为停止 producer 并等待其退出、完成/清理队列、等待所有 worker 及有序输出消费退出、确认 leased sample 全部归还，最后调用 `rkgst_close`。native close 不自行等待外部 lease，也不提供取消正在运行硬件操作的能力。

媒体 PTS 与本机观察时间分别记录，不能用主机时钟替代媒体时间戳。

## 已有日志的实际交付速率

从历史真实 RTSP 测试的 30 个已交付帧计算 29 个间隔：

| 时间序列 | 观察窗口 | 交付率 | 间隔中位数 / 最大值 |
| --- | --- | --- | --- |
| 媒体 PTS | 510.532 ms | 56.8035 帧/秒 | 16.619 / 33.573 ms |
| 本机 pull 观察时间 | 510.483 ms | 56.8089 帧/秒 | 16.570 / 33.671 ms |

两个时间序列各有两处间隔大于 25ms。计算公式为 `(帧数-1)/(末时间-首时间)`，不是 `帧数/窗口`。这是约半秒短窗内应用实际收到帧的速率，不是持续相机源帧率、解码器极限或 RKNN 库吞吐；无法仅凭这份日志确定丢帧发生在哪一层。caps 的 `60/1` 是声明值。三会话实测需分别报告实际输入/交付率、完成率、丢弃量、在途上限与处理时间，不能把媒体输入不足当作推理库性能上限。

## 暂停采集后重放已有 DMA 样本

新增导出 `int rkgst_pause(rkgst_capture*, char* error, uint32_t error_size)`；0 为成功，-1 为失败，公开帧结构仍为 112 字节。它不释放 sample、不 flush、不关闭 decoder/capture，也不读取图像像素。

调用者先停止并等待唯一 producer，再调用 pause。实现请求 `GST_STATE_PAUSED`，以 `gst_element_get_state` 最多等待 5 秒；只有返回 SUCCESS 或 live source 合法的 NO_PREROLL，且实际 `current=PAUSED`、`pending=VOID_PENDING` 才成功。不能把 ASYNC 当成功；失败/超时后的状态不作保证，不继续性能基准。上述 5 秒是状态等待上限，源元素仍使用前述 TCP/TEARDOWN 超时；外层有限测试另有 20 秒进程时限。

成功后继续 pull 会明确失败，避免误把残留 appsink 缓冲当新输入。当前只提供暂停，不提供恢复入口；有限重放完成后等待所有消费者和硬件同步调用退出、释放每个原始 sample，再 close capture。重放任务借用同一组只读来源帧时，必须由外层 batch owner 保活至全部消费者结束；每个请求不能分别释放同一个原始 token。绘图仍只在独占输出 DMA 图像进行。

板端真实摄像头验证结果：

| 检查 | 实际结果 |
| --- | --- |
| 借出帧 | 六个真实 2560×1440 sample，独立 token/fd |
| 暂停状态 | 0.657 ms；get_state=SUCCESS、current=PAUSED、pending=VOID |
| 暂停后解码输出 | decoder src 的 BUFFER probe 在 500 ms 观察窗计数 7→7 |
| 保持有效性 | 六个 fd 均有效，暂停后对每帧执行同步 `rks_copy_frame` RGA 硬件复制，6/6 成功 |
| 清理 | 消费完成后释放六个 sample，再关闭 capture，通过 |
| 编译/符号 | GCC `-Wall -Wextra -Werror` 通过，`nm -D` 确认导出 `rkgst_pause` |

重现命令不把 URI 或密码放在参数中：

```sh
read -r -p '暂停验证日志输出文件: ' PAUSE_LOG_PATH
: "${PAUSE_LOG_PATH:?未设置日志输出文件}"
timeout 20s python3 samples/GStreamerNative/verify_pause.py "$GST_BUILD_DIR/librknnsharp_gst.so" "$NATIVE_BUILD_DIR/librknnsharp_native.so" "$RTSP_URL_FILE" "$PAUSE_LOG_PATH"
```

`verify_pause.py` 是样例目录内的有限验证脚本：读取 URI 文件但不打印/下载，检查独立 sample、PAUSED 状态、输出探针、fd 与 RGA 访问，无 CPU 像素映射。探针稳定证明本次窗口没有新增解码输出，不能据此声称所有网络流量或 RTCP/keepalive 消失，也不等于整机 VPU/NPU 资源完全空闲。官方行为参见 [GstElement 状态变更与 NO_PREROLL](https://gstreamer.freedesktop.org/documentation/gstreamer/gstelement.html) 和 [rtspsrc PAUSED 会请求 RTSP PAUSE](https://gstreamer.freedesktop.org/documentation/rtsp/rtspsrc.html)。

`PAUSE_LOG_PATH` 由调用者设置为新的日志文件路径；两个构建目录沿用前述配置。上述历史暂停验证对应 adapter SHA-256 为 `4063e3489063eb1744dcfdbb5a70d8d1042d1f6ade4956bb2f74f00467382374`，不要求自行构建得到相同二进制。

## 包含持续 VPU 解码的无节拍输入

固定六个 DMA 样本的重放不包含持续解码，不能替代完整流水线基准。为此在独占媒体窗口内用同相机原始 H.264 制备了有限 MP4：

```text
rtspsrc(TCP) -> rtph264depay -> h264parse
  -> video/x-h264,stream-format=avc,alignment=au
  -> identity eos-after=601 -> mp4mux -> filesink
```

此过程只解析/复用压缩码流，没有解码器、编码器、CPU 图像映射或像素转换。`capture_compressed.py` 在设备内部读取 `$RTSP_URL_FILE`；正常 EOS 后关闭 pipeline，检查尺寸、packet 数与首关键帧，再把 `.partial.mp4` 原子改名。脚本会拒绝覆盖已有素材。实际捕获 10.146 秒。测试视频不随源码提供，使用时以自己的 `VIDEO_PATH` 指定有限 MP4 文件。

| 元数据 | 实际值 |
| --- | --- |
| 编码/尺寸 | H.264 Main level 5.1，2560×1440 |
| 包/帧数 | 600 packets / 600 frames |
| 时长/文件大小 | 10.029667 秒 / 19522905 字节 |
| 第一包 | PTS=0、DTS=0、关键帧 |
| SHA-256 | `f5cd1e3ce46ae79d827007b262ed0b7560ac8e4c0428c060c9c8788fb8696244` |
| SPS VUI | full range=1，matrix 未声明；保持 BT.601 Full 为显式矩阵假设 |

用户视频仅存设备任务目录，不下载、打包或提交视频像素；本地证据只保存元数据、校验值和不含像素的验证日志。`ffprobe -skip_frame all -count_packets` 仅用于封装/压缩元数据检查，不执行软件图像解码。

新增独立入口：

```c
int rkgst_open_file_unpaced(const char *path, const char *codec,
                           rkgst_capture **out, char *error, uint32_t error_size);
```

该入口仍走 qtdemux/parser/MPP/NV12/DMABUF 严格检查，保持原始分辨率与 PTS；唯一策略差异是 `appsink sync=false drop=false max-buffers=1`。这是明确的有限吞吐基准模式，不测播放时钟同步或端到端延迟；无 clock pacing、有界背压且不丢 appsink 帧。原有 RTSP 与 `rkgst_open_file` 的同步/drop 默认行为保持兼容。

单一 producer 在取得总在途许可后 pull，为每次成功取帧生成独立 `SourceIndex`，与原始 PTS 分别保存。每个结果保留相同来源索引与 owner；不得用 PTS 或完成顺序去匹配其他帧。本次文件入口不自动循环：EOF 后停止 producer、排空结果、等待所有 worker/硬件操作完成、归还所有 sample，再 close。比较不同 worker 数时先单独借首帧完成相同暖机并归还，再重新打开同一文件，使用相同索引范围和相同绘框路径。不能通过降分辨率、跳过复制/绘框、重复处理固定 DMA 或丢掉慢帧提高“完整”吞吐。

有限 VPU-only 验证命令：

```sh
timeout 12s "$GST_BUILD_DIR/rknnsharp_gst_probe" file-unpaced "$VIDEO_PATH" h264 601
```

实际收到 600/600 帧，0 timeout，EOS，PTS 严格递增，所有帧尺寸保持 2560×1440、ABI=112。冷启动进程耗时 1.2364 秒，600/耗时=485.27 fps；首末交付观察间隔计算约 512.32 fps。两者只包含 VPU 解码、描述符检查与 pull/release，**没有包含 RGA/NPU/后处理/复制/绘框，不代表完整流水线达到该吞吐**。完整吞吐由整合样例以此有限输入另行验收。

随后在独立短窗口做了十二在途预检：同文件、单 producer、每次提供独立全零 frame，单次 pull timeout 上限 3 秒。持有全部十二个 sample 后确认十二个不同 token、十二个不同 DMABUF fd、2560×1440/ABI112、PTS 递增与 fd 有效；逆序全部释放后 close 成功，总耗时 89.584ms。该预检没有运行 NPU/RGA，也没有 CPU 图像映射。这是独立的在途容量验证，不能由前述逐帧吞吐测试替代。

该历史无节拍验证对应 adapter SHA-256 为 `c4ff6358d437925673fb099c4051167f82736162c2439485a1e55afd751fa6ef`，保留 `rkgst_pause` 和原 112 字节 frame ABI。旧 paced 文件入口另以三帧读取、0 timeout 作兼容验证。
