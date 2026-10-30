#include "rknnsharp.h"
#include <cstddef>
#include <im2d.h>
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <dlfcn.h>
#include <unistd.h>
#include <fstream>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

static void require(bool ok,const char* message){if(!ok)throw std::runtime_error(message);}
static void check(int code,const char* message){if(code)throw std::runtime_error(std::string(message)+": "+rks_last_error());}
struct Image {
    rks_image* owner=nullptr;rks_frame frame{};
    Image(unsigned w,unsigned h,unsigned format){check(rks_allocate_image(w,h,format,&owner,&frame),"allocate fixture");}
    ~Image(){rks_release_image(owner);}
    Image(const Image&)=delete;
    void stripes(){
        int bpp=frame.format==RKS_RGB888?3:4;
        auto handle=importbuffer_fd(frame.fd,int(frame.size_bytes));require(handle!=0,"fixture RGA import");
        auto image=wrapbuffer_handle(handle,frame.width,frame.height,frame.format==RKS_RGB888?RK_FORMAT_RGB_888:RK_FORMAT_RGBA_8888,frame.row_stride_bytes/bpp,frame.height_stride);
        uint32_t colors[]={0xff000000,0xff010101,0xff7f7f7f,0xff808080,0xffffffff,0xff7f0180,0xff80ff7f,0xffff8000};
        for(unsigned i=0;i<8;i++){
            im_rect area{int(i*frame.width/8),0,int(frame.width/8),int(frame.height)};
            auto status=imfill(image,area,colors[i],1);
            if(status!=IM_STATUS_SUCCESS&&status!=IM_STATUS_NOERROR){releasebuffer_handle(handle);throw std::runtime_error("fixture hardware fill failed");}
        }
        releasebuffer_handle(handle);
    }
};
using Into=int(*)(rks_session*,const rks_frame*,float* const*,const uint32_t*,uint32_t,rks_run_info*);
using Capabilities=uint32_t(*)();
int main(int argc,char** argv){try{
    require(argc==3,"usage: run_output_regression model.rknn output-directory");
    rks_session* raw=nullptr;check(rks_create(argv[1],&raw),"create");
    std::unique_ptr<rks_session,decltype(&rks_destroy)> session(raw,rks_destroy);
    check(rks_set_core_mask(raw,1),"core0");
    char versions[1024];check(rks_get_versions(raw,versions,sizeof(versions)),"versions");std::puts(versions);
    uint32_t inputs=0,count=0;check(rks_get_tensor_count(raw,&inputs,&count),"count");
    std::vector<uint32_t> sizes(count);std::vector<std::vector<float>> cached(count),direct(count);
    std::vector<float*> pointers(count);
    for(unsigned i=0;i<count;i++){
        rks_tensor attr{};check(rks_get_tensor(raw,1,i,&attr),"attribute");sizes[i]=attr.element_count;
        cached[i].resize(sizes[i]);direct[i].resize(sizes[i]);pointers[i]=direct[i].data();
    }
    auto into=reinterpret_cast<Into>(dlsym(RTLD_DEFAULT,"rks_run_float_into"));
    auto capabilities=reinterpret_cast<Capabilities>(dlsym(RTLD_DEFAULT,"rks_capabilities"));
    require((into!=nullptr)==(capabilities!=nullptr),"capability/export disagreement");
    require(!capabilities||(capabilities()&1),"missing float-into capability");
    std::printf("direct_float_capability=%d\n",int(into!=nullptr));
    std::vector<std::vector<float>> retained;
    auto run_case=[&](const char* name,const rks_frame& frame,bool test_direct=true){
        rks_run_info info{};check(rks_run(raw,&frame,&info),"cached float run");
        for(unsigned i=0;i<count;i++){
            check(rks_copy_output(raw,i,cached[i].data(),sizes[i]),"cached float copy");
            std::ofstream file(std::string(argv[2])+"/"+name+"-head"+std::to_string(i)+".f32",std::ios::binary);
            file.write(reinterpret_cast<const char*>(cached[i].data()),sizes[i]*sizeof(float));require(bool(file),"write fixture output");
        }
        if(into&&test_direct){
            check(into(raw,&frame,pointers.data(),sizes.data(),count,&info),"direct float run");
            require(cached==direct,"direct float output differs from cached API");
            require(rks_copy_output(raw,0,cached[0].data(),sizes[0])!=0,"direct run exposed stale internal float cache");
            const int8_t* borrowed=nullptr;uint32_t n=0;uint64_t generation=0;
            require(rks_borrow_output_int8(raw,0,&borrowed,&n,&generation)!=0&&!borrowed&&!n&&!generation,"direct run exposed stale borrowed INT8");
            if(retained.empty()){
                retained=std::move(direct);direct.resize(count);
                for(unsigned i=0;i<count;i++){direct[i].resize(sizes[i]);pointers[i]=direct[i].data();}
            }
        }
        std::printf("case=%s direct_exact=%d preprocess_ms=%.6f run_ms=%.6f output_ms=%.6f\n",name,int(into&&test_direct),info.preprocess_ms,info.inference_ms,info.outputs_ms);
    };
    Image square(640,640,RKS_RGB888);square.stripes();run_case("rgb-full",square.frame);
    Image padded(656,640,RKS_RGB888);padded.frame.width=640;padded.stripes();run_case("rgb-padded-stride",padded.frame);
    Image letterbox(640,480,RKS_RGB888);letterbox.stripes();run_case("rgb-letterbox",letterbox.frame);
    Image wide(1280,720,RKS_RGB888);wide.stripes();run_case("rgb-resize-letterbox",wide.frame);
    Image rgba(640,640,RKS_RGBA8888);rgba.stripes();run_case("rgba-full",rgba.frame);
    Image nv12(640,480,RKS_NV12);nv12.frame.color_space=RKS_BT709_LIMIT;
    check(rks_copy_frame(&letterbox.frame,&nv12.frame),"generate NV12 fixture with hardware CSC");run_case("nv12-letterbox",nv12.frame);
    Image nv12square(640,640,RKS_NV12);nv12square.frame.color_space=RKS_BT709_LIMIT;
    check(rks_copy_frame(&square.frame,&nv12square.frame),"generate square NV12 fixture");run_case("nv12-full",nv12square.frame);
    // input_dma_fd is public and may be supplied back as a source, including
    // through a different descriptor referring to the same DMA allocation.
    rks_run_info alias_info{};check(rks_run(raw,&square.frame,&alias_info),"reset alias source");
    rks_tensor input_attr{};check(rks_get_tensor(raw,0,0,&input_attr),"input attribute");
    auto alias=square.frame;alias.fd=alias_info.input_dma_fd;alias.row_stride_bytes=alias_info.input_stride_bytes;alias.size_bytes=input_attr.size_with_stride;
    run_case("rgb-owned-input-alias",alias,false);
    if(into){check(rks_run(raw,&square.frame,&alias_info),"reset alias direct source");check(into(raw,&alias,pointers.data(),sizes.data(),count,&alias_info),"direct aliased source");require(cached==direct,"direct alias output differs");}
    check(rks_run(raw,&square.frame,&alias_info),"reset duplicated alias source");
    int duplicate=dup(alias_info.input_dma_fd);require(duplicate>=0,"duplicate owned input fd");alias.fd=duplicate;
    run_case("rgb-owned-input-dup-alias",alias,false);
    if(into){check(rks_run(raw,&square.frame,&alias_info),"reset duplicate alias direct source");check(into(raw,&alias,pointers.data(),sizes.data(),count,&alias_info),"direct duplicated alias source");require(cached==direct,"direct duplicate alias output differs");}
    close(duplicate);std::puts("owned/dup source aliases preserve old read-before-fill order");
    rks_run_info info{};
    auto expect_invalid=[&](rks_frame invalid,const char* reason){
        require(rks_run(raw,&invalid,&info)!=0,reason);
        require(rks_copy_output(raw,0,cached[0].data(),sizes[0])!=0,"invalid frame retained stale output");
    };
    auto bad=square.frame;bad.row_stride_bytes--;expect_invalid(bad,"invalid RGB stride accepted");
    bad=square.frame;bad.offset_bytes=1;expect_invalid(bad,"nonzero offset accepted");
    bad=square.frame;bad.format=999;expect_invalid(bad,"unknown format accepted");
    bad=nv12.frame;bad.width--;expect_invalid(bad,"odd NV12 width accepted");
    bad=nv12.frame;bad.color_space=0;expect_invalid(bad,"implicit NV12 color metadata accepted");
    if(into){
        check(rks_run_int8(raw,&square.frame,&info),"INT8 before direct argument failures");
        require(into(raw,&square.frame,pointers.data(),sizes.data(),count-1,&info)!=0,"wrong output count accepted");
        require(into(raw,&square.frame,nullptr,sizes.data(),count,&info)!=0,"null outputs accepted");
        auto wrong=sizes;wrong[0]--;require(into(raw,&square.frame,pointers.data(),wrong.data(),count,&info)!=0,"short output buffer accepted");
        auto aliases=pointers;aliases[1]=aliases[0];require(into(raw,&square.frame,aliases.data(),sizes.data(),count,&info)!=0,"overlapping output buffers accepted");
        auto missing=pointers;missing[1]=nullptr;require(into(raw,&square.frame,missing.data(),sizes.data(),count,&info)!=0,"null output head accepted");
        const int8_t* borrowed=nullptr;uint32_t n=0;uint64_t generation=0;
        require(rks_borrow_output_int8(raw,0,&borrowed,&n,&generation)!=0&&!borrowed&&!n&&!generation,"failed direct run retained borrowed output");
        check(rks_run(raw,&square.frame,&info),"old cached API after direct/failure");
        for(unsigned i=0;i<count;i++)check(rks_copy_output(raw,i,cached[i].data(),sizes[i]),"old cached copy after direct/failure");
        require(cached==retained,"old cached API changed after direct/failure");
        auto saved=retained;session.reset();require(saved==retained,"snapshot changed on destroy");
        std::puts("direct argument/state/old API/destroy checks passed");
    }
    std::puts("frame validation regression passed");return 0;
}catch(const std::exception& e){std::fprintf(stderr,"%s\n",e.what());return 1;}}
