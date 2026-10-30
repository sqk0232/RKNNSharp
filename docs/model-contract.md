# Supplied RK3588 YOLOv5 model contract

Observed on 2026-10-29. This contract applies only to the supplied binary, not all YOLOv5 exports. The library should expose class IDs; class names and anchors belong to explicit model configuration.

## Identity and evidence

| Item | Observed value |
| --- | --- |
| Model | `$MODEL_PATH` (read only) |
| Bytes | 8,502,205 |
| SHA-256 | `5484cccbcc30292a167b2f9d7cf7cb06210decf4343e41f59e35c0ee41785f1e` |
| Embedded compiler | `1.5.0+1fa95b5c`, compiler `1.5.0 (e6fe0c678@2023-05-25T16:15:03)` |
| Embedded source/target | ONNX / RK3588; static shape; `dynamic_shapes={}` |
| Runtime reported by hardware smoke | RKNN API `1.5.2 (c6b7b351a@2023-08-23T15:28:22)`, driver `0.9.8`; RGA API `1.10.1_[3]` |

The original PyTorch/ONNX model, exporter revision, training dataset, custom anchors, label order, and weight license were not supplied. A filename and a 255-channel head do not establish those facts. No weights were downloaded, converted, trained, or overwritten. Python and PyTorch are not runtime dependencies of this C# inference path; original training software versions remain unknown. CUDA is not used on RK3588.

## Input and preprocessing

Embedded source input `images`: float32 NCHW `[1,3,640,640]`, range `[0,1]`, mean `[0,0,0]`, standard deviation `[255,255,255]`, `rgb2bgr=False`. The source layout differs from the native runtime layout.

The platform Worker's runtime query reports native input INT8 NHWC `[1,640,640,3]`, affine zero point `-128`, scale approximately `0.00392157`, 1,228,800 bytes, width stride 640. Re-query all exact parameters in the runtime; do not hardcode rounded values from this document.

For native pass-through input, RGB unsigned bytes must become signed quantized bytes `q = u - 128`, preserving byte bits through XOR `0x80`. This follows `q = round((u/255)/scale) + zeroPoint` for this input's scale. Division by 255 must not be applied a second time. The final hardware implementation uses RGA ROP over an RGB565 word reinterpretation for the XOR; the earlier RGBA reinterpretation was discarded because alpha handling changed every fourth byte. The final diagnostic checked 384/384 quantized boundary bytes across first/middle/last rows against an independent RGA signed-stripe reference, and all three NPU outputs matched that reference with `maxdiff=0, unequal=0`. These are input quantization/reference checks for the tested data, not original-model export parity. A normal `rknn_inputs_set` UINT8 input with conversion enabled is a different API path from native pass-through.

RGB is the intended integration profile, consistent with official YOLOv5 input handling and the absence of compiler RGB/BGR swapping. Its correctness against the unavailable original model is not independently proven. VPU decoding and RGA color conversion/resizing/padding/drawing are required by this task; CPU image processing or software decode fallback is prohibited.

NV12 YUV matrix/range must be explicit per input frame. The native contract distinguishes BT.601 limited, BT.601 full and BT.709 limited. The RTSP smoke used an explicit BT.601 full override (`capsColorSpace=0`, `selectedColorSpace=2`); the media investigation established full range from SPS, but did not establish the matrix. Therefore BT.601 is a **test assumption**, not a verified camera matrix or a model property. The RGB signed-stripe reference does not exercise YUV-to-RGB color correctness.

Use an explicit letterbox transform. For original `W×H`, choose resized integer dimensions, actual `scaleX=resizedW/W`, `scaleY=resizedH/H`, and integer left/top padding. Keep those exact values with the frame. Suggested profile is centered padding RGB `(114,114,114)` to 640×640; this is an integration choice, not a property recoverable from this binary. RGA alignment/rounding must be reflected in the recorded transform. Stretch resize is only correct if the corresponding independent x/y scales and zero padding are used in restoration.

## Output attributes

The platform Worker queried standard output attributes as INT8 affine NCHW:

| Index/name | Shape | Elements | Zero point | Scale (rounded) | Embedded calibration range |
| --- | --- | ---: | ---: | ---: | --- |
| 0 / `269` | `[1,255,80,80]` | 1,632,000 | 83 | 0.0931357 | -19.61631584 to 4.13328457 |
| 1 / `271` | `[1,255,40,40]` | 408,000 | 48 | 0.0898542 | -15.84555626 to 7.06725216 |
| 2 / `273` | `[1,255,20,20]` | 102,000 | 46 | 0.0786304 | -13.65917969 to 6.39156151 |

