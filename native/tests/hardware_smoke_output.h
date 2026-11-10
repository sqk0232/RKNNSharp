#ifndef RKS_HARDWARE_SMOKE_OUTPUT_H
#define RKS_HARDWARE_SMOKE_OUTPUT_H

#include "rknnsharp.h"
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <limits>
#include <vector>

namespace rks_test {

// rks_copy_output supplies floats even when the tensor metadata describes INT8.
// The caller supplies the SDK's NCHW enum value; no vendor runtime is needed here.
inline bool print_float_output(std::FILE* stream,unsigned index,const rks_tensor& tensor,
                               const std::vector<float>& data,int32_t nchw_layout){
    if(tensor.n_dims==0||tensor.n_dims>16||tensor.element_count==0||data.empty()){
        std::fprintf(stream,"output%u invalid or empty tensor\n",index);return false;
    }
    size_t count=1;
    for(unsigned i=0;i<tensor.n_dims;i++){
        if(tensor.dims[i]==0||count>std::numeric_limits<size_t>::max()/tensor.dims[i]){
            std::fprintf(stream,"output%u invalid or overflowing dimensions\n",index);return false;
        }
        count*=tensor.dims[i];
    }
    if(count>std::numeric_limits<size_t>::max()/sizeof(float)
       ||count!=tensor.element_count||count!=data.size()){
        std::fprintf(stream,"output%u dimensions/element count/buffer mismatch\n",index);return false;
    }
    size_t negative=0,nonfinite=0;
    for(float value:data){negative+=value<0;nonfinite+=!std::isfinite(value);}
    auto range=std::minmax_element(data.begin(),data.end());
    std::fprintf(stream,"output%u range=[%.7g,%.7g] negative=%zu nonfinite=%zu\n",
                 index,*range.first,*range.second,negative,nonfinite);
    if(tensor.n_dims==4&&tensor.layout==nchw_layout){
        size_t spatial=size_t(tensor.dims[2])*tensor.dims[3];
        for(unsigned channel=0;channel<std::min(6u,tensor.dims[1]);channel++){
            auto first=data.begin()+channel*spatial;
            auto channel_range=std::minmax_element(first,first+spatial);
            std::fprintf(stream," channel%u [%.7g,%.7g]\n",channel,*channel_range.first,*channel_range.second);
        }
    }else{
        std::fprintf(stream," channel ranges unavailable for rank=%u layout=%d (requires 4D NCHW)\n",
                     tensor.n_dims,tensor.layout);
    }
    return nonfinite==0;
}

} // namespace rks_test
#endif
