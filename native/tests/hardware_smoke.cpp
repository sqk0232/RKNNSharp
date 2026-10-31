#include "rknnsharp.h"
#include <cstddef>
#include <rknn_api.h>
#include <im2d.h>
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <dirent.h>
#include <sys/stat.h>
#include <sys/mman.h>
#include <cstdlib>
#include <linux/dma-buf.h>
#include <sys/ioctl.h>
#include <vector>
#include <limits>

static_assert(sizeof(rks_frame) == 48, "rks_frame ABI size drifted from DmaFrame");
static_assert(sizeof(rks_tensor) == 364, "rks_tensor ABI size drifted from NativeTensor");
static_assert(sizeof(rks_run_info) == 56, "rks_run_info ABI size drifted from NativeRunInfo");
static_assert(sizeof(rks_rect) == 16, "rks_rect ABI size drifted from NativeRect");

static int fail(const char* op){std::fprintf(stderr,"%s: %s\n",op,rks_last_error());return 1;}
static int descriptors(){DIR* d=opendir("/proc/self/fd");if(!d)return -1;int n=0;while(readdir(d))n++;closedir(d);return n;}
int main(int argc,char** argv){
    if(argc!=2){std::fprintf(stderr,"usage: %s model.rknn\n",argv[0]);return 2;}
    std::printf("ABI sizeof frame=%zu tensor=%zu run_info=%zu rect=%zu\n",sizeof(rks_frame),sizeof(rks_tensor),sizeof(rks_run_info),sizeof(rks_rect));
    if(rks_abi_version()!=2)return 1;
    int initial_fds=descriptors();
    rks_session* s=nullptr;if(rks_create(argv[1],&s))return fail("create");
    if(rks_set_core_mask(s,42)==0||rks_set_core_mask(s,0))return fail("core mask validation");
    char v[1024];if(rks_get_versions(s,v,sizeof(v)))return fail("versions");std::printf("%s\n",v);
    uint32_t inputs=0,outputs=0;rks_get_tensor_count(s,&inputs,&outputs);
    rks_tensor in{};rks_get_tensor(s,0,0,&in);
    for(uint32_t i=0;i<outputs;i++){rks_tensor a{};rks_get_tensor(s,1,i,&a);std::printf("output%u %s dims=",i,a.name);for(unsigned j=0;j<a.n_dims;j++)std::printf("%u,",a.dims[j]);std::printf(" fmt=%d type=%d zp=%d scale=%.9g elems=%u\n",a.layout,a.data_type,a.zero_point,a.scale,a.element_count);}
    // Synthetic source pixels are generated exclusively by RGA hardware fill.
    rknn_context ctx=0;if(rknn_init(&ctx,argv[1],0,0,nullptr))return 1;
    unsigned w=in.dims[2],h=in.dims[1],stride=in.width_stride?in.width_stride:w;
    // RKNN allocations do not guarantee the DMA32 storage needed by RGA fill
    // on this BSP. Use the production allocator for both hardware fixtures.
    rks_image* source_owner=nullptr;rks_frame frame{};
    if(rks_allocate_image(w,h,RKS_RGB888,&source_owner,&frame))return fail("source allocation");
    if(frame.row_stride_bytes!=stride*3||frame.height_stride!=h)return 1;
    auto handle=importbuffer_fd(frame.fd,int(frame.size_bytes));if(!handle)return 1;
    auto image=wrapbuffer_handle(handle,w,h,RK_FORMAT_RGB_888,stride,h);
    if(imfill(image,{0,0,int(w),int(h)},0xff727272,1)!=IM_STATUS_SUCCESS)return 1;
    rks_run_info info{};
    for(int i=0;i<4;i++){if(rks_run(s,&frame,&info))return fail("run");std::printf("run%d src_fd=%d bound_input_fd=%d stride=%u pre=%.3f npu=%.3f outputs=%.3f ms\n",i,frame.fd,info.input_dma_fd,info.input_stride_bytes,info.preprocess_ms,info.inference_ms,info.outputs_ms);}
    std::vector<std::vector<float>> constant_float(outputs);
    for(unsigned i=0;i<outputs;i++){
        rks_tensor a{};rks_get_tensor(s,1,i,&a);std::vector<float> data(a.element_count);
        if(rks_copy_output(s,i,data.data(),data.size()))return fail("copy output");
        constant_float[i]=data;
        unsigned negative=0,nonfinite=0;for(float f:data){negative+=f<0;nonfinite+=!std::isfinite(f);}
        auto range=std::minmax_element(data.begin(),data.end());std::printf("output%u range=[%.7g,%.7g] negative=%u nonfinite=%u\n",i,*range.first,*range.second,negative,nonfinite);
        unsigned spatial=a.dims[2]*a.dims[3];for(unsigned c=0;c<6;c++){auto first=data.begin()+c*spatial;auto range2=std::minmax_element(first,first+spatial);std::printf(" channel%u [%.7g,%.7g]\n",c,*range2.first,*range2.second);}
        if(nonfinite)return 1;
    }
    const int8_t* borrowed=nullptr;uint32_t borrowed_count=0;uint64_t generation=0,previous_generation=0;
    if(rks_borrow_output_int8(s,0,&borrowed,&borrowed_count,&generation)==0)return 1;
    for(unsigned run=0;run<2;run++){
        if(rks_run_int8(s,&frame,&info))return fail("raw INT8 run");
        std::printf("raw run%u preprocess=%.4f inference=%.4f runtimeOutputs=%.4f ms\n",run,info.preprocess_ms,info.inference_ms,info.outputs_ms);
        for(unsigned i=0;i<outputs;i++){
            rks_tensor attr{};if(rks_get_tensor(s,1,i,&attr)||rks_borrow_output_int8(s,i,&borrowed,&borrowed_count,&generation))return fail("raw output borrow");
            if(borrowed_count!=attr.element_count||!borrowed||!generation)return 1;
            if(i==0){if(generation<=previous_generation)return 1;previous_generation=generation;}else if(generation!=previous_generation)return 1;
            float maxdiff=0;unsigned unequal=0,split_unequal=0,fused_unequal=0,inverse_errors=0,maxulp=0;
            for(unsigned j=0;j<borrowed_count;j++){
                float value=(int(borrowed[j])-attr.zero_point)*attr.scale;
                float expected=constant_float[i][j];
                maxdiff=std::max(maxdiff,std::abs(value-expected));unequal+=value!=expected;
                volatile float product=float(borrowed[j])*attr.scale,bias=float(attr.zero_point)*attr.scale;
                split_unequal+=(product-bias)!=expected;
                fused_unequal+=std::fma(float(borrowed[j]),attr.scale,-bias)!=expected;
                inverse_errors+=std::lrint(double(expected)/attr.scale+attr.zero_point)!=borrowed[j];
                if(value!=expected){uint32_t lhs=0,rhs=0;std::memcpy(&lhs,&value,4);std::memcpy(&rhs,&expected,4);lhs=(lhs&0x80000000u)?~lhs:lhs|0x80000000u;rhs=(rhs&0x80000000u)?~rhs:rhs|0x80000000u;maxulp=std::max(maxulp,lhs>rhs?lhs-rhs:rhs-lhs);}
                if(std::abs(value-expected)>4*std::numeric_limits<float>::epsilon()*std::max(1.f,std::abs(expected)))return 1;
            }
            std::printf("raw output%u standardNCHW count=%u generation=%llu vsFloat maxabs=%g unequal=%u maxULP=%u inverseQuantErrors=%u splitMultiplyUnequal=%u fusedMultiplyUnequal=%u\n",i,borrowed_count,static_cast<unsigned long long>(generation),maxdiff,unequal,maxulp,inverse_errors,split_unequal,fused_unequal);
            if(inverse_errors)return 1;
        }
        if(rks_copy_output(s,0,constant_float[0].data(),constant_float[0].size())==0)return 1;
    }
    if(rks_run(s,&frame,&info))return fail("switch back to float");
    if(rks_borrow_output_int8(s,0,&borrowed,&borrowed_count,&generation)==0||borrowed||borrowed_count||generation)return 1;
    std::puts("raw/float encoding state, pointer reset and generation checks passed");
    // Independent reference: RGA directly fills the expected signed RGB bytes,
    // avoiding both the XOR code and CPU pixel accesses. Mixed colors ensure
    // every RGB channel also occupies every RGBA word byte (including alpha).
    rks_image* reference_owner=nullptr;rks_frame reference_frame{};
    if(rks_allocate_image(w,h,RKS_RGB888,&reference_owner,&reference_frame))return fail("reference allocation");
    if(reference_frame.row_stride_bytes!=stride*3||reference_frame.height_stride!=h)return 1;
    // The VA satisfies the external-memory runtime contract. Only the optional
    // diagnostic below reads these pixels; RGA generates the reference input.
    void* reference_address=mmap(nullptr,reference_frame.size_bytes,PROT_READ|PROT_WRITE,MAP_SHARED,reference_frame.fd,0);
    if(reference_address==MAP_FAILED)return 1;
    auto* reference=rknn_create_mem_from_fd(ctx,reference_frame.fd,reference_address,reference_frame.size_bytes,0);if(!reference)return 1;
    auto reference_handle=importbuffer_fd(reference->fd,int(reference->size));if(!reference_handle)return 1;
    auto reference_image=wrapbuffer_handle(reference_handle,w,h,RK_FORMAT_RGB_888,stride,h);
    uint32_t colors[]={0xff000000,0xff010101,0xff7f7f7f,0xff808080,0xffffffff,0xff7f0180,0xff80ff7f,0xffff8000};
    for(unsigned i=0;i<8;i++){
        im_rect stripe{int(i*w/8),0,int(w/8),int(h)};
        if(imfill(image,stripe,colors[i],1)!=IM_STATUS_SUCCESS)return 1;
        if(imfill(reference_image,stripe,colors[i]^0x00808080,1)!=IM_STATUS_SUCCESS)return 1;
    }
    if(rks_run(s,&frame,&info))return fail("boundary stripe run");
    if(std::getenv("RKS_DIAGNOSTIC_READBACK")){
        // Diagnostic-only 16-byte read samples; never used to transform input.
        auto* mapped=static_cast<unsigned char*>(mmap(nullptr,stride*h*3,PROT_READ,MAP_SHARED,info.input_dma_fd,0));
        if(mapped==MAP_FAILED)return 1;
        dma_buf_sync sync{DMA_BUF_SYNC_START|DMA_BUF_SYNC_READ};
        if(ioctl(info.input_dma_fd,DMA_BUF_IOCTL_SYNC,&sync)||ioctl(reference->fd,DMA_BUF_IOCTL_SYNC,&sync))return 1;
        for(unsigned row:{0u,h/2,h-1})for(unsigned stripe=0;stripe<8;stripe++){unsigned offset=row*stride*3+stripe*(w/8)*3;for(unsigned j=0;j<16;j++)if(mapped[offset+j]!=static_cast<unsigned char*>(reference->virt_addr)[offset+j])return 1;}
        std::puts("diagnostic boundary input sample equality: 384/384 bytes across first/middle/last rows");
        sync.flags=DMA_BUF_SYNC_END|DMA_BUF_SYNC_READ;
        if(ioctl(info.input_dma_fd,DMA_BUF_IOCTL_SYNC,&sync)||ioctl(reference->fd,DMA_BUF_IOCTL_SYNC,&sync))return 1;
        munmap(mapped,stride*h*3);
    }
    rknn_tensor_attr native{};rknn_query(ctx,RKNN_QUERY_NATIVE_INPUT_ATTR,&native,sizeof(native));native.pass_through=1;
    if(rknn_set_io_mem(ctx,reference,&native)||rknn_run(ctx,nullptr))return 1;
    std::vector<rknn_output> ref_out(outputs);std::vector<std::vector<float>> expected(outputs);
    for(unsigned i=0;i<outputs;i++){rks_tensor a{};rks_get_tensor(s,1,i,&a);expected[i].resize(a.element_count);ref_out[i].index=i;ref_out[i].want_float=1;ref_out[i].is_prealloc=1;ref_out[i].buf=expected[i].data();ref_out[i].size=expected[i].size()*4;}
    if(rknn_outputs_get(ctx,outputs,ref_out.data(),nullptr))return 1;
    for(unsigned i=0;i<outputs;i++){std::vector<float> actual(expected[i].size());if(rks_copy_output(s,i,actual.data(),actual.size()))return fail("boundary output copy");float maxdiff=0;unsigned unequal=0;for(unsigned j=0;j<actual.size();j++){maxdiff=std::max(maxdiff,std::abs(actual[j]-expected[i][j]));unequal+=actual[j]!=expected[i][j];}std::printf("hardware signed-stripe reference output%u maxdiff=%g unequal=%u\n",i,maxdiff,unequal);if(unequal)return 1;}
    rks_image* padded_owner=nullptr;rks_frame padded{};
    if(rks_allocate_image(w+16,h,RKS_RGB888,&padded_owner,&padded))return fail("padded source allocation");padded.width=w;
    if(rks_copy_frame(&frame,&padded)||rks_run(s,&padded,&info))return fail("padded source hardware copy/run");
    for(unsigned i=0;i<outputs;i++){std::vector<float> actual(expected[i].size());if(rks_copy_output(s,i,actual.data(),actual.size()))return fail("padded output");if(actual!=expected[i])return 1;}
    std::printf("padded RGB source row stride=%u vs visible=%u: all outputs identical\n",padded.row_stride_bytes,w*3);rks_release_image(padded_owner);
    auto odd=frame;odd.width=w-1;if(rks_run(s,&odd,&info))return fail("valid odd RGB width");
    std::puts("valid odd RGB width accepted with aligned row stride");
    rknn_outputs_release(ctx,outputs,ref_out.data());releasebuffer_handle(reference_handle);rknn_destroy_mem(ctx,reference);
    munmap(reference_address,reference_frame.size_bytes);rks_release_image(reference_owner);
    struct stat input_stat{};if(fstat(info.input_dma_fd,&input_stat))return 1;
    std::printf("bound input DMA fd=%d inode=%llu; same imported fd used by RGA dst and RKNN set_io_mem pass_through=1\n",info.input_dma_fd,static_cast<unsigned long long>(input_stat.st_ino));
    rks_image* draw_owner=nullptr;rks_frame draw{};
    if(rks_allocate_image(w,h,RKS_NV12,&draw_owner,&draw))return fail("allocate NV12 draw");
    if(rks_copy_frame(&frame,&draw))return fail("copy to owned NV12");
    rks_rect owned_box{32,32,160,100};if(rks_draw_rectangles(&draw,&owned_box,1,0xff00ff00,2))return fail("owned NV12 draw");
    rks_rect nv12_edges[]={{0,0,4,4},{2,2,56,20},{int(w)-56,2,56,20},{2,int(h)-20,56,20}};
    for(int thickness:{2,4})if(rks_draw_rectangles(&draw,nv12_edges,4,0xff00ff00,thickness))return fail("NV12 aligned edge borders");
    rks_release_image(draw_owner);std::puts("owned DMA32 NV12 copy and hardware draw passed");
    rks_rect box{32,32,160,100};if(rks_draw_rectangles(&frame,&box,1,0xff00ff00,2))return fail("draw");
    rks_rect small_boxes[]={{0,0,1,1},{int(w)-1,int(h)-3,1,3},{int(w)-3,int(h)-1,3,1},{10,10,3,3},{1,1,1,3},{1,2,3,1},{2,1,1,3}};
    if(rks_draw_rectangles(&frame,small_boxes,7,0xff00ff00,2))return fail("small hardware markers");
    std::puts("small 1x1/1x3/3x1/3x3 edge and origin-one boxes use in-bounds hardware fill");
    rks_rect edge_boxes[]={{1,1,55,20},{2,2,55,20},{0,0,int(w),int(h)},{int(w)-55,1,55,20},{1,int(h)-20,55,20}};
    for(int thickness:{1,2,4})if(rks_draw_rectangles(&frame,edge_boxes,5,0xff00ff00,thickness))return fail("origin-one/centered-edge border regression");
    std::puts("normal hollow border origin-one and all frame edges PASS (thickness 1/2/4)");
    rks_image* exact_owner=nullptr;rks_frame exact{};
    if(rks_allocate_image(1920,1080,RKS_RGB888,&exact_owner,&exact))return fail("video-size draw allocation");
    rks_rect exact_boxes[]={{981,1,55,2},{981,1,55,101},{982,2,53,99},{1,1,55,101},{1919,1079,1,1}};
    for(int thickness:{1,2,4})if(rks_draw_rectangles(&exact,exact_boxes,5,0xff00ff00,thickness))return fail("exact video rectangle regression");
    rks_release_image(exact_owner);
    std::puts("1920x1080 exact rect[981,1,55,2], neighboring hollow borders, NV12 even edge borders PASS");
    auto bad=frame;bad.offset_bytes=1;if(rks_run(s,&bad,&info)==0)return 1;
    if(rks_run_int8(s,&bad,&info)==0||rks_borrow_output_int8(s,0,&borrowed,&borrowed_count,&generation)==0||borrowed||borrowed_count||generation)return 1;
    float f=0;if(rks_copy_output(s,0,&f,1)==0)return 1;std::printf("invalid-offset and invalidated-output checks passed\n");
    bad=frame;bad.row_stride_bytes++;if(rks_run(s,&bad,&info)==0)return 1;
    bad=frame;bad.fd=-1;if(rks_run(s,&bad,&info)==0)return 1;
    bad=frame;bad.size_bytes=UINT64_MAX;if(rks_run(s,&bad,&info)==0)return 1;
    bad=frame;bad.size_bytes=frame.size_bytes+4096;if(rks_run(s,&bad,&info)==0)return 1;
    bad=frame;bad.height+=16;bad.height_stride+=16;bad.size_bytes=uint64_t(bad.row_stride_bytes)*bad.height_stride;if(rks_run(s,&bad,&info)==0)return 1;
    bad=frame;bad.format=RKS_NV12;bad.width--;bad.color_space=RKS_BT601_LIMIT;if(rks_run(s,&bad,&info)==0)return 1;
    bad=frame;bad.format=99;if(rks_run(s,&bad,&info)==0)return 1;
    if(rks_copy_frame(&frame,&frame)==0)return 1;
    std::puts("bad stride/fd/size/odd NV12/format/aliased-copy rejection passed");
    releasebuffer_handle(handle);rks_release_image(source_owner);rknn_destroy(ctx);rks_destroy(s);
    int warm_fds=descriptors();
    for(int i=0;i<5;i++){s=nullptr;if(rks_create(argv[1],&s))return fail("recreate");rks_destroy(s);if(descriptors()!=warm_fds)return 1;}
    std::printf("fd cold=%d warm_baseline=%d final=%d (five recreate/destroy cycles stable)\n",initial_fds,warm_fds,descriptors());
    std::puts("hardware smoke PASS");return 0;
}
