#include <cstddef>
#include "rknnsharp.h"
#include <rknn_api.h>
#include <im2d.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <fstream>
#include <memory>
#include <numeric>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>
#include <sys/mman.h>

using Clock=std::chrono::steady_clock;
static double elapsed(Clock::time_point start){return std::chrono::duration<double,std::milli>(Clock::now()-start).count();}
static void check(int status,const char* op){if(status<0)throw std::runtime_error(std::string(op)+": "+std::to_string(status));}
static void load(){std::ifstream input("/sys/kernel/debug/rknpu/load");std::string line;std::getline(input,line);std::puts(line.c_str());}
static void report(const char* name,int mask,const std::vector<double>& values){
    auto sorted=values;std::sort(sorted.begin(),sorted.end());
    std::printf("%s mask=%d count=%zu latency_ms mean=%.4f median=%.4f p95=%.4f min=%.4f max=%.4f\n",
        name,mask,sorted.size(),std::accumulate(sorted.begin(),sorted.end(),0.0)/sorted.size(),
        sorted[sorted.size()/2],sorted[size_t(std::ceil(sorted.size()*.95))-1],sorted.front(),sorted.back());
}
struct Context {
    rknn_context context=0;
    rknn_tensor_mem* input=nullptr;
    rga_buffer_handle_t handle=0;
    rks_image* input_owner=nullptr;
    rks_frame input_frame{};
    void* input_address=MAP_FAILED;
    int mask=0;
    std::vector<uint32_t> output_sizes;
    ~Context(){if(handle)releasebuffer_handle(handle);if(input)rknn_destroy_mem(context,input);if(input_address!=MAP_FAILED)munmap(input_address,input_frame.size_bytes);rks_release_image(input_owner);if(context)rknn_destroy(context);}
    void initialize(const char* model,int selected){
        mask=selected;check(rknn_init(&context,const_cast<char*>(model),0,0,nullptr),"init");
        check(rknn_set_core_mask(context,static_cast<rknn_core_mask>(mask)),"set core mask");
        rknn_tensor_attr attr{};check(rknn_query(context,RKNN_QUERY_NATIVE_INPUT_ATTR,&attr,sizeof(attr)),"query input");
        if(attr.n_dims!=4||attr.fmt!=RKNN_TENSOR_NHWC||attr.type!=RKNN_TENSOR_INT8||attr.dims[0]!=1||attr.dims[3]!=3||attr.zp!=-128||attr.scale!=1.f/255.f)
            throw std::runtime_error("benchmark requires the validated RGB signed-byte input profile");
        // Color fill is RGA2-only on RK3588 and requires DMA32 storage. The
        // SDK default allocator does not promise that address range.
        check(rks_allocate_image(attr.dims[2],attr.dims[1],RKS_RGB888,&input_owner,&input_frame),"allocate DMA32 input");
        if(input_frame.row_stride_bytes!=(attr.w_stride?attr.w_stride:attr.dims[2])*3||input_frame.size_bytes<attr.size_with_stride)
            throw std::runtime_error("DMA32 input stride/size mismatch");
        input_address=mmap(nullptr,input_frame.size_bytes,PROT_READ|PROT_WRITE,MAP_SHARED,input_frame.fd,0);
        if(input_address==MAP_FAILED)throw std::runtime_error("map external input");
        input=rknn_create_mem_from_fd(context,input_frame.fd,input_address,input_frame.size_bytes,0);
        if(!input)throw std::runtime_error("import DMA32 input");
        handle=importbuffer_fd(input->fd,int(input->size));if(!handle)throw std::runtime_error("import RGA input");
        auto image=wrapbuffer_handle(handle,attr.dims[2],attr.dims[1],RK_FORMAT_RGB_888,
                                     attr.w_stride?attr.w_stride:attr.dims[2],attr.dims[1]);
        std::printf("signed RGB input DMA32 fd=%d bytes=%u width=%u height=%u stride=%u zp=%d scale=%.9g\n",input->fd,input->size,attr.dims[2],attr.dims[1],attr.w_stride,attr.zp,attr.scale);
        // Pixel generation is entirely hardware. 114 -> signed byte -14 = 0xf2.
        if(imfill(image,{0,0,int(attr.dims[2]),int(attr.dims[1])},0xfff2f2f2,1)!=IM_STATUS_SUCCESS)
            throw std::runtime_error("RGA signed input fill");
        attr.pass_through=1;check(rknn_set_io_mem(context,input,&attr),"bind input");
        rknn_input_output_num count{};check(rknn_query(context,RKNN_QUERY_IN_OUT_NUM,&count,sizeof(count)),"query count");
        for(unsigned i=0;i<count.n_output;i++){rknn_tensor_attr out{};out.index=i;check(rknn_query(context,RKNN_QUERY_OUTPUT_ATTR,&out,sizeof(out)),"query output");output_sizes.push_back(out.n_elems);}
    }
    std::vector<double> run(unsigned warmup,unsigned count){
        for(unsigned i=0;i<warmup;i++)check(rknn_run(context,nullptr),"warmup");
        std::vector<double> times;times.reserve(count);
        for(unsigned i=0;i<count;i++){auto start=Clock::now();check(rknn_run(context,nullptr),"run");times.push_back(elapsed(start));}
        return times;
    }
    std::vector<std::vector<float>> outputs(){
        std::vector<std::vector<float>> values(output_sizes.size());std::vector<rknn_output> descriptors(output_sizes.size());
        for(unsigned i=0;i<output_sizes.size();i++){values[i].resize(output_sizes[i]);auto& d=descriptors[i];d.index=i;d.want_float=1;d.is_prealloc=1;d.buf=values[i].data();d.size=output_sizes[i]*4;}
        check(rknn_outputs_get(context,descriptors.size(),descriptors.data(),nullptr),"get output");
        check(rknn_outputs_release(context,descriptors.size(),descriptors.data()),"release output");return values;
    }
};
int main(int argc,char** argv){try{
    if(argc!=2){std::fprintf(stderr,"usage: %s model.rknn\n",argv[0]);return 2;}
    std::vector<std::vector<float>> reference;
    for(int mask:{0,1,3,7,0}){
        Context ctx;ctx.initialize(argv[1],mask);auto times=ctx.run(5,40);report("single-context",mask,times);
        auto values=ctx.outputs();if(reference.empty())reference=values;
        std::printf("single-context mask=%d output_exact=%d\n",mask,int(values==reference));
        if(values!=reference)return 1;load();
    }
    std::vector<std::unique_ptr<Context>> contexts;
    for(int mask:{1,2,4}){auto ctx=std::make_unique<Context>();ctx->initialize(argv[1],mask);report("pinned-solo",mask,ctx->run(5,60));if(ctx->outputs()!=reference)return 1;contexts.push_back(std::move(ctx));}
    std::atomic<unsigned> ready{0};std::atomic<bool> go{false};
    std::vector<std::thread> threads;std::vector<std::vector<double>> times(3);std::vector<std::string> failures(3);
    for(unsigned i=0;i<3;i++)threads.emplace_back([&,i]{
        ready.fetch_add(1);while(!go.load())std::this_thread::yield();
        try{times[i]=contexts[i]->run(0,100);}catch(const std::exception& e){failures[i]=e.what();}
    });
    while(ready.load()!=3)std::this_thread::yield();
    auto started=Clock::now();go.store(true);
    std::this_thread::sleep_for(std::chrono::milliseconds(1000));load();
    for(auto& thread:threads)thread.join();double wall=elapsed(started);
    for(unsigned i=0;i<3;i++){if(!failures[i].empty())throw std::runtime_error(failures[i]);report("pinned-parallel",contexts[i]->mask,times[i]);bool equal=contexts[i]->outputs()==reference;std::printf("parallel mask=%d output_exact=%d\n",contexts[i]->mask,int(equal));if(!equal)return 1;}
    std::printf("parallel completed_requests=300 wall_ms=%.4f aggregate_requests_per_second=%.4f (throughput, not per-request latency)\n",wall,300000.0/wall);
    return 0;
}catch(const std::exception& error){std::fprintf(stderr,"%s\n",error.what());return 1;}}
