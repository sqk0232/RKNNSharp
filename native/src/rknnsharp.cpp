#include "rknnsharp.h"
#include "rknn_float_output.h"
#include <cstddef>
#include <rknn_api.h>
#include <im2d.h>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <climits>
#include <cstring>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <vector>
#include <cerrno>
#include <fcntl.h>
#include <linux/dma-heap.h>
#include <sys/ioctl.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>

// C# sequential layouts, in this order: DmaFrame, NativeTensor, NativeRunInfo, NativeRect.
// AArch64 LP64 natural alignment is 48/364/56/16; the same widths hold on x86_64.
static_assert(sizeof(rks_frame) == 48, "rks_frame ABI size drifted from DmaFrame");
static_assert(sizeof(rks_tensor) == 364, "rks_tensor ABI size drifted from NativeTensor");
static_assert(sizeof(rks_run_info) == 56, "rks_run_info ABI size drifted from NativeRunInfo");
static_assert(sizeof(rks_rect) == 16, "rks_rect ABI size drifted from NativeRect");
static_assert(offsetof(rks_frame, size_bytes) == 24, "rks_frame.size_bytes offset drifted from DmaFrame.SizeBytes");
static_assert(offsetof(rks_run_info, preprocess_ms) == 24, "rks_run_info.preprocess_ms offset drifted from NativeRunInfo.PreprocessMs");

