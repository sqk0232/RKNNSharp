#include "rknn_float_output.h"
#include <array>
#include <cstdio>
#include <cstring>
#include <limits>
#include <stdexcept>
#include <vector>
static void require(bool ok,const char* what){if(!ok)throw std::runtime_error(what);}
int main(){try{
    rknn_tensor_attr a{};a.n_dims=4;a.dims[0]=1;a.dims[1]=3;a.dims[2]=5;a.dims[3]=17;
    a.n_elems=255;a.size=255;a.fmt=RKNN_TENSOR_NCHW;a.type=RKNN_TENSOR_INT8;
    a.qnt_type=RKNN_TENSOR_QNT_AFFINE_ASYMMETRIC;a.zp=-128;a.scale=1.f/255;
    require(rks_detail::can_dequantize_int8(a),"valid head rejected");
    unsigned rejected=0;
    auto reject=[&](rknn_tensor_attr invalid){require(!rks_detail::can_dequantize_int8(invalid),"unsupported head selected for fast conversion");rejected++;};
    for(auto fmt:{RKNN_TENSOR_NHWC,RKNN_TENSOR_NC1HWC2,RKNN_TENSOR_UNDEFINED}){auto x=a;x.fmt=fmt;reject(x);}
    for(auto type:{RKNN_TENSOR_FLOAT32,RKNN_TENSOR_FLOAT16,RKNN_TENSOR_UINT8,RKNN_TENSOR_INT16,RKNN_TENSOR_INT32}){auto x=a;x.type=type;reject(x);}
    for(auto qnt:{RKNN_TENSOR_QNT_NONE,RKNN_TENSOR_QNT_DFP}){auto x=a;x.qnt_type=qnt;reject(x);}
    for(float scale:{0.f,-1.f,std::numeric_limits<float>::infinity(),std::numeric_limits<float>::quiet_NaN()}){auto x=a;x.scale=scale;reject(x);}
    for(int zp:{-129,128}){auto x=a;x.zp=zp;reject(x);}
    {auto x=a;x.size=256;reject(x);}
    {auto x=a;x.n_elems=0;reject(x);}
    {auto x=a;x.n_dims=0;reject(x);}
    {auto x=a;x.n_dims=17;reject(x);}
    {auto x=a;x.dims[3]=18;reject(x);}
    {auto x=a;x.dims[3]=0;reject(x);}
    {auto x=a;x.dims[0]=0xffffffffu;reject(x);}
    unsigned cases=0;
    for(int zp:{-128,-1,0,46,83,127})for(float scale:{1.f/255.f,.0931356847f,.5f,1.f,1e6f,std::numeric_limits<float>::min(),std::numeric_limits<float>::denorm_min(),std::numeric_limits<float>::max()/512.f})
        for(size_t count:{size_t(1),size_t(15),size_t(16),size_t(17),size_t(31),size_t(255),size_t(256),size_t(257)}){
            std::vector<int8_t> source(count+2);std::vector<float> output(count+2,-111.f);
            for(size_t i=0;i<count;i++)source[i+1]=static_cast<int8_t>(int(i%256)-128);
            rks_detail::dequantize_int8(source.data()+1,output.data()+1,count,zp,scale);
            require(output.front()==-111.f&&output.back()==-111.f,"output tail overwritten");
            for(size_t i=0;i<count;i++){
                volatile float expected=float(int(source[i+1])-zp)*scale;
                require(std::memcmp(&output[i+1],const_cast<const float*>(&expected),sizeof(float))==0,"SIMD/scalar IEEE arithmetic mismatch");
            }
            cases++;
        }
    std::printf("float_output_unit passed: rejected=%u unsupported descriptors; arithmetic=%u zero-point/scale/tail/unaligned cases; SIMD is bitwise scalar IEEE affine, not SDK exact\n",rejected,cases);
    return 0;
}catch(const std::exception& e){std::fprintf(stderr,"%s\n",e.what());return 1;}}
