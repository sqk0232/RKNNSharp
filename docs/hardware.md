# Rockchip 原生硬件后端

当前后端已经在 LubanCat-5 的 **RK3588（非 RK3588S）** 上通过原生构建、RGA/NPU 正常与失败路径测试。媒体拉流属于 sample adapter，不进入本库。后端不含软件解码、CPU 图像预处理或 CPU 绘框回退。

版本记录分阶段: 本页早期 DMA/媒体链路使用 Runtime 1.5.2; 后续 Runtime 2.3.2 已有 Run/RunInto、YOLOv5 和指定 YOLOv8n 的实测记录, 驱动仍为 RKNPU 0.9.8、RGA 1.10.1_[3]. 指定模型边界见[DFL检测契约](yolo-detection.md). 这些记录不代表所有 2.x、模型或媒体链路组合均已验证. 编译头文件须与目标 Runtime 匹配, 模型 Toolkit/Runtime/BSP 驱动需兼容, 不要求它们版本号数字相同. 本文不代表对其他组合新增硬件验证.

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

以下为 2026-10-29 的特定设备记录。私有模型与开发日志不随源码提供，也不是构建前提；首次使用请按上面的变量化步骤。历史 hash 只标识当时实测产物，不要求读者取得这些文件或构建出相同 hash，性能数字不构成其他环境的保证。

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

各模型的输入、推理耗时与完整链路吞吐见[测试数据](../README.md#测试数据)。这些是特定 RK3588 环境的历史测量，不能将纯 NPU 执行时间换算为摄像头端到端吞吐。

## 并发预处理与绘框约束

当前推理 resize/CSC 对每次 `improcess` 显式选择两个已验证等价的 RGA3 核和水平/垂直 LINEAR 插值，避免依赖全局或线程调度状态；fill/ROP 使用经过验证的路径。不同 NPU session 保持独立并发，缺少兼容 RGA3 或 geometry 时明确失败。此选择只针对已验证的 RK3588/BSP，不保证其他 SoC 自动等价。

同色矩形最多每组 8 个合并为同步 RGA job，必须在硬件操作返回后才能复用图像；普通框、行 padding 与边缘小框均有独立对照。单像素 marker 和 origin-one 几何调整的规则见前面的生命周期与失败行为，不能将它解释为任意尺寸 RGA2 CSC 已等价。

相关诊断源码为 `native/tests/concurrency_probe.cpp`、`draw_batch_benchmark.cpp` 和 `run_output_regression.cpp`。并发诊断直接包含 native 内部实现，未纳入标准 CMake 目标；它不是首次使用的前提。诊断程序中的只读像素校验不进入生产图像处理路径。

## 官方依据与验证边界

- [Rockchip RKNN-Toolkit2 / rknpu2](https://github.com/airockchip/rknn-toolkit2) 与 [官方 C API 头文件](https://github.com/airockchip/rknn-toolkit2/blob/master/rknpu2/runtime/Linux/librknn_api/include/rknn_api.h)：外部 fd 导入、native tensor、pass-through；具体编译以本板 SDK 1.5.2 头文件为准。旧 rknpu2 仓库已迁移，不将官方主分支版本当成本板版本。
- [Rockchip librga 开发指南](https://github.com/airockchip/librga/blob/main/docs/Rockchip_Developer_Guide_RGA_CN.md)：DMA buffer 导入、stride、同步、ROP 和矩形绘制；以板端 1.10.1 API 头与实测结果为准。
- [Rockchip MPP](https://github.com/rockchip-linux/mpp)：VPU 部分由 sample 的媒体 adapter 验证，模型语义与 sigmoid/anchors 见 [model-contract.md](model-contract.md)。

真实 RTSP 的 VPU→DMA-BUF→本桥接→C# 后处理综合验收由 sample 整合测试单独记录。原生 smoke 不包含摄像头内容或标注集；当前不能宣称目标域准确率、mAP 或所有 Rockchip/BSP/模型版本兼容。硬件失败时应保留错误信息并完成资源释放，不以修改系统组件绕过契约检查。