namespace {
thread_local std::string error;
using Clock=std::chrono::steady_clock;
double ms(Clock::time_point a,Clock::time_point b){return std::chrono::duration<double,std::milli>(b-a).count();}
void check(int status,const char* operation){if(status<0)throw std::runtime_error(std::string(operation)+": "+std::to_string(status));}
void rga(IM_STATUS s,const char* op){if(s!=IM_STATUS_SUCCESS&&s!=IM_STATUS_NOERROR)throw std::runtime_error(std::string(op)+": "+imStrError(s));}
void require(bool v,const char* why){if(!v)throw std::runtime_error(why);}
template<class F> int guard(F f){try{f();return 0;}catch(const std::exception& e){error=e.what();return -1;}catch(...){error="unknown native exception";return -1;}}
struct Imported {
    rga_buffer_handle_t handle=0;
    rga_buffer_t image{};
    dev_t device=0;ino_t inode=0;
    Imported(int fd,uint32_t bytes,int w,int h,int stride,int hstride,int format){
        require(fd>=0&&bytes>0&&bytes<=INT_MAX,"invalid DMA fd/size");
        handle=importbuffer_fd(fd,static_cast<int>(bytes));
        require(handle!=0,"RGA importbuffer_fd failed");
        image=wrapbuffer_handle(handle,w,h,format,stride,hstride);
    }
    ~Imported(){if(handle)releasebuffer_handle(handle);}
    Imported(const Imported&)=delete;
};
struct RgaJob {
    im_job_handle_t handle=imbeginJob();
    explicit RgaJob(bool required=true){if(required)require(handle!=0,"RGA imbeginJob failed");}
    ~RgaJob(){if(handle)imcancelJob(handle);}
    void finish(){auto status=imendJob(handle,IM_SYNC);rga(status,"RGA batch drawing");handle=0;}
    RgaJob(const RgaJob&)=delete;
};
int format(const rks_frame& f){
    require(f.reserved==0,"frame.reserved must be zero");
    require(f.offset_bytes==0,"nonzero buffer offset is unsupported");
    require(f.width>0&&f.height>0&&f.width<=8192&&f.height<=8192,"invalid frame dimensions");
    require(f.height_stride>=f.height&&f.height_stride<=8192,"invalid frame height stride");
    uint64_t required=0;
    int fmt=0;
    if(f.format==RKS_NV12){
        require((f.width|f.height|f.row_stride_bytes|f.height_stride)%2==0,"NV12 dimensions/strides must be even");
        require(f.row_stride_bytes>=f.width,"NV12 row stride is too small");
        require(f.color_space>=RKS_BT601_LIMIT&&f.color_space<=RKS_BT709_LIMIT,"NV12 color space must be explicit");
        required=uint64_t(f.row_stride_bytes)*f.height_stride*3/2;fmt=RK_FORMAT_YCbCr_420_SP;
    }else if(f.format==RKS_RGB888||f.format==RKS_RGBA8888){
        uint32_t bpp=f.format==RKS_RGB888?3:4;
        require(f.row_stride_bytes%bpp==0&&f.row_stride_bytes/bpp>=f.width,"RGB row stride must represent whole pixels");
        required=uint64_t(f.row_stride_bytes)*f.height_stride;
        fmt=f.format==RKS_RGB888?RK_FORMAT_RGB_888:RK_FORMAT_RGBA_8888;
    }else throw std::runtime_error("unsupported frame format; only linear NV12/RGB888/RGBA8888");
    require(f.size_bytes>=required&&f.size_bytes<=INT_MAX,"DMA allocation size is too small/large");
    require(f.row_stride_bytes<=32768,"row stride exceeds hardware bound");return fmt;
}
std::unique_ptr<Imported> import(const rks_frame& f){
    int fmt=format(f);int bpp=f.format==RKS_RGB888?3:f.format==RKS_RGBA8888?4:1;
    struct stat allocation{};
    require(fstat(f.fd,&allocation)==0&&allocation.st_size>0,"DMA fd has no verifiable allocation length");
    require(f.size_bytes<=uint64_t(allocation.st_size),"declared DMA size exceeds actual fd allocation");
    auto p=std::make_unique<Imported>(f.fd,static_cast<uint32_t>(f.size_bytes),f.width,f.height,f.row_stride_bytes/bpp,f.height_stride,fmt);
    p->device=allocation.st_dev;p->inode=allocation.st_ino;
    return p;
}
int yuv_to_rgb(uint32_t c){return c==RKS_BT601_FULL?IM_YUV_TO_RGB_BT601_FULL:c==RKS_BT709_LIMIT?IM_YUV_TO_RGB_BT709_LIMIT:IM_YUV_TO_RGB_BT601_LIMIT;}
int rgb_to_yuv(uint32_t c){return c==RKS_BT601_FULL?IM_RGB_TO_YUV_BT601_FULL:c==RKS_BT709_LIMIT?IM_RGB_TO_YUV_BT709_LIMIT:IM_RGB_TO_YUV_BT601_LIMIT;}
// librga 1.10 color-fill reads the destination full-range color space
// (IM_YUV_*_RANGE). The legacy RGB_TO_YUV direction bits used by improcess
// do not select the fill matrix and leave BT.601 limited as the default.
int nv12_fill_color_space(uint32_t c){return c==RKS_BT601_FULL?IM_YUV_BT601_FULL_RANGE:c==RKS_BT709_LIMIT?IM_YUV_BT709_LIMIT_RANGE:IM_YUV_BT601_LIMIT_RANGE;}
void tensor(const rknn_tensor_attr& a,rks_tensor& t){
    t={};t.index=a.index;t.n_dims=a.n_dims;
    for(unsigned i=0;i<std::min(16u,a.n_dims);i++)t.dims[i]=a.dims[i];
    t.element_count=a.n_elems;t.layout=a.fmt;t.data_type=a.type;t.quantization=a.qnt_type;
    t.zero_point=a.zp;t.scale=a.scale;t.size_bytes=a.size;t.width_stride=a.w_stride;t.size_with_stride=a.size_with_stride;
    std::memcpy(t.name,a.name,std::min(sizeof(t.name)-1,sizeof(a.name)));
}
}
struct rks_image {int fd=-1;void* address=MAP_FAILED;size_t length=0;~rks_image(){if(address!=MAP_FAILED)munmap(address,length);if(fd>=0)close(fd);}};
struct rks_session {
    std::mutex gate;
    rknn_context context=0;
    rknn_tensor_attr input{};
    std::vector<rknn_tensor_attr> attrs;
    rknn_tensor_mem *signed_input=nullptr;
    std::unique_ptr<rks_image> rgb_owner,signed_owner;
    std::unique_ptr<Imported> rgb_rga,signed_rga;
    uint32_t w=0,h=0,stride=0;
    std::vector<std::vector<float>> values;
    std::vector<std::vector<int8_t>> quantized_values;
    std::string versions;
    bool has_output=false;
    uint32_t output_encoding=0; // 0 invalid, 1 float snapshot, 2 borrowed INT8
    uint64_t generation=0;
    bool supports_int8=false;
    bool preprocessing_jobs=true;
    bool prefer_fast_float=false;
    dev_t input_device=0;ino_t input_inode=0;
    ~rks_session(){rgb_rga.reset();signed_rga.reset();if(context){if(signed_input)rknn_destroy_mem(context,signed_input);rknn_destroy(context);}}
};
struct FloatOutputs {float* const* values;const uint32_t* counts;uint32_t count;};
extern "C" {
const char* rks_last_error(){return error.c_str();}
uint32_t rks_abi_version(){return 2;}
uint32_t rks_capabilities(){return 1;}
int rks_create(const char* path,rks_session** result){return guard([&]{
    require(result,"session output is null");*result=nullptr;require(path&&*path,"model path is empty");
    auto s=std::make_unique<rks_session>();
    rga(imcheckHeader(),"RGA header/runtime compatibility");
    check(rknn_init(&s->context,const_cast<char*>(path),0,0,nullptr),"rknn_init");
    rknn_input_output_num n{};check(rknn_query(s->context,RKNN_QUERY_IN_OUT_NUM,&n,sizeof(n)),"query tensor count");
    require(n.n_input==1&&n.n_output>0&&n.n_output<=64,"only single-input models with 1..64 outputs are supported");
    check(rknn_query(s->context,RKNN_QUERY_NATIVE_INPUT_ATTR,&s->input,sizeof(s->input)),"query native input");
    const auto& a=s->input;
    require(a.n_dims==4&&a.dims[0]==1&&a.fmt==RKNN_TENSOR_NHWC&&a.dims[3]==3,"only static batch1 NHWC RGB native input is supported");
    require(a.type==RKNN_TENSOR_INT8&&a.qnt_type==RKNN_TENSOR_QNT_AFFINE_ASYMMETRIC&&a.zp==-128&&a.scale==1.f/255.f,"only INT8 zp=-128 exact float scale=1/255 input is supported; hardware normalization profile required");
    s->w=a.dims[2];s->h=a.dims[1];s->stride=a.w_stride?a.w_stride:s->w;
    require(s->w>=2&&s->h>=2&&s->w<=4096&&s->h<=4096&&s->stride>=s->w&&s->stride<=8192,"unsupported input dimensions/stride");
    uint64_t bytes=uint64_t(s->stride)*s->h*3;
    require(bytes<=UINT32_MAX&&a.size_with_stride>=bytes,"native input allocation/stride mismatch");
    require(s->stride%16==0&&a.size_with_stride==bytes,"native RGB input must have 16-pixel aligned stride and no hidden padding");
    rks_image* owner=nullptr;rks_frame allocated{};
    auto allocate_dma=[&](const char* what){
        owner=nullptr;
        if(rks_allocate_image(s->stride,s->h,RKS_RGB888,&owner,&allocated)!=0){
            std::string cause=error;
            throw std::runtime_error(std::string(what)+": "+(cause.empty()?"unknown DMA failure":cause));
        }
    };
    allocate_dma("RGB DMA32 allocation failed");s->rgb_owner.reset(owner);
    s->rgb_rga=std::make_unique<Imported>(allocated.fd,allocated.size_bytes,s->w,s->h,s->stride,s->h,RK_FORMAT_RGB_888);
    allocate_dma("INT8 DMA32 allocation failed");s->signed_owner.reset(owner);
    // Runtime 1.5.2 requires a non-null virtual address at set_io_mem even in
    // pass-through mode. Establish VA only; never dereference image pixels.
    owner->length=allocated.size_bytes;owner->address=mmap(nullptr,owner->length,PROT_READ|PROT_WRITE,MAP_SHARED,owner->fd,0);
    require(owner->address!=MAP_FAILED,"RKNN required virtual address mapping failed");
    s->signed_input=rknn_create_mem_from_fd(s->context,allocated.fd,owner->address,allocated.size_bytes,0);
    require(s->signed_input,"RKNN external DMA32 import failed");
    require(s->signed_input->offset==0,"nonzero RKNN DMA offset unsupported");
    struct stat input_allocation{};
    require(fstat(s->signed_input->fd,&input_allocation)==0,"cannot identify owned RKNN input allocation");
    s->input_device=input_allocation.st_dev;s->input_inode=input_allocation.st_ino;
    s->signed_rga=std::make_unique<Imported>(s->signed_input->fd,s->signed_input->size,s->w,s->h,s->stride,s->h,RK_FORMAT_RGB_888);
    auto bind=s->input;bind.pass_through=1;
    check(rknn_set_io_mem(s->context,s->signed_input,&bind),"rknn_set_io_mem(native pass-through)");
    s->attrs.resize(n.n_output);s->values.resize(n.n_output);s->quantized_values.resize(n.n_output);s->supports_int8=true;
    for(unsigned i=0;i<n.n_output;i++){
        auto& o=s->attrs[i];o.index=i;
        check(rknn_query(s->context,RKNN_QUERY_OUTPUT_ATTR,&o,sizeof(o)),"query output");
        require(o.n_elems>0&&o.n_dims<=16,"invalid output tensor");s->values[i].resize(o.n_elems);
        bool supported=o.fmt==RKNN_TENSOR_NCHW&&o.type==RKNN_TENSOR_INT8&&o.qnt_type==RKNN_TENSOR_QNT_AFFINE_ASYMMETRIC&&o.size==o.n_elems&&o.scale>0&&std::isfinite(o.scale);
        if(supported)s->quantized_values[i].resize(o.n_elems);else s->supports_int8=false;
    }
    rknn_sdk_version v{};check(rknn_query(s->context,RKNN_QUERY_SDK_VERSION,&v,sizeof(v)),"query runtime version");
    s->versions=std::string("RKNN API=")+v.api_version+"; driver="+v.drv_version+"; "+RGA_API_FULL_VERSION;
    *result=s.release();
});}
void rks_destroy(rks_session* s){delete s;}
int rks_get_tensor_count(rks_session* s,uint32_t* i,uint32_t* o){return guard([&]{require(s&&i&&o,"null tensor count argument");std::lock_guard<std::mutex> lock(s->gate);*i=1;*o=s->attrs.size();});}
int rks_get_tensor(rks_session* s,uint32_t output,uint32_t index,rks_tensor* t){return guard([&]{require(s&&t,"null tensor argument");std::lock_guard<std::mutex> lock(s->gate);require(output<=1&&index<(output?s->attrs.size():1),"tensor index out of range");tensor(output?s->attrs[index]:s->input,*t);});}
int rks_get_versions(rks_session* s,char* p,uint32_t n){return guard([&]{require(s&&p,"null version argument");std::lock_guard<std::mutex> lock(s->gate);require(n>s->versions.size(),"version buffer is too small");std::memcpy(p,s->versions.c_str(),s->versions.size()+1);});}
int rks_set_core_mask(rks_session* s,uint32_t mask){return guard([&]{require(s,"null session");require(mask==0||mask==1||mask==2||mask==4||mask==3||mask==7,"unsupported core mask");std::lock_guard<std::mutex> lock(s->gate);check(rknn_set_core_mask(s->context,static_cast<rknn_core_mask>(mask)),"set core mask");});}
RKS_API int rks_set_float_output_mode(rks_session* s,uint32_t mode){return guard([&]{
    require(s,"null session");require(mode<=RKS_FLOAT_OUTPUT_PREFER_FAST_INT8,"unsupported float output mode");
    std::lock_guard<std::mutex> lock(s->gate);s->prefer_fast_float=mode==RKS_FLOAT_OUTPUT_PREFER_FAST_INT8;
});}
static int run_encoded(rks_session* s,const rks_frame* f,rks_run_info* info,bool quantized,const FloatOutputs* direct=nullptr){return guard([&]{
    require(s,"null session");std::lock_guard<std::mutex> lock(s->gate);s->has_output=false;s->output_encoding=0;s->generation++;require(f&&info,"null run argument");*info={};
    require(!quantized||s->supports_int8,"raw path requires contiguous standard NCHW affine INT8 outputs");
    if(direct){
        require(!quantized&&direct->values&&direct->counts&&direct->count==s->attrs.size(),"float output buffer count mismatch");
        for(unsigned i=0;i<direct->count;i++){
            require(direct->values[i]&&direct->counts[i]==s->attrs[i].n_elems,"float output buffer is null or has incorrect element count");
            uint64_t bytes=uint64_t(direct->counts[i])*sizeof(float);
            uintptr_t begin=reinterpret_cast<uintptr_t>(direct->values[i]);
            require(bytes<=UINT32_MAX&&begin<=UINTPTR_MAX-bytes,"float output buffer size/address overflows");
            for(unsigned j=0;j<i;j++){
                uintptr_t previous=reinterpret_cast<uintptr_t>(direct->values[j]);
                uint64_t previous_bytes=uint64_t(direct->counts[j])*sizeof(float);
                require(begin+bytes<=previous||previous+previous_bytes<=begin,"float output buffers overlap");
            }
        }
    }
    auto start=Clock::now();auto src=import(*f);auto rgb=s->rgb_rga->image;auto dst=s->signed_rga->image;
    if(f->format==RKS_NV12)src->image.color_space_mode=yuv_to_rgb(f->color_space);
    double scale=std::min(double(s->w)/f->width,double(s->h)/f->height);
    int rw=std::max(2,int(std::round(f->width*scale))),rh=std::max(2,int(std::round(f->height*scale)));
    require(rw<=int(s->w)&&rh<=int(s->h),"letterbox dimensions overflow");
    int left=(int(s->w)-rw)/2,top=(int(s->h)-rh)/2;
    im_rect sr{0,0,int(f->width),int(f->height)},dr{left,top,rw,rh};
    // Every visible destination pixel is overwritten when no letterbox border
    // remains. Keep the same validated RGA3 operation for every source format.
    if(left||top||rw!=int(s->w)||rh!=int(s->h))
        rga(imfill(rgb,{0,0,int(s->w),int(s->h)},0xff727272,1),"RGA RGB letterbox fill");
    rga(imcheck(src->image,rgb,sr,dr),"RGA resize/CSC validation");
    // RK3588 RGA2 and RGA3 produce different resize/CSC pixels on this BSP.
    // Keep the two verified-equivalent RGA3 engines eligible per operation;
    // do not mutate thread/global imconfig state or fall back to RGA2/CPU.
    im_opt_t preprocessing{};
    preprocessing.core=IM_SCHEDULER_RGA3_CORE0|IM_SCHEDULER_RGA3_CORE1;
    preprocessing.interp=IM_INTERP(IM_INTERP_LINEAR,IM_INTERP_LINEAR);
    bool prepared=false;
    // These two tasks have independent destinations. Keep padding before the
    // job and the dependent ROP after its synchronous completion. A caller may
    // reuse input_dma_fd (or dup it) as a source, requiring the old read/fill
    // order; source identity comes from the existing per-frame validation.
    bool source_aliases_input=src->device==s->input_device&&src->inode==s->input_inode;
    if(s->preprocessing_jobs&&!source_aliases_input){
        RgaJob job(false);
        if(job.handle){
            auto status=improcessTask(job.handle,src->image,rgb,{},sr,dr,{},&preprocessing,0);
            if(status==IM_STATUS_SUCCESS||status==IM_STATUS_NOERROR)
                status=imfillTask(job.handle,dst,{0,0,int(s->w),int(s->h)},0xff808080);
            if(status==IM_STATUS_SUCCESS||status==IM_STATUS_NOERROR){
                status=imendJob(job.handle,IM_SYNC);
                if(status==IM_STATUS_SUCCESS||status==IM_STATUS_NOERROR){job.handle=0;prepared=true;}
            }
        }
        // Optional batching of the same resize/CSC and INT8 fill. Any job
        // failure, including errors other than NOT_SUPPORTED, is cancelled by
        // the local guard and this frame uses the synchronous path below.
        // Cache only this session capability decision, never an external fd
        // or frame allocation.
        if(!prepared)s->preprocessing_jobs=false;
    }
    if(!prepared){
        rga(improcess(src->image,rgb,{},sr,dr,{},-1,nullptr,&preprocessing,IM_SYNC),"RGA3 linear resize/CSC");
        rga(imfill(dst,{0,0,int(s->w),int(s->h)},0xff808080,1),"RGA INT8 base fill");
    }
    // RGA2 ROP is a bitwise operation. Reinterpret packed RGB bytes as RGB565
    // words: no conversion is requested, and no byte occupies an alpha channel
    // (RGBA ROP forces alpha to 255). Width stride is required divisible by 16.
    auto xor_src=wrapbuffer_handle(s->rgb_rga->handle,int(s->stride*3/2),int(s->h),RK_FORMAT_RGB_565);
    auto xor_dst=wrapbuffer_handle(s->signed_rga->handle,int(s->stride*3/2),int(s->h),RK_FORMAT_RGB_565);
    rga(imrop(xor_src,xor_dst,IM_ROP_XOR,1),"RGA INT8 XOR128 (no CPU fallback)");
    auto pre=Clock::now();check(rknn_run(s->context,nullptr),"rknn_run");auto run=Clock::now();
    std::vector<rknn_output> outs(s->attrs.size());
    for(unsigned i=0;i<outs.size();i++){
        // Explicit per-session mode: standard OUTPUT_ATTR NCHW only, never
        // NATIVE_OUTPUT_ATTR NC1HWC2. SDK float remains the exact default.
        bool raw=quantized||(s->prefer_fast_float&&!s->quantized_values[i].empty()&&rks_detail::can_dequantize_int8(s->attrs[i]));
        outs[i].index=i;outs[i].want_float=raw?0:1;outs[i].is_prealloc=1;
        outs[i].buf=raw?static_cast<void*>(s->quantized_values[i].data()):direct?static_cast<void*>(direct->values[i]):static_cast<void*>(s->values[i].data());
        outs[i].size=raw?s->quantized_values[i].size():s->attrs[i].n_elems*sizeof(float);
    }
    int got=rknn_outputs_get(s->context,outs.size(),outs.data(),nullptr);
    // is_prealloc buffers stay owned here, but get can still hold runtime
    // conversion memory. Release even when get fails, then keep get's error.
    int released=rknn_outputs_release(s->context,outs.size(),outs.data());
    if(got<0&&released<0)throw std::runtime_error("rknn_outputs_get: "+std::to_string(got)+"; rknn_outputs_release: "+std::to_string(released));
    if(got<0)check(got,"rknn_outputs_get");
    check(released,"rknn_outputs_release");
    // All raw destinations were preallocated by this session: release does
    // not transfer/free ownership. Convert only after successful get/release;
    // failures keep output_encoding invalid and never publish partial results.
    if(!quantized&&s->prefer_fast_float)for(unsigned i=0;i<outs.size();i++)
        if(!s->quantized_values[i].empty()&&rks_detail::can_dequantize_int8(s->attrs[i])){
        float* destination=direct?direct->values[i]:s->values[i].data();
        rks_detail::dequantize_int8(s->quantized_values[i].data(),destination,s->attrs[i].n_elems,s->attrs[i].zp,s->attrs[i].scale);
    }
    auto end=Clock::now();s->has_output=!direct;s->output_encoding=direct?0:quantized?2:1;
    info->scale_x=float(rw)/f->width;info->scale_y=float(rh)/f->height;info->pad_left=left;info->pad_top=top;info->resize_width=rw;info->resize_height=rh;
    info->preprocess_ms=ms(start,pre);info->inference_ms=ms(pre,run);info->outputs_ms=ms(run,end);info->input_dma_fd=s->signed_input->fd;info->input_stride_bytes=s->stride*3;
});}
int rks_run(rks_session* s,const rks_frame* f,rks_run_info* info){return run_encoded(s,f,info,false);}
int rks_run_float_into(rks_session* s,const rks_frame* f,float* const* outputs,const uint32_t* counts,uint32_t count,rks_run_info* info){
    FloatOutputs direct{outputs,counts,count};return run_encoded(s,f,info,false,&direct);
}
int rks_run_int8(rks_session* s,const rks_frame* f,rks_run_info* info){return run_encoded(s,f,info,true);}
int rks_copy_output(rks_session* s,uint32_t index,float* p,uint32_t n){return guard([&]{require(s&&p,"null output argument");std::lock_guard<std::mutex> lock(s->gate);require(s->has_output&&s->output_encoding==1,"no valid float inference outputs");require(index<s->values.size(),"output index out of range");require(n==s->values[index].size(),"output element count must match tensor");std::copy(s->values[index].begin(),s->values[index].end(),p);});}
int rks_borrow_output_int8(rks_session* s,uint32_t index,const int8_t** p,uint32_t* count,uint64_t* generation){return guard([&]{
    require(p&&count&&generation,"null borrow output arguments");*p=nullptr;*count=0;*generation=0;
    require(s,"null session");std::lock_guard<std::mutex> lock(s->gate);
    require(s->has_output&&s->output_encoding==2,"no valid INT8 inference outputs");
    require(index<s->quantized_values.size(),"output index out of range");
    *p=s->quantized_values[index].data();*count=s->quantized_values[index].size();*generation=s->generation;
});}
int rks_draw_rectangles(const rks_frame* f,const rks_rect* rects,uint32_t n,uint32_t color,int32_t thickness){return guard([&]{
    require(f&&(rects||n==0)&&n<=4096&&thickness>0,"invalid rectangle arguments");auto dst=import(*f);
    require(n==0||(f->width>=2&&f->height>=2),"drawing requires a minimum 2x2 image");
    for(unsigned i=0;i<n;i++){
        const auto& r=rects[i];
        require(r.x>=0&&r.y>=0&&r.width>0&&r.height>0&&uint64_t(r.x)+r.width<=f->width&&uint64_t(r.y)+r.height<=f->height,"rectangle outside image");
        if(f->format==RKS_NV12){
            require(((r.x|r.y|r.width|r.height)%2)==0,"NV12 rectangle coordinates and extents must be even");
            if(uint64_t(thickness)*2<uint64_t(std::min(r.width,r.height)))
                require(thickness%2==0,"NV12 border thickness must be even");
        }
    }
    // Rectangle color is RGB. On NV12, librga converts it with the destination
    // color space; leaving the mode unset uses BT.601 limited for every frame.
    if(f->format==RKS_NV12)dst->image.color_space_mode=nv12_fill_color_space(f->color_space);
    // Same-color fills commute, including overlapping borders. Keep jobs bounded
    // to eight rectangles (at most 32 edge tasks); wait before reusing the DMA.
    for(unsigned first=0;first<n;first+=8){
        RgaJob job;
        for(unsigned i=first;i<std::min(first+8,n);i++){
            const auto& r=rects[i];
            if(uint64_t(thickness)*2>=uint64_t(std::min(r.width,r.height))){
                int w=std::max(2,r.width),h=std::max(2,r.height);
                int x=std::min(r.x,int(f->width)-w),y=std::min(r.y,int(f->height)-h);
                // librga 1.10.1 rejects a fill origin of exactly one on this
                // BSP. Extend left/up to zero, preserving the full marker.
                if(x==1){x=0;++w;}
                if(y==1){y=0;++h;}
                rga(imfillTask(job.handle,dst->image,{x,y,w,h},color),"RGA small rectangle fill task");
            }else{
                rga(imrectangleTask(job.handle,dst->image,{r.x,r.y,r.width,r.height},color,thickness),"RGA rectangle task");
            }
        }
        job.finish();
    }
});}
int rks_copy_frame(const rks_frame* a,const rks_frame* b){return guard([&]{
    require(a&&b,"null frame copy argument");
    require(a->width==b->width&&a->height==b->height,"copy requires equal dimensions");
    struct stat source{},destination{};
    require(fstat(a->fd,&source)==0&&fstat(b->fd,&destination)==0,"copy fd is invalid");
    require(source.st_ino!=destination.st_ino||source.st_dev!=destination.st_dev,"copy source and destination alias the same allocation");
    auto src=import(*a),dst=import(*b);
    if(a->format==RKS_NV12&&b->format!=RKS_NV12)
        src->image.color_space_mode=yuv_to_rgb(a->color_space);
    if(a->format!=RKS_NV12&&b->format==RKS_NV12)
        dst->image.color_space_mode=rgb_to_yuv(b->color_space);
    if(a->format==RKS_NV12&&b->format==RKS_NV12)
        require(a->color_space==b->color_space,"NV12 copy must preserve color metadata");
    im_rect full{0,0,int(a->width),int(a->height)};
    rga(imcheck(src->image,dst->image,full,full),"RGA copy validation");
    // RGA2 and RGA3 CSC pixels differ on this BSP. Pin the verified RGA3 cores
    // for format conversion only; a same-format copy has no matrix and may use
    // strides that RGA3 rejects, so it keeps the driver scheduler.
    im_opt_t copy{};
    const bool format_conversion=a->format!=b->format;
    if(format_conversion)copy.core=IM_SCHEDULER_RGA3_CORE0|IM_SCHEDULER_RGA3_CORE1;
    rga(improcess(src->image,dst->image,{},full,full,{},-1,nullptr,format_conversion?&copy:nullptr,IM_SYNC),format_conversion?"RGA3 copy/CSC":"RGA copy");
});}
int rks_allocate_image(uint32_t w,uint32_t h,uint32_t fmt,rks_image** result,rks_frame* frame){return guard([&]{
    require(result&&frame,"null image allocation output");*result=nullptr;*frame={};
    require(w>0&&h>0&&w<=4096&&h<=4096,"image allocation dimensions outside 1..4096");
    require(fmt==RKS_NV12||fmt==RKS_RGB888||fmt==RKS_RGBA8888,"unsupported allocation format");
    require(fmt!=RKS_NV12||((w|h)%2==0),"NV12 dimensions must be even");
    uint32_t stride=(w+15)&~15u,bpp=fmt==RKS_RGB888?3:fmt==RKS_RGBA8888?4:1;
    uint64_t bytes=uint64_t(stride)*h*bpp;if(fmt==RKS_NV12)bytes=bytes*3/2;
    require(bytes>0&&bytes<=INT_MAX,"image allocation size overflow");
    int heap=open("/dev/dma_heap/system-uncached-dma32",O_RDONLY|O_CLOEXEC);
    require(heap>=0,"cannot open system-uncached-dma32 DMA heap");
    dma_heap_allocation_data request{};request.len=bytes;request.fd_flags=O_RDWR|O_CLOEXEC;
    int status=ioctl(heap,DMA_HEAP_IOCTL_ALLOC,&request);int saved=errno;close(heap);
    if(status<0)throw std::runtime_error(std::string("DMA_HEAP_IOCTL_ALLOC: ")+std::strerror(saved));
    auto image=std::make_unique<rks_image>();image->fd=request.fd;
    *frame={image->fd,w,h,fmt,stride*bpp,h,bytes,0,fmt==RKS_NV12?uint32_t(RKS_BT601_LIMIT):0u,0};
    *result=image.release();
});}
void rks_release_image(rks_image* image){delete image;}
}
