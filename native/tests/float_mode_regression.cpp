#include "rknnsharp.h"
#include "rknn_float_output.h"
#include <im2d.h>
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <dlfcn.h>
#include <memory>
#include <stdexcept>
#include <vector>
static void require(bool ok,const char* what){if(!ok)throw std::runtime_error(what);}
static void check(int code,const char* what){if(code)throw std::runtime_error(std::string(what)+": "+rks_last_error());}
int main(int argc,char**argv){try{
    require(argc==2,"usage: float_mode_native_regression model");
    using Setter=int(*)(rks_session*,uint32_t);auto setter=reinterpret_cast<Setter>(dlsym(RTLD_DEFAULT,"rks_set_float_output_mode"));
    require(setter,"mode setter missing");require(setter(nullptr,0)!=0,"null mode setter accepted");
    rks_session* handle=nullptr;check(rks_create(argv[1],&handle),"create");
    std::unique_ptr<rks_session,decltype(&rks_destroy)> session(handle,rks_destroy);check(rks_set_core_mask(handle,1),"core0");
    rks_image* owner=nullptr;rks_frame image{};check(rks_allocate_image(640,640,RKS_RGB888,&owner,&image),"image");
    std::unique_ptr<rks_image,decltype(&rks_release_image)> release(owner,rks_release_image);
    auto rga_handle=importbuffer_fd(image.fd,int(image.size_bytes));require(rga_handle,"RGA fixture import");
    auto buffer=wrapbuffer_handle(rga_handle,640,640,RK_FORMAT_RGB_888,image.row_stride_bytes/3,image.height_stride);
    auto status=imfill(buffer,{0,0,640,640},0xff7f0180,1);releasebuffer_handle(rga_handle);
    require(status==IM_STATUS_SUCCESS||status==IM_STATUS_NOERROR,"hardware fixture fill");
    uint32_t inputs=0,outputs=0;check(rks_get_tensor_count(handle,&inputs,&outputs),"outputs");
    std::vector<uint32_t> counts(outputs);std::vector<float*> pointers(outputs);
    std::vector<std::vector<float>> sdk(outputs),actual(outputs),expected(outputs);std::vector<std::vector<int8_t>> raw(outputs);
    std::vector<rks_tensor> attrs(outputs);
    for(unsigned i=0;i<outputs;i++){
        check(rks_get_tensor(handle,1,i,&attrs[i]),"attr");counts[i]=attrs[i].element_count;
        sdk[i].resize(counts[i]);actual[i].resize(counts[i]);expected[i].resize(counts[i]);raw[i].resize(counts[i]);pointers[i]=actual[i].data();
    }
    rks_run_info info{};check(rks_run_float_into(handle,&image,pointers.data(),counts.data(),outputs,&info),"default direct SDK");sdk=actual;
    check(setter(handle,0),"explicit exact");check(rks_run_float_into(handle,&image,pointers.data(),counts.data(),outputs,&info),"explicit SDK");require(sdk==actual,"default differs from explicit SDK mode");
    check(rks_run_int8(handle,&image,&info),"raw reference");
    for(unsigned i=0;i<outputs;i++){
        const int8_t* data=nullptr;uint32_t n=0;uint64_t generation=0;check(rks_borrow_output_int8(handle,i,&data,&n,&generation),"borrow raw");require(n==counts[i],"raw reference count");
        std::copy(data,data+n,raw[i].begin());
        for(unsigned j=0;j<n;j++)expected[i][j]=float(int(raw[i][j])-attrs[i].zero_point)*attrs[i].scale;
    }
    check(setter(handle,1),"prefer fast");check(rks_run_float_into(handle,&image,pointers.data(),counts.data(),outputs,&info),"fast direct");require(expected==actual,"fast differs from scalar affine reference");
    require(setter(handle,2)!=0,"invalid mode accepted");
    check(rks_run_float_into(handle,&image,pointers.data(),counts.data(),outputs,&info),"fast after invalid mode");require(expected==actual,"invalid setter changed mode");
    check(rks_run(handle,&image,&info),"fast cached API");
    for(unsigned i=0;i<outputs;i++)check(rks_copy_output(handle,i,actual[i].data(),counts[i]),"fast cached copy");
    require(expected==actual,"fast cached output differs from direct");
    check(rks_run_int8(handle,&image,&info),"Detect raw unaffected");
    for(unsigned i=0;i<outputs;i++){
        const int8_t* data=nullptr;uint32_t n=0;uint64_t generation=0;check(rks_borrow_output_int8(handle,i,&data,&n,&generation),"raw after mode");require(n==raw[i].size()&&std::equal(data,data+n,raw[i].begin()),"fast mode changed INT8 output");
    }
    check(setter(handle,0),"restore exact");check(rks_run_float_into(handle,&image,pointers.data(),counts.data(),outputs,&info),"SDK after fast");require(sdk==actual,"SDK exact changed after mode round trip");
    check(setter(handle,1),"fast invalid-buffer test");auto wrong=counts;wrong[0]--;
    require(rks_run_float_into(handle,&image,pointers.data(),wrong.data(),outputs,&info)!=0,"invalid buffer accepted in fast mode");
    require(rks_copy_output(handle,0,actual[0].data(),counts[0])!=0,"failed fast run published cached output");
    std::puts("float mode native regression passed: default==explicitSDK exact, fast==scalar affine, invalid mode unchanged, cached/direct match, Detect INT8 unchanged, SDK round-trip exact, invalid outputs unpublished");
    return 0;
}catch(const std::exception&e){std::fprintf(stderr,"%s\n",e.what());return 1;}}