Embedded layout declarations agree with these standard attributes. Native output memory may have a different packed layout; use standard `rknn_outputs_get`/an explicitly verified bridge conversion. Do not reinterpret native blocked buffers as contiguous NCHW.

For raw INT8 output, dequantize each head with its own queried parameters: `t = (signedValue-zeroPoint)*scale`. If the runtime already returns float with `want_float=1`, do not dequantize twice. Check output size/layout/type before reading. The model does not expose final `[N,85]` boxes or NMS results.

## Decode profile and uncertainty

The final constant-114 RGB hardware smoke returned these dequantized float values from standard NCHW outputs:

| Head | Measured range | Negative values / elements | Non-finite values |
| --- | --- | --- | ---: |
| 0 | -17.78892 to 2.700935 | 1,576,804 / 1,632,000 | 0 |
| 1 | -15.81433 to 3.234749 | 393,793 / 408,000 | 0 |
| 2 | -13.68168 to 3.774257 | 98,979 / 102,000 | 0 |

The objectness channel for anchor 0 is also negative on all three heads (ranges -17.32324 to -10.4312, -15.81433 to -9.344831, and -13.68168 to -9.750164). These outputs are not probabilities. Together with embedded metadata and the compiler-matched [official rknpu2 v1.5.0 postprocess](https://github.com/rockchip-linux/rknpu2/blob/v1.5.0/examples/rknn_yolov5_demo/src/postprocess.cc), this confirms the selected raw-logit plus sigmoid contract. The bridge requests `rknn_outputs_get` with `want_float=1`; C# must not dequantize those floats again. In contrast, [current Model Zoo postprocess](https://github.com/airockchip/rknn_model_zoo/blob/main/examples/yolov5/cpp/postprocess.cc) consumes probabilities. These contracts are different. There is still no original ONNX/PyTorch numerical parity baseline.

For the explicit three-anchor YOLOv5 profile, `255=3*(5+80)` gives 80 class channels. For anchor `a`, attribute `k`, grid row `y`, column `x`, contiguous NCHW offset is `((a*85+k)*gridH+y)*gridW+x`. Attributes are `tx,ty,tw,th,obj,class0..class79`. This ordering is the selected official YOLOv5 profile and must be verified when accepting another binary.

Set `s(v)=1/(1+exp(-v))`, with a numerically stable sigmoid for extreme values. For each head:

```text
centerX = (2*s(tx)-0.5+x)*strideX
centerY = (2*s(ty)-0.5+y)*strideY
width   = (2*s(tw))^2*anchorWidth
height  = (2*s(th))^2*anchorHeight
classId = argmax(class logits)       # ties: lowest index
score   = s(obj)*s(classLogit[classId])
x1,y1,x2,y2 = centerX±width/2, centerY±height/2
```

Strides are `(8,8)`, `(16,16)`, `(32,32)` for the observed 640×640 input. Apply sigmoid exactly once, including objectness and class channels. Filter on the final product, not only the individual terms. For example, objectness 0.4 and class probability 0.4 yield score 0.16, which fails a 0.25 threshold.

The official default anchors below are an **explicit example assumption**, not recovered model metadata. Make them configurable and validate three heads with three positive width/height pairs each:

```text
80×80: (10,13), (16,30), (33,23)
40×40: (30,61), (62,45), (59,119)
20×20: (116,90), (156,198), (373,326)
```

Keep labels optional and return `ClassId` 0–79. Do not silently label these channels COCO without model provenance or a reference comparison. These defaults can be used for a visibly annotated demonstration; a different model requires its own profile.

Use score-descending class-aware NMS with configurable threshold and result limit. Define continuous-coordinate IoU without integer pixel `+1`; this is the library's explicit convention. Some official C++ examples use `+1`, so bit-for-bit boundary parity with those examples is not claimed. Suppress only boxes of the same class when IoU exceeds the threshold; make equal-score ordering deterministic. Reject non-finite/invalid boxes. Suggested demonstration thresholds are confidence 0.25 and NMS IoU 0.45, both configurable.

Restore to the source image using `(x-padLeft)/scaleX` and `(y-padTop)/scaleY`, then clip continuous coordinates to `[0,W]×[0,H]`. Do not clip to the letterbox content before removing padding. Preserve floating-point coordinates until the RGA drawing boundary; define integer conversion there. Empty detections are a valid result and do not prove accuracy.

## Focused acceptance cases for the C# implementation

- Validate each runtime tensor shape, element count, signed type, finite positive scale and affine parameters. Reject mismatched heads, malformed anchor counts, invalid dimensions, truncated buffers and non-finite float outputs.
- Quantization: a raw signed value equal to its head zero point decodes to logit 0 and probability 0.5. Distinct head scales must produce distinct expected logits from the same raw byte; signed `-128` must not be read as 128.
- Decode: isolated zero logits at stride 8, grid `(x=10,y=20)`, anchor `(10,13)` give center `(84,164)`, size `(10,13)`, corners `(79,157.5,89,170.5)`, score 0.25. Use an isolated candidate, because a whole tensor of zeros creates many candidates. Verify a second anchor and non-square anchor values to catch channel/axis swaps.
- Product threshold: verify 0.4×0.4 rejects at 0.25; 0.5×0.5 accepts if threshold comparison is inclusive. Test extreme logits without overflow and class ties with deterministic selection.
- NMS: same-class identical boxes retain the highest score; different-class identical boxes both remain. Test zero-area boxes, disjoint boxes, equal scores and the exact IoU boundary.
- Restoration: source 1920×1080, resize 640×360, padding left 0/top 140 gives `(100,190,200,290)` → `(300,150,600,450)`. Include odd padding and RGA-aligned dimensions with distinct actual scaleX/scaleY, plus boxes entirely in padding and clipping at source edges.
- Real NPU smoke test: synthetic constant input proves load/run/output access and finite/logit ranges only. A camera or labelled image with expected targets is required for semantic validation, and the unavailable original ONNX/PyTorch output is required for export parity or any accuracy claim. No throughput/accuracy improvement is claimed here.

## Implemented-profile semantic review

Read-only review of `src/RKNNSharp/YoloV5Profile.cs`, `src/RKNNSharp/LetterboxTransform.cs`, `tests/RKNNSharp.Tests/YoloV5Tests.cs` and the restoration/NMS cases in `ContractTests.cs` found no blocking model-semantic issue for this explicit profile. The implementation checks standard affine INT8 head metadata and consumes already-dequantized floats, uses anchor-major/channel-plane NCHW indexing, applies stable sigmoid once, filters on objectness times best-class probability, uses squared width/height anchor decoding, performs stable class-aware continuous-coordinate NMS in model coordinates, and finally removes exact padding/scales before clipping to source bounds. Default anchors are explicitly documented as an example assumption; output carries class IDs without assigning unverified labels.

The inspected cases cover the zero-logit coordinate/score example, another anchor and class 79, product rejection, extreme logits, malformed/non-finite tensors, head order, class ties, padding removal, independent scales and stable class-aware NMS including IoU=1 boundary behavior. This review did not run or add tests; the integration Worker owns final build/test evidence. The original float `Decode` receives the RKNN float output contract; the later `Detect` fast path and its raw dequantization tests are recorded below.

The recorded RTSP integration smoke processed 30 frames and produced 295 post-NMS detections at confidence 0.25, with explicit official640 example anchors and no label names. This demonstrates that the hardware-to-C# detection path produces results on the supplied stream. It does not establish correct anchors/labels, camera color-matrix correctness, labelled accuracy, or parity with the unavailable source model. A later final integration check may have a different frame count; preserve each run's own log and statistics rather than combining them.

## Performance-compatible raw INT8 contract

Standard raw INT8 output plus a per-head lookup table has now been implemented for `Detect` through `DecodeInt8`; `Run` retains float snapshots. The requirements below remain the compatibility contract. The measured acceptance evidence follows separately, and segmented performance measurements belong to the integration Worker. Keep the original model, input buffer, anchors, class count, confidence/NMS thresholds, maximum detections, candidate order and full detection region unchanged.

The default `SdkExact` mode retains SDK float conversion as the reference for `Run`/`RunInto`. A separately selected `PreferFastInt8` mode may use affine INT8 conversion for eligible standard heads and fall back to SDK conversion for other heads; it does not change `Detect`. Its rounding differences and limits must be evaluated separately, not presented as universal bitwise or decision equivalence. The historical comparisons below do not establish an error bound for every model, scale or threshold. See [the explicit mode contract](api.md).

The [version-matched RKNN API header](https://github.com/rockchip-linux/rknpu2/blob/v1.5.0/runtime/RK3588/Linux/librknn_api/include/rknn_api.h) distinguishes standard output attributes from native output attributes and exposes `rknn_output.want_float`. Before consuming `want_float=0`, the platform Worker must verify that `rknn_outputs_get` yields contiguous standard NCHW INT8 for this runtime/model, with exactly 1,632,000 / 408,000 / 102,000 valid elements. A native NC1HWC2 buffer requires its own verified unpacking; dimensions or total allocated bytes alone do not prove standard order. Use queried standard `size`/element counts and observe output allocation/release lifetime. Changing float conversion cannot assume that packed-layout conversion or all copying disappears.

For each head, build a 256-entry **float** table from the exact queried scale and zero point. With `q` in signed range `[-128,127]`, use `index=q+128`, `logit=((float)q-(float)zeroPoint)*scale`, and the existing stable `Sigmoid(logit)`. Cast raw bytes to signed INT8 before indexing/comparing; byte 0x80 represents -128. Cache against the complete head quantization contract, and rebuild if scale, zero point or model changes. Use the current floating-point operation sequence; half precision, approximate exp or a double-intermediate variant needs separate equivalence evidence.

Select the best class by comparing signed quantized logits directly, preserving the first/lowest class ID on equal values. For this model's positive normal scales, affine dequantization is order preserving across all 256 values. Comparing rounded sigmoid table entries for argmax can introduce extra ties when probabilities saturate, so retain the signed-logit comparison. Recheck order preservation across all entries before generalizing to unusual quantization scales.

Use table objectness for the same early rejection `objectness < confidenceThreshold`; a probability product cannot exceed its objectness factor. Then compute the existing float product `objectness * table[bestClassQ+128]` and reject only when `score < confidenceThreshold`. This preserves the inclusive acceptance boundary. A precomputed minimum objectness integer may be derived by scanning the actual float table for its first accepted value, with explicit empty/all-accepted cases. An unsigmoid threshold followed by integer truncation/rounding is not a sufficient equivalence argument, especially near a representable boundary. A 256×256 product table is unnecessary unless measurement justifies it and its float multiplication/ordering remains identical.

Decode position and size from the same table, retaining the current arithmetic order, candidate traversal, continuous-coordinate NMS, stable score ties, restoration and clipping. Zero-point entries still produce logit zero/probability 0.5. The raw path can avoid materializing every float tensor, but its public buffer contract and ownership must remain explicit.

Acceptance should compare the two paths on the **same captured input and matching inference outputs**, without launching competing NPU jobs. For each head, compare all raw values dequantized with its own parameters against the existing `want_float=1` result; record exact mismatch count, maximum absolute difference and any ULP difference. Prefer obtaining both output representations from the same completed run if the runtime permits it; otherwise prove deterministic paired inference before attributing differences to conversion. A single synthetic output/reference comparison does not replace representative camera evidence.

Then compare pre-NMS candidate IDs/order, selected class IDs, float confidences and model-space boxes; compare NMS retained candidate indices/order and final restored boxes. Candidate identity is `(head, anchor, row, column, classId)`. Require identical candidate inclusion, class IDs and NMS decisions. If runtime dequantization differs by rounding, quantify the float differences and explicitly examine confidence and IoU boundary cases before accepting tolerances; an epsilon must not hide different detections. Cover q=-128/127/zeroPoint, all three head quantizers, signed ordering, class ties, confidence equality and adjacent representable scores, equal-score NMS, and IoU exactly at/around the configured threshold. Report segmented timing using the same model/input/options and comparable warm-up, separately from numerical evidence. No accuracy improvement follows from replacing float tensors with raw INT8 decoding.

## Implemented INT8 acceptance evidence and limits

Targeted read-only review of `YoloV5Profile.DecodeInt8` and `LookupSet` found no blocking model-semantic issue for the supplied model/profile. All three signed buffers use `q+128` indexing into their own 256-entry table. Class selection compares signed q with strict `>` and therefore preserves the lowest ID on equal logits; it does not compare saturated table probabilities. The confidence product, inclusive threshold, anchor/channel traversal, center/size arithmetic and shared `Finish` NMS/restoration path match `Decode`. Every possible dequantized LUT value is checked for finiteness, allowing the raw tensor path to omit repeated per-element NaN/Inf scans without omitting any representable quantized value.

The shared LUT cache is constructed completely before `Volatile.Write`; each decoding call keeps its chosen immutable `LookupSet` in a local reference. Scales and zero points are copied into the set, and its tables are not mutated after construction. Concurrent use of one profile by three model workers can publish duplicate sets or replace the cached set when quantization changes, but an in-flight decoder retains the set matching its own validated metadata. There is no shared candidate list. This reasoning assumes the internal call's tensor metadata/input buffers remain stable for the decoding call, as required by the borrowed-output contract. It is a model-cache review, not a general audit of native/session scheduling.

The hardware Worker verified `want_float=0` standard NCHW element counts on two runs, generations 5 and 6, during the historical board diagnostic. Each run compared the full head against float output:

| Head | Valid raw elements | Maximum absolute float difference | Unequal float elements | Inverse-quantized q errors |
| --- | ---: | ---: | ---: | ---: |
| 0 | 1,632,000 | 9.53674e-7 | 146,112 | 0 |
| 1 | 408,000 | 0 | 0 | 0 |
| 2 | 102,000 | 9.53674e-7 | 14,400 | 0 |

This verifies standard ordering and quantized values for the tested output path; the float representation is **not bit exact**. The diagnostic records large ULP distances near zero from small runtime residuals (heads 0/2: 855,638,016 / 864,026,625); the maximum absolute error does not justify a claim of a one-ULP bound. The final native library SHA-256 reported with this evidence is `99afc2f6824758f6525497406cd790e713f7bb559c601f3b54085f6068acd305`.

Historical board validation included 12 paired frame comparisons, including two warm-up frames and ten measured frames. All 12 preserved final detection count, class ID and ordering. Maximum confidence difference was `2.9802322e-8`; maximum restored source-coordinate difference was `0.00012207031` pixels. The smoke comparison enforces score tolerance `1e-5` and coordinate tolerance `0.01` source pixels. It compares final results; its log does not expose every pre-NMS candidate or the retained original candidate indices. The compare mode executes both paths and is numerical validation evidence, not the optimized path's standalone frame-time benchmark.

The observed float differences can affect confidence threshold inclusion, score ordering or NMS IoU decisions for values sufficiently close to a boundary. Therefore the evidence supports agreement on these tested frames within the stated numeric tolerances, not universal bitwise equivalence or a guarantee of identical detections for every possible input. Retain the float path as a reference. Anchors/labels and the BT.601 matrix assumption remain subject to the earlier limitations.

The reviewed `Int8DecodeTests.cs` covers 20 random seeds across three different head quantizers against the float decoder, exact zero-point score 0.25 inclusion, signed class selection, saturation without false argmax ties, truncated buffers and non-finite LUT overflow. The existing suite log at review time reports 14/14 passing tests. This reviewer ran no tests/NPU jobs and changed no C#. Explicit equal-q class ties, thresholds immediately above/below a representable score, and interleaved/concurrent reuse of one profile across changing quantization were requested from the implementation Worker as focused supplemental cases; randomized parity alone is not evidence for those exact boundaries.

## Reproduce metadata evidence

```sh
sha256sum "$MODEL_PATH"
strings "$MODEL_PATH" | head -n 12
python3 tools/model-probe/extract_metadata.py "$MODEL_PATH" --output "$METADATA_OUTPUT"
```

Set `METADATA_OUTPUT` to a fresh output file path before running the command. The extraction script uses `ast.literal_eval` on embedded metadata, never executes model contents, and refuses to overwrite an existing evidence output. Use a fresh output name when repeating it. Embedded metadata extraction is not a supported RKNN API and does not replace runtime query. The first evidence file was produced on the board by the same read-only extraction algorithm in an inline Python command.

The checked-in script was run on the test board with `python3`. Actual-model SHA-256/size/head-shape assertions passed; attempting to extract the script file itself correctly raised `ValueError` for unrecognized metadata. Both checks exited 0. These historical checks cover evidence extraction, not neural-network numerical correctness.

Official sources checked: [v1.5.0 decode source](https://github.com/rockchip-linux/rknpu2/blob/v1.5.0/examples/rknn_yolov5_demo/src/postprocess.cc), [current Model Zoo output-layout explanation](https://github.com/airockchip/rknn_model_zoo/tree/main/examples/yolov5), [conversion normalization configuration](https://github.com/airockchip/rknn_model_zoo/blob/main/examples/yolov5/python/convert.py). The cited decode source has an Apache-2.0 header; that does not establish the supplied binary's weight license. Obtain the original model/exporter metadata and license before redistributing weights.


