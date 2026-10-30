# Rockchip 原生硬件后端

当前后端已经在 LubanCat-5 的 **RK3588（非 RK3588S）** 上通过原生构建、RGA/NPU 正常与失败路径测试。媒体拉流属于 sample adapter，不进入本库。后端不含软件解码、CPU 图像预处理或 CPU 绘框回退。

版本记录分阶段: 本页早期 DMA/媒体链路使用 Runtime 1.5.2; 后续 Runtime 2.3.2 已有 Run/RunInto、YOLOv5 和指定 YOLOv8n 的实测记录, 驱动仍为 RKNPU 0.9.8、RGA 1.10.1_[3]. 指定模型边界见[DFL检测契约](yolo-detection.md). 这些记录不代表所有 2.x、模型或媒体链路组合均已验证. 编译头文件须与目标 Runtime 匹配, 模型 Toolkit/Runtime/BSP 驱动需兼容, 不要求它们版本号数字相同. 本次文档整理没有新增硬件验证.

## 从源码构建

运行目标是Linux ARM64 RK3588, 不是Windows/x64. 在目标板准备C++17工具链、Linux开发头（含`linux/dma-heap.h`）、CMake≥3.16, 以及与BSP驱动兼容的RKNN Runtime/SDK头文件和RGA头文件/运行库. 依赖来源为板卡BSP供应商及[Rockchip RKNN官方仓库](https://github.com/airockchip/rknn-toolkit2)、[librga官方项目](https://github.com/airockchip/librga); 下表仅是已验证版本, 不将官方主分支当作该旧BSP版本, 不承诺SDK任意升级兼容.

从源码根目录执行以下Bash配置. 输入你实际取得的头文件目录和ARM64运行库文件, 以及你选择的构建输出目录, 均使用绝对路径; 与后续集成步骤保持同一个Bash会话. CMake负责创建输出, 无需下载已有so:

```sh
read -r -p '包含rknn_api.h的目录: ' RKNN_INCLUDE_DIR
read -r -p 'ARM64 librknnrt.so文件: ' RKNN_LIBRARY
read -r -p '核心native构建输出目录: ' NATIVE_BUILD_DIR
: "${RKNN_INCLUDE_DIR:?未设置RKNN头文件目录}"
: "${RKNN_LIBRARY:?未设置RKNN运行库文件}"
: "${NATIVE_BUILD_DIR:?未设置构建输出目录}"
cmake -S native -B "$NATIVE_BUILD_DIR" -DCMAKE_BUILD_TYPE=Release \
  -DRKNN_INCLUDE_DIR="$RKNN_INCLUDE_DIR" -DRKNN_LIBRARY="$RKNN_LIBRARY"
cmake --build "$NATIVE_BUILD_DIR" --target rknnsharp_native --parallel 2
```

`native/CMakeLists.txt`会查找`im2d.h`与RGA库; 非标准安装位置可在配置时增加`-DRGA_INCLUDE_DIR="$RGA_INCLUDE_DIR" -DRGA_LIBRARY="$RGA_LIBRARY"`, 两变量分别是包含im2d.h的目录和目标板RGA库文件. 不使用Windows库或x86库. 默认板端单配置构建输出为`$NATIVE_BUILD_DIR/librknnsharp_native.so`, 将其放入自己应用发布目录并配置其外部依赖, 见[完整源码部署流程](integration.md#从源码使用).

BSP必须提供本实现使用的RGA3、NPU和`/dev/dma_heap/system-uncached-dma32`, 并授予运行用户相应设备权限. 缺少硬件/驱动/匹配runtime不能仅靠编译本桥接补足; 不支持时失败, 无CPU回退. 模型需由调用者自备并满足[模型契约](model-contract.md), 源码不附测试模型.

## 本地验收记录说明

以下为2026-10-29的特定设备记录. 机器SDK路径、`.artifacts`日志/候选二进制和私有模型不作为公开下载内容或构建前提; 首次使用按上面的变量化步骤. 历史hash只标识当时实测产物, 不要求读者取得这些文件或构建出相同hash.

## 实测版本与依赖

| 项目 | 2026-10-29 实测值 |
|---|---|
| SoC / 系统 | device-tree `rockchip,rk3588-lubancat-5` / `rockchip,rk3588`；AArch64 Ubuntu 22.04.5 |
| 内核 / ABI | 6.1.84；glibc 2.35；GCC 11.4.0；C++17 |
| NPU | RKNPU driver 0.9.8；RKNN Runtime 1.5.2 `c6b7b351a@2023-08-23T15:28:22` |
| RGA | driver 1.3.7；板端头文件和运行库 1.10.1_[3]；两个 RGA3 和一个 RGA2 |
| 模型 | `$MODEL_PATH`，模型编译工具版本 1.5.0；模型文件只读 |
| 模型 SHA256 | `5484cccbcc30292a167b2f9d7cf7cb06210decf4343e41f59e35c0ee41785f1e` |

板端现有 SDK 位于 `$RKNN_SDK_DIR`，本次显式使用该目录的 `include/rknn_api.h` 和 `aarch64/librknnrt.so`，没有安装或替换系统运行库。RGA 依赖 `/usr/include/rga` 和 `/lib/aarch64-linux-gnu/librga.so.2`。设备节点 `/dev/rga`、`/dev/dma_heap/system-uncached-dma32`、DRM render 节点存在；测试使用已授权的现有 root 会话，普通用户的节点权限仍需部署方验证。

没有执行模型转换、量化校准、驱动/BSP 修改或频率调整。源权重、ONNX、代表性校准数据未提供；此次数值验证是硬件输入变换正确性与同模型运行结果对照，不是数据集 mAP 验收。

## 支持的模型与缓冲契约

`native/include/rknnsharp.h` 是 C ABI，`rks_abi_version()` 为 **2**。ABI 2 增加 raw INT8 借用输出和显式 core mask；结构体布局保持不变。AArch64 实测结构体大小：frame 48、tensor 364、run_info 56、rect 16 字节。所有字段自然对齐，托管调用方必须检查 ABI，拒绝缺少新导出的旧库。

本版只支持一个静态 batch=1 的 NHWC RGB 三通道原生输入，类型 INT8、affine zero point=-128、scale 必须精确等于 `float(1/255)`。输入 width stride 必须为 16 像素的倍数，allocation size 必须等于 stride×height×3，无额外隐藏布局。实际模型为 `[1,640,640,3]`、stride=640、size=1228800。此 profile 对应 RGB、mean=0、std=255 的模型契约；量化属性相同不代表其他模型的颜色或训练预处理也相同。

外部帧接受线性的单 DMA-BUF fd，格式 NV12、RGB888 或 RGBA8888。`offset_bytes` 必须为 0；RGB 行步长以字节给出且能整除每像素字节数。NV12 必须宽高/步长均为偶数，Y 和 UV 连续，UV 起点严格为 `row_stride_bytes * height_stride`，总大小至少为该值的 3/2。不接受独立双 fd、AFBC、未知 modifier、非零起始 offset 或无法证明的 plane layout。

NV12 必须明确标记 BT.601 limited、BT.601 full 或 BT.709 limited；其他矩阵/范围失败，不猜测。RGB/RGBA 的 color_space 可为 0。NV12 到 NV12 的硬件复制必须保留同一颜色元数据；allocator 默认 NV12 BT.601 limited，调用方应在描述符中继承真实源颜色信息。NV12 绘框还受偶数坐标/尺寸/线宽限制；硬件拒绝时返回错误，应用可选择 RGB 独占目标后绘框。

## 硬件路径与共享证据

1. 上游完成 VPU 解码，并持有 `GstSample`/帧 owner。桥接只借用 DMA fd；调用前生产者必须完成写入。
2. RGA 通过 `importbuffer_fd` 导入该 fd，按真实 stride 进行 resize、指定矩阵的 NV12→RGB CSC 和 114 letterbox，写入专用 DMA32 RGB 缓冲。推理 resize/CSC 每任务仅允许本板两个经验证等价的 RGA3 核，显式采用水平/垂直 LINEAR；缺少 RGA3 或不兼容格式/geometry 即失败，不回退 RGA2 或 CPU。
3. RGA 填充另一个 DMA32 缓冲为逐字节 `0x80`，把两个 packed RGB 缓冲按 **RGB565 字视图**执行硬件 ROP XOR。只重新解释地址和字宽，不执行 RGB565 颜色转换；这精确实现每个字节 `uint8_pixel XOR 0x80`，对应本 profile 的 INT8 输入。
4. 同一个 signed DMA fd 经 `rknn_create_mem_from_fd` 导入，`rknn_set_io_mem` 使用查询得到的 native attr，`pass_through=1`，同步 `rknn_run`。
5. 只有输出检测张量进入 CPU。兼容的 `rks_run` 使用 `rknn_outputs_get(want_float=1)`，再由 `rks_copy_output` 复制 float 检测张量；快路径 `rks_run_int8` 使用 `want_float=0` 写入 session 预分配的标准 NCHW INT8 输出，再通过借用指针在托管锁内直接后处理。
6. 绘框先 `rks_allocate_image` 分配调用方独占的 DMA32 图像，再 `rks_copy_frame` 由 RGA 复制/CSC，最后 `rks_draw_rectangles` 在该目标上执行 RGA 硬件画框。不得修改可能被解码器继续引用的原帧。

DMA32 分配使用 `/dev/dma_heap/system-uncached-dma32`。原生 smoke 输出实际绑定 fd 及 DMA inode；实现中该 fd 同时作为 RGA signed destination 和 RKNN native input。当前 RKNN 1.5.2 的 `set_io_mem` 对 null virtual address 返回 -4，因此仅为输入建立一个 VA 映射并传给 runtime；生产桥接从不解引用该地址或读写像素。不能把这描述为“完全无 mmap”或“CPU 零占用”。专有 runtime 内部行为未做二进制审计；输入无转换的依据是 native layout/type、共享 fd、pass-through 官方契约与实际数值对照。

这些步骤包含必要的设备端格式转换和写入另一硬件缓冲，不是“所有步骤共用一个 fd”。可证明的结论是：**应用桥接的图像像素处理不经 CPU 拷贝、预处理或绘框**，解码帧→RGA 与 RGA signed buffer→NPU 各段通过 DMA-BUF 共享。输出检测张量读取/反量化及后处理使用 CPU。

所有 RGA 调用均为同步模式，NPU 也同步返回；不输出异步 fence，不在硬件完成前复用缓冲。桥接内部使用 uncached DMA32，不存在 CPU 像素写入后的 flush；外部缓冲的生产完成和缓存一致性由生产者保证。若以后接受 CPU 写入的缓冲或异步生产者，必须扩展显式 cache/fence 契约，不能直接复用当前假设。

## 生命周期与失败行为

单个 session 内部串行化推理和输出访问；不同 session 各自拥有上下文与输入缓冲。调用方必须将 session destroy 与所有调用互斥。外部帧 fd 和 owner 需要活到同步调用返回；`rks_image` 的 fd 是借用值，释放 image owner 前必须结束所有使用复制描述符的调用。C# SafeHandle 需要覆盖整个借用期。

`rks_borrow_output_int8` 返回的指针由 session 持有，只在下一次 attempted run 或 destroy 之前有效。调用方的同一把排他锁必须覆盖 run→取得所有输出指针→完整 decode/NMS，任何指针或 Span 都不得逃逸；native 单次调用中的锁不足以保护返回后的借用期。每次 attempted run 都递增 generation，并先将 encoding 状态置为无效；失败、float/raw 模式切换和旧编码读取均被拒绝。不同 head 的 generation 必须一致。Raw 快路径只接受标准 `OUTPUT_ATTR` 的连续 NCHW、affine INT8，而不是原生 NC1HWC2 或 padded 输出布局。

RGA handle 先释放，再释放 RKNN external-memory wrapper/context，最后解除 VA 并关闭自有 DMA fd。外部帧 fd 不由桥接关闭。外部导入必须通过 `fstat` 验证 fd 的实际 allocation 长度，拒绝声明 size 超过该长度的描述符；本板 DMA heap 已实测 `st_size` 等于真实分配大小。复制通过 fd 的 inode/device 检查源目标是否同一 allocation，拒绝别名复制。失败推理使旧输出失效；不合法 offset/格式/尺寸/stride/size、越界矩形和硬件错误显式失败，不进行软件回退。

当线宽覆盖小框时，RGA 直接填充矩形；RGB 的单像素宽/高提升为图内靠近原坐标的最小 2x2、2xN 或 Nx2 marker。本板 librga 1.10.1 还会拒绝起点恰为 1 的填充：仅对这类小框 marker，将对应起点扩到 0、extent 加 1，保持覆盖原 marker；其他起点不变。例如 `(1,1,1,3)` 的 marker 为 `(0,0,3,4)`。这个几何调整只处理坐标，不读取或修改 CPU 像素。NV12 仍要求偶数坐标和 extent；其他硬件拒绝继续显式返回错误。

## 硬件验证命令

已满足上述目标板条件并配置变量后, 可额外从同一源码构建有限硬件smoke. `MODEL_PATH`由调用者指定, 不是仓库附带模型：

```sh
cmake --build "$NATIVE_BUILD_DIR" --target rknnsharp_hardware_smoke --parallel 2
"$NATIVE_BUILD_DIR/rknnsharp_hardware_smoke" "$MODEL_PATH"
# 仅诊断程序可选的只读像素抽样; 不属于生产调用路径.
RKS_DIAGNOSTIC_READBACK=1 "$NATIVE_BUILD_DIR/rknnsharp_hardware_smoke" "$MODEL_PATH"
```

普通 smoke 不读输入图像像素。诊断开关仅在测试程序中启用少量只读抽样，用 DMA_BUF_SYNC READ 包围读取，验证首/中/末行的 384 个边界字节；这些字节不会参与生成或修正模型输入。

实际验证通过：

- RGB 0/1/127/128/255 及通道交错边界：384/384 抽样字节等于独立 RGA signed 直接填充参考。
- 三个完整 NPU 输出与该独立参考逐元素相同，均 `maxdiff=0, unequal=0`。
- 外部 RGB 行步长 1968 字节、可见行 1920 字节时，全部输出和无 padding 的参考相同；合法奇数 RGB 宽 639 能运行。
- owned DMA32 NV12 图像的硬件复制、CSC 和绘框通过；1x1/1x3/3x1/3x3 RGB 边缘小框硬件填充通过。坏 stride/fd/size、伪造超过真实 fd allocation 的 size/height、奇数 NV12、未知格式、非零 offset、别名复制均被拒绝。
- 五次 session 重建/释放后 fd 数保持 warm baseline 8→8；冷启动 6→8 为库进程级资源，未据此宣称全进程资源归零。
- Raw 输出各 head 的字节数严格为 1632000、408000、102000。对同一硬件输入的全部 2,142,000 元素逐一比较 `(q-zp)*scale` 与 runtime float：最大绝对差 `9.53674e-7`，逆量化恢复的 q 全部相同。runtime 的近零舍入残差可造成很大的 ULP 距离，不能称为 bit exact 或最多 1 ULP；后处理对照使用明确的绝对容差并保持原阈值。Raw/float 编码状态、失败失效、指针清空和跨 head generation 一致性均通过。

早期短基线为 batch1、一个 context、RKNN 默认 core policy：一次启动帧后连续三帧常量 114，RGA 预处理约 2 ms、NPU run 约 17.1 ms、float 输出取得约 2.6 ms；raw 输出取得热帧观察约 0.70–0.78 ms。时间由同步调用边界的 monotonic clock 测量，预处理包含外部 fd 导入；不包含 RTSP、解码、C# 拷贝检测张量或 YOLO NMS，不能换算成完整管线吞吐承诺。真实流完整阶段基准由 sample 的 warmup/测量日志给出。环境快照 NPU 温度约 38.8°C。没有测量 DDR 带宽、持续负载、节流或长期稳定性；未调整频率、governor、驱动或模型。

单会话真实流主对照见 `.artifacts/integration/benchmark-run-abi2.log` 和 `benchmark-detect-abi2.log`：同 ABI、各 5 warmup+60 帧，平均完整处理 33.130→26.080 ms，Gen2 GC 24→0。相机内容随时间变化，这不是静态逐像素同输入的耗时对照；同帧数值验收单独见 `raw-float-compare.log`，12 对帧的检测 count/class/order 相同，最大 score 差 `2.9802322e-8`、坐标差 `0.00012207031` 像素。[完整阶段定义与日志](integration.md#单会话性能对照)。

## 当前模型的算子执行设备

在独立诊断 context 中仅启用 `RKNN_FLAG_COLLECT_PERF_MASK`，按本板 1.5.2 头文件要求，在一次同步 `rknn_run` 和 `rknn_outputs_get` 后查询 `RKNN_QUERY_PERF_DETAIL`。正式库和性能计时 context 没有启用该标志，模型、驱动、时钟和 ABI 2 均未改变。原始报告见 `.artifacts/hardware/perf-detail.txt`，命令/版本记录见 `perf-detail.log`，可复现的独立诊断源码为 `perf_detail.cpp`。

报告共 82 个节点，其中 78 个神经网络计算节点全部标记 `Target=NPU`：ConvRelu 50、ConvReluAdd 7、Concat 13、MaxPool 3、ConvTranspose 2、Conv 3。其余 4 个节点是 `InputOperator:images` 和三个 `OutputOperator`，标记 `CPU`、NPU cycles 为 0，报告耗时合计 92 µs。没有 GPU target，也未报告 `fallback` 或 `unsupported` 标记。

当前给定模型的可证结论为：**报告的神经网络计算节点全部在 NPU，未见 CPU/GPU 计算算子 fallback；Runtime 仍报告 CPU 输入/输出边界节点，并由 CPU 完成调用调度和检测结果后处理。** 四个 CPU 节点不能从图中忽略：报告将它们归类为输入/输出，但没有进一步拆分其内部的同步、绑定、布局整理或数据搬运。native 输入使用查询得到的原生 layout/type 和 `pass_through=1`，该契约不要求 CPU 输入格式转换；输出检测张量在 CPU 可读内存取得与整理则属于本实现明确允许的结果处理。专有 Runtime 内部未做二进制审计，不能把结果扩大为“整个 Runtime 没有 CPU 活动/搬运”或“所有报告节点都是 NPU”。诊断开启 perf 标志后单次 run 为 43.315 ms，**只作执行设备诊断，不能混入正常性能数据**。

## 核心选择、吞吐与 Graph

`rks_set_core_mask` 显式设置单个 session，支持本板头文件定义的 0/1/2/4/3/7；默认仍为 auto。三独立 session 可分别设置 1/2/4，并各自持有 I/O；库不隐式创建池，也不把同步调用改成返回前一帧。

`rknnsharp_core_benchmark` 使用硬件生成的相同输入、同步 `rknn_run`，没有 RTSP、预处理、每帧输出转换或 NMS。每个单 context policy 5 warmup+40 次，单独固定核心 5 warmup+60 次，最后三 context 同时各 100 次。实测单 context mask 0/1/3/7 的中位数均约 17.1 ms，mask3/7 未获多核延迟收益；不能因设置 API 返回 0 宣称该模型已三核拆分。

三独立 context 固定核心 1/2/4 后，独立运行中位数分别 17.304/17.521/17.628 ms；并发中位数分别 18.108/18.568/18.606 ms，实际三个 core 的 load 均非零，全部输出与参考相同。300 次完成用时 1861.65 ms，**聚合吞吐 161.15 request/s**；这是吞吐改善，单请求延迟没有变成 `1000/161.15` ms。受 DDR/资源竞争影响，并发单请求反而略慢。复现命令：

```sh
./build/rknnsharp_core_benchmark $MODEL_PATH
```

上述纯 NPU 测试使用各 context 相同的常量输入，不能代替真实图像的并发数值验收。后续真实样本重放揭示了旧版自动 RGA 调度的偏差，已按下述原生分段证据修复；常量测试或实时流非空检测计数不能代替这项正确性验证。

## 真实帧并发预处理修复

独立 `native/tests/concurrency_probe.cpp` 使用当前 GStreamer adapter 在其自身进程持有六个真实 2560×1440 NV12 样本并 PAUSED。原始样本 fd/lease 直到所有测试结束才释放；诊断只读 CPU 取样/指纹与逐字节对照不进入生产预处理，不用于修正像素。

修复前证据分别保存在 `.artifacts/hardware/concurrency-initial.log` 和 `concurrency-core-interp.log`：

- 六个源 allocation 首尾指纹不变；三个 context 串行处理时，signed 输入与 raw 输出相同。
- 完整并发时，约每轮一个 context 的 signed 输入出现约 40 万字节差异，随后 NPU 输出不同；context 位置不固定。
- 先串行完成 RGA、固定各自 signed 输入后，再仅并发 NPU：输入及全部 raw 输出完全相同。
- 每任务强制 RGA2 的 resize/CSC 精确重现第二组输入指纹；RGA3 core0/core1 相同。仅设置 LINEAR 无法消除 RGA2/3 差异，不能声称插值单独修复了问题。这里确认的是本板两代 RGA 的组合路径输出差异，未进一步推定硬件内部的插值/CSC 实现细节。

最小生产修复只对推理 resize/CSC 的 `improcess` 传入每任务 `im_opt_t`：`core=RGA3_CORE0|RGA3_CORE1`、双向 LINEAR，避免全局或线程 `imconfig` 状态竞争。仍允许两个 RGA3 调度，RGA2 继续负责经过数值验证的 fill/ROP，三个 NPU context 保持独立并发；没有串行整个推理链路。官方对强制 core/priority 的产品使用有谨慎说明，因此本后端只采用已实测的 RK3588 core 集合，保留原优先级，遇不兼容 geometry 显式失败；不能泛化为其他 BSP/SoC 自动安全。

修复后 `concurrency-rga3-fixed.log` 对同六个 PAUSED 真实源执行 90 次同源并发和 180 次混源并发：共 270 次的 signed 输入、三个完整 raw INT8 输出均与**旧串行 RGA3 DEFAULT** 参考逐字节相同，六个源指纹首尾不变，测试退出 0，`exact_equal=1`。这批实际帧上旧串行参考没有变化。安全 smoke 重跑记录为 `diagnostic-rga3-fix.log`，原有量化/stride/小框/失败失效/释放检查全部通过。托管 pipeline 的最终真实重放与性能复测见整合文档。

额外检查 owned `CopyFrom` 的无缩放 2560×1440 NV12→RGB：两个 RGA3 产生完全相同的 RGB 字节；强制 RGA2 在此 geometry 被 kernel 以 `no core match` 拒绝，而不是产生另一组有效像素。因此当前实际底图复制路径没有证据要求扩大修改，`rks_copy_frame` 保留自动兼容核心选择；这一结论不等于任意尺寸的 RGA2 CSC 均已验证等价。

该诊断测试直接包含 native 实现以访问内部缓冲并隔离阶段，不增加生产导出/测试钩子。在板端 hardware 测试目录实际使用以下编译和执行命令：

```sh
sdk="$RKNN_SDK_DIR"
gst='/tmp/rknnsharp-20261029/gstreamer'
g++ -O2 -std=c++17 tests/concurrency_probe.cpp -Iinclude \
  -I"$sdk/include" -I/usr/include/rga -I"$gst" \
  -L"$sdk/aarch64" -L"$gst/build" \
  -Wl,-rpath,"$sdk/aarch64" -Wl,-rpath,"$gst/build" \
  -lrknnrt -lrga -lrknnsharp_gst -pthread -o concurrency_probe
./concurrency_probe $MODEL_PATH $RTSP_URL_FILE fixed
```

第二个参数是进程内部读取的私有 URL 文件，不在日志或命令参数中展开凭证。ABI 仍为 2；此轮预处理修复基线库 SHA256 为 `e0b09bd19034900c9697a04059c9ee27b1d81bc1113c1fc53dbb4d2df50d8d15`。

## 同色绘框批量提交

`rks_draw_rectangles` 先验证全部参数，导入目标一次，再将最多 8 个矩形加入一个 RGA job（最多 32 个边框填充任务），`imendJob(IM_SYNC)` 完成后继续下一批。相同颜色的覆盖写入可交换，重叠框不依赖执行先后；没有混色或异步返回。未提交任务遇错由 RAII 取消，硬件错误仍向调用者传播。公开 ABI 和独占目标 DMA 生命周期不变，推理 resize/CSC 的每任务双 RGA3 策略不变。

独立 `native/tests/draw_batch_benchmark.cpp` 同时保留旧同步逐框调用与新公开入口，像素初始化和绘图均由 RGA 完成。CPU 只读映射及 DMA-BUF READ sync 只用于测试逐字节对照，不进入生产像素路径。RGB888、RGBA8888、NV12 的 640×480 与有行 padding 的 642×482，各 37–38 个普通、重叠、填充及边缘小框，所有可见字节相同，padding 均未改写。额外 12 个 RGB/RGBA origin-one 小框与独立硬件 marker 参考完全相同。原有拒绝证据保留在 `draw-batch-existing-odd-tiny-failure.log`，原点 0/1/2/3 的矩阵证据在 `draw-tiny-matrix.log`，没有将该边界静默排除。

2560×1440 RGB888，10 次 warmup + 100 次测量，仅绘框调用的平均耗时如下；不是完整流水线吞吐或单帧推理延迟：

| 同色框数 | 旧逐框同步 ms | 新 job8 ms |
|---|---:|---:|
| 1 | 0.706 | 0.422 |
| 3 | 1.639 | 0.566 |
| 8 | 4.029 | 0.961 |
| 32 | 15.584 | 3.017 |

板端实际命令为 `cmake --build build -j2`、`./build/rknnsharp_draw_batch_benchmark`、`RKS_DIAGNOSTIC_READBACK=1 ./build/rknnsharp_hardware_smoke $MODEL_PATH`，退出均为 0。生产目标使用 `-Wall -Wextra -Werror` 构建通过；直接包含实现的诊断目标仅有匿名 namespace 字段的 subobject-linkage 警告。安全 smoke 的 stride/实际 allocation/失败输出失效/量化/释放检查通过，FD warm 8→final 8。

候选再按上节命令执行真实六源 `concurrency_probe ... fixed`：270 个请求的 signed 输入和三个完整 raw INT8 输出逐字节相同，六源 allocation 指纹均不变。日志位于 `.artifacts/hardware/{build-draw-batch,draw-batch-production,smoke-draw-batch,concurrency-draw-batch}.log`。当前产物 SHA256 为 `09971b537e2798947794bb24cb5a00973cc63089e69814b77287a7bf2f15c95e`，ABI 2；旧基线二进制保留为 `.artifacts/hardware/librknnsharp_native-baseline-e0b09.so`。完整含 VPU 的吞吐和严格检测对照由整合 sample 单独记录，不用本节绘框数字估算。

最终增加测试专用 `fixed6` 模式覆盖每核两个独立 context：`./concurrency_probe $MODEL_PATH $RTSP_URL_FILE fixed6`。六个 session 的 core mask 为 1/2/4/1/2/4，仍各自拥有 signed DMA 与输出；六个真实 PAUSED 源先按旧串行 RGA3 参考建立 baseline，再执行 180 个同源并发与 360 个混源轮转请求。540 个请求的 signed 输入及三个完整 raw 输出全部逐字节相同，六源 allocation 首尾 hash 不变，退出 0，最终 so hash 仍为上述 `09971b...`。证据为 `.artifacts/hardware/concurrency-six-session-final.log`；这是数值诊断，不作吞吐计时。

板端 1.5.2 和当前官方公开 C API 中未发现 CUDA Graph 类的 capture/replay graph-executable 接口。`.rknn` 已是预编译模型；本库持久化 context、预分配并绑定 DMA I/O，然后重复 `rknn_run`，已经避免逐帧重建模型和输入缓冲。这不等于能捕获 VPU→RGA→NPU 的跨设备图。异步 flag 是不同的结果/调度契约，不能透明替换当前同步 Detect。

尚未找到足够官方依据保证当前 compiler1.5.0 模型在较新 2.x runtime 上直接获得多核或性能收益，因此没有下载新 runtime 或进行猜测性的版本 A/B，也没有替换现有运行库。

用户提供的 [yolo26-rk3588-cpp](https://github.com/li2390893/yolo26-rk3588-cpp/tree/db83c751b2eb55998b88f06084e6b294e1573739) 已按固定 commit 只读对照。其实际构建应用采用单 context 同步运行，没有线程池/显式核分配；使用 OpenCV 的 padding、颜色转换、resize、memcpy 和绘框，不能直接移植为当前要求的硬件像素路径。其 [video FPS 标签](https://github.com/li2390893/yolo26-rk3588-cpp/blob/db83c751b2eb55998b88f06084e6b294e1573739/src/yolo26_video.cpp#L84-L105) 需要区分循环吞吐日志和来源于 `CAP_PROP_FPS` 的视频源标签；[Inference 计时](https://github.com/li2390893/yolo26-rk3588-cpp/blob/db83c751b2eb55998b88f06084e6b294e1573739/src/task/yolo26.cpp#L213-L236) 也包含输入设置、输出取得及复制。它使用不同的 YOLO26 n/s 模型和后处理输出结构，[README](https://github.com/li2390893/yolo26-rk3588-cpp/blob/db83c751b2eb55998b88f06084e6b294e1573739/README.md#L8-L12) 还记录了原始模型 INT8 推理待解决的问题，不能用作当前 YOLOv5s 同模型、同输入、同阈值的性能基准。未读取大型权重，未推测模型激活替换或复现其外部视频数字。

证据保存在本地 `.artifacts/hardware/{environment,probe,smoke,diagnostic}.log`，以及板端隔离目录。最终 AArch64 桥接二进制位于 `.artifacts/hardware/linux-arm64/librknnsharp_native.so`；环境日志包含 SHA256 与真实依赖解析路径。该构建的 loader 路径引用测试设备现有 SDK，其他机器应使用自己匹配的运行库重新构建或显式配置 loader，不复制本机路径假装通用部署。

## 官方依据与验证边界

- [Rockchip RKNN-Toolkit2 / rknpu2](https://github.com/airockchip/rknn-toolkit2) 与 [官方 C API 头文件](https://github.com/airockchip/rknn-toolkit2/blob/master/rknpu2/runtime/Linux/librknn_api/include/rknn_api.h)：外部 fd 导入、native tensor、pass-through；具体编译以本板 SDK 1.5.2 头文件为准。旧 rknpu2 仓库已迁移，不将官方主分支版本当成本板版本。
- [Rockchip librga 开发指南](https://github.com/airockchip/librga/blob/main/docs/Rockchip_Developer_Guide_RGA_CN.md)：DMA buffer 导入、stride、同步、ROP 和矩形绘制；以板端 1.10.1 API 头与实测结果为准。
- [Rockchip MPP](https://github.com/rockchip-linux/mpp)：VPU 部分由 sample 的媒体 adapter 验证，模型语义与 sigmoid/anchors 见 [model-contract.md](model-contract.md)。

真实 RTSP 的 VPU→DMA-BUF→本桥接→C# 后处理综合验收由 sample 整合测试单独记录。原生 smoke 不包含摄像头内容或标注集；当前不能宣称目标域准确率、mAP 或所有 Rockchip/BSP/模型版本兼容。失败时保留错误和资源收尾，用户可停止使用本任务产物；本次未改系统组件和已有模型，无系统版本回退步骤。


