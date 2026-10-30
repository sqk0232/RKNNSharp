#pragma once
#include <rknn_api.h>
#include <cmath>
#include <cstdint>
#include <cstddef>
#if defined(__aarch64__)
#include <arm_neon.h>
#endif

namespace rks_detail {
inline bool can_dequantize_int8(const rknn_tensor_attr& a) noexcept {
    if(a.fmt!=RKNN_TENSOR_NCHW||a.type!=RKNN_TENSOR_INT8||a.qnt_type!=RKNN_TENSOR_QNT_AFFINE_ASYMMETRIC||
       !a.n_dims||a.n_dims>16||!a.n_elems||a.size!=a.n_elems||a.zp < -128||a.zp>127||!(a.scale>0)||!std::isfinite(a.scale))return false;
    uint64_t elements=1;
    for(uint32_t i=0;i<a.n_dims;i++){
        if(!a.dims[i]||elements>a.n_elems/a.dims[i])return false;
        elements*=a.dims[i];
    }
    return elements==a.n_elems;
}

// Source is owned, standard NCHW INT8 returned by outputs_get, never the
// native NC1HWC2 tensor memory. Float destinations are separate caller/session
// buffers; conversion writes exactly count values and does not retain them.
inline void dequantize_int8(const int8_t* source,float* destination,size_t count,int32_t zero_point,float scale) noexcept {
    size_t i=0;
#if defined(__aarch64__)
    const int32x4_t zp=vdupq_n_s32(zero_point);
    for(;count-i>=16;i+=16){
        const int8x16_t bytes=vld1q_s8(source+i);
        const int16x8_t low=vmovl_s8(vget_low_s8(bytes)),high=vmovl_s8(vget_high_s8(bytes));
        vst1q_f32(destination+i,vmulq_n_f32(vcvtq_f32_s32(vsubq_s32(vmovl_s16(vget_low_s16(low)),zp)),scale));
        vst1q_f32(destination+i+4,vmulq_n_f32(vcvtq_f32_s32(vsubq_s32(vmovl_s16(vget_high_s16(low)),zp)),scale));
        vst1q_f32(destination+i+8,vmulq_n_f32(vcvtq_f32_s32(vsubq_s32(vmovl_s16(vget_low_s16(high)),zp)),scale));
        vst1q_f32(destination+i+12,vmulq_n_f32(vcvtq_f32_s32(vsubq_s32(vmovl_s16(vget_high_s16(high)),zp)),scale));
    }
#endif
    for(;i<count;i++)destination[i]=static_cast<float>(static_cast<int32_t>(source[i])-zero_point)*scale;
}
}
