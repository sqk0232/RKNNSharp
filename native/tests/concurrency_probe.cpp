// Isolated diagnosis only: includes internals to separate RGA and NPU stages.
// No test hooks or new exports are added to the production library.
#include "../src/rknnsharp.cpp"
#include <rknnsharp_gst.h>
#include <linux/dma-buf.h>
#include <atomic>
#include <fstream>
#include <thread>

struct Capture {
    rkgst_capture* value=nullptr;
    std::vector<rkgst_frame> samples;
    ~Capture(){for(auto& s:samples)rkgst_release(&s);if(value)rkgst_close(value);}
};
uint64_t fingerprint(const uint8_t* bytes,size_t size){uint64_t h=1469598103934665603ull;for(size_t i=0;i<size;i++){h^=bytes[i];h*=1099511628211ull;}return h;}
std::vector<uint8_t> diagnostic_read(int fd,size_t bytes){
    auto* p=static_cast<uint8_t*>(mmap(nullptr,bytes,PROT_READ,MAP_SHARED,fd,0));
    require(p!=MAP_FAILED,"diagnostic mmap failed");
    dma_buf_sync sync{DMA_BUF_SYNC_START|DMA_BUF_SYNC_READ};
    require(ioctl(fd,DMA_BUF_IOCTL_SYNC,&sync)==0,"diagnostic read sync start failed");
    std::vector<uint8_t> result(p,p+bytes);
    sync.flags=DMA_BUF_SYNC_END|DMA_BUF_SYNC_READ;
    require(ioctl(fd,DMA_BUF_IOCTL_SYNC,&sync)==0,"diagnostic read sync end failed");
    munmap(p,bytes);return result;
}
struct Snapshot {std::vector<uint8_t> input;std::vector<std::vector<int8_t>> output;};
Snapshot snapshot(rks_session* s){return {diagnostic_read(s->signed_input->fd,s->signed_input->size),s->quantized_values};}
bool compare(const char* phase,unsigned source,unsigned slot,const Snapshot& a,const Snapshot& b){
    size_t differences=0;int largest=0;
    for(size_t i=0;i<a.input.size();i++){differences+=a.input[i]!=b.input[i];largest=std::max(largest,std::abs(int(int8_t(a.input[i]))-int(int8_t(b.input[i]))));}
    std::printf("phase=%s source=%u slot=%u signed_hash=%016llx baseline=%016llx input_unequal=%zu max_abs_signed=%d",
        phase,source,slot,(unsigned long long)fingerprint(b.input.data(),b.input.size()),(unsigned long long)fingerprint(a.input.data(),a.input.size()),differences,largest);
    bool equal=differences==0;
    for(unsigned h=0;h<a.output.size();h++){
        size_t unequal=0;int maxdiff=0;
        for(size_t i=0;i<a.output[h].size();i++){unequal+=a.output[h][i]!=b.output[h][i];maxdiff=std::max(maxdiff,std::abs(int(a.output[h][i])-int(b.output[h][i])));}
        std::printf(" head%u_unequal=%zu head%u_maxq=%d",h,unequal,h,maxdiff);
        equal=equal&&unequal==0;
    }
    std::puts("");
    return equal;
}
void infer_fixed(rks_session* s){
    check(rknn_run(s->context,nullptr),"fixed input inference");
    std::vector<rknn_output> outputs(s->attrs.size());
    for(unsigned i=0;i<outputs.size();i++){outputs[i].index=i;outputs[i].is_prealloc=1;outputs[i].want_float=0;outputs[i].buf=s->quantized_values[i].data();outputs[i].size=s->quantized_values[i].size();}
    check(rknn_outputs_get(s->context,outputs.size(),outputs.data(),nullptr),"fixed outputs get");
    check(rknn_outputs_release(s->context,outputs.size(),outputs.data()),"fixed outputs release");
}
void preprocess_selected(rks_session* s,const rks_frame& f,int core,int interpolation){
    auto src=import(f);auto rgb=s->rgb_rga->image;auto dst=s->signed_rga->image;
    src->image.color_space_mode=yuv_to_rgb(f.color_space);
    double scale=std::min(double(s->w)/f.width,double(s->h)/f.height);
    int rw=int(std::round(f.width*scale)),rh=int(std::round(f.height*scale));
    im_rect sr{0,0,int(f.width),int(f.height)},dr{(int(s->w)-rw)/2,(int(s->h)-rh)/2,rw,rh};
    rga(imfill(rgb,{0,0,int(s->w),int(s->h)},0xff727272,1),"selected RGB fill");
    im_opt_t options{};options.core=core;options.interp=interpolation;
    rga(improcess(src->image,rgb,{},sr,dr,{},-1,nullptr,&options,IM_SYNC),"selected resize/CSC");
    rga(imfill(dst,{0,0,int(s->w),int(s->h)},0xff808080,1),"selected signed fill");
    auto xor_src=wrapbuffer_handle(s->rgb_rga->handle,int(s->stride*3/2),int(s->h),RK_FORMAT_RGB_565);
    auto xor_dst=wrapbuffer_handle(s->signed_rga->handle,int(s->stride*3/2),int(s->h),RK_FORMAT_RGB_565);
    rga(imrop(xor_src,xor_dst,IM_ROP_XOR,1),"selected XOR");
}
static unsigned worker_count=3;
template<class F>void parallel(F action){
    std::atomic<unsigned> ready{0};std::atomic<bool> start{false};
    std::vector<std::string> errors(worker_count);std::vector<std::thread> threads;
    for(unsigned i=0;i<worker_count;i++)threads.emplace_back([&,i]{ready++;while(!start.load())std::this_thread::yield();try{action(i);}catch(const std::exception& e){errors[i]=e.what();}});
    while(ready.load()!=worker_count)std::this_thread::yield();start=true;
    for(auto& t:threads)t.join();for(const auto& e:errors)require(e.empty(),e.c_str());
}
int main(int argc,char** argv){try{
    bool six=argc==4&&std::string(argv[3])=="fixed6";
    bool fixed=six||(argc==4&&std::string(argv[3])=="fixed");
    worker_count=six?6:3;
    require(argc==3||fixed,"usage: concurrency_probe model.rknn private_rtsp_url_file [fixed|fixed6]");
    std::printf("independent_sessions=%u masks_repeat=1,2,4\n",worker_count);
    std::ifstream private_file(argv[2]);std::string url;std::getline(private_file,url);
    while(!url.empty()&&(url.back()=='\r'||url.back()=='\n'))url.pop_back();require(!url.empty(),"missing RTSP input file");
    Capture capture;char message[1024]{};
    require(rkgst_open_rtsp(url.c_str(),"h264",100,&capture.value,message,sizeof(message))==0,"RTSP adapter open failed");
    url.clear();
    for(unsigned i=0;i<6;i++){rkgst_frame sample{};int status=rkgst_pull(capture.value,10000,&sample,message,sizeof(message));require(status==1,"could not retain six decoded samples");capture.samples.push_back(sample);}
    require(rkgst_pause(capture.value,message,sizeof(message))==0,"capture PAUSED transition failed");
    std::vector<rks_frame> frames;std::vector<uint64_t> source_hashes;
    for(unsigned i=0;i<capture.samples.size();i++){
        const auto& x=capture.samples[i];require(x.fd[0]==x.fd[1]&&x.offset[0]==0&&x.stride[0]==x.stride[1]&&x.offset[1]%x.stride[0]==0,"unsupported retained source layout");
        rks_frame f{x.fd[0],x.width,x.height,RKS_NV12,uint32_t(x.stride[0]),uint32_t(x.offset[1]/x.stride[0]),x.allocation_size[0],0,RKS_BT601_FULL,0};
        frames.push_back(f);auto bytes=diagnostic_read(f.fd,f.size_bytes);source_hashes.push_back(fingerprint(bytes.data(),bytes.size()));
        std::printf("source=%u fd=%d width=%u height=%u stride=%u hstride=%u caps_color=%u selected_color=%u hash=%016llx\n",i,f.fd,f.width,f.height,f.row_stride_bytes,f.height_stride,x.color_space,f.color_space,(unsigned long long)source_hashes.back());
    }
    std::unique_ptr<rks_session> sessions[6];
    for(unsigned slot=0;slot<worker_count;slot++){rks_session* raw=nullptr;require(rks_create(argv[1],&raw)==0,rks_last_error());sessions[slot].reset(raw);require(rks_set_core_mask(raw,1u<<(slot%3))==0,rks_last_error());}
    std::vector<Snapshot> baseline;
    for(unsigned source=0;source<6;source++){
        rks_run_info info{};
        if(fixed){preprocess_selected(sessions[0].get(),frames[source],IM_SCHEDULER_RGA3_CORE0,0);infer_fixed(sessions[0].get());}
        else require(rks_run_int8(sessions[0].get(),&frames[source],&info)==0,rks_last_error());
        baseline.push_back(snapshot(sessions[0].get()));
        if(!fixed)for(unsigned slot=0;slot<3;slot++){require(rks_run_int8(sessions[slot].get(),&frames[source],&info)==0,rks_last_error());compare("sequential",source,slot,baseline.back(),snapshot(sessions[slot].get()));}
    }
    bool fixed_equal=true;
    if(fixed){
        for(unsigned source=0;source<6;source++)for(unsigned round=0;round<5;round++){
            parallel([&](unsigned slot){rks_run_info info{};require(rks_run_int8(sessions[slot].get(),&frames[source],&info)==0,rks_last_error());});
            for(unsigned slot=0;slot<worker_count;slot++)fixed_equal=compare("fixed-same-source-parallel",source,slot,baseline[source],snapshot(sessions[slot].get()))&&fixed_equal;
        }
        for(unsigned round=0;round<60;round++){
            parallel([&](unsigned slot){unsigned source=(round*(six?1:3)+slot)%6;rks_run_info info{};require(rks_run_int8(sessions[slot].get(),&frames[source],&info)==0,rks_last_error());});
            for(unsigned slot=0;slot<worker_count;slot++){unsigned source=(round*(six?1:3)+slot)%6;fixed_equal=compare("fixed-mixed-source-parallel",source,slot,baseline[source],snapshot(sessions[slot].get()))&&fixed_equal;}
        }
        // Full-resolution CSC without scaling is examined separately: this
        // determines whether owned CopyFrom needs the same core restriction.
        bool unsupported_rga2_csc=false;
        for(unsigned source=0;source<6;source++){
            rks_image* raw_owner=nullptr;rks_frame destination{};
            require(rks_allocate_image(frames[source].width,frames[source].height,RKS_RGB888,&raw_owner,&destination)==0,rks_last_error());
            std::unique_ptr<rks_image> owner(raw_owner);auto src=import(frames[source]),dst=import(destination);src->image.color_space_mode=yuv_to_rgb(frames[source].color_space);
            im_rect full{0,0,int(destination.width),int(destination.height)};std::vector<uint8_t> reference;
            for(int core:{IM_SCHEDULER_RGA3_CORE0,IM_SCHEDULER_RGA3_CORE1,IM_SCHEDULER_RGA2_CORE0}){
                if(core==IM_SCHEDULER_RGA2_CORE0&&unsupported_rga2_csc)continue;
                im_opt_t options{};options.core=core;
                auto status=improcess(src->image,dst->image,{},full,full,{},-1,nullptr,&options,IM_SYNC);
                if(core==IM_SCHEDULER_RGA2_CORE0&&status!=IM_STATUS_SUCCESS&&status!=IM_STATUS_NOERROR){unsupported_rga2_csc=true;std::printf("CSC-only source=%u core=%d explicitly_unsupported_status=%d (not a production fallback)\n",source,core,status);continue;}
                rga(status,"full-size CSC only");
                auto bytes=diagnostic_read(destination.fd,destination.size_bytes);if(reference.empty())reference=bytes;
                size_t unequal=0;int largest=0;for(size_t i=0;i<bytes.size();i++){unequal+=bytes[i]!=reference[i];largest=std::max(largest,std::abs(int(bytes[i])-int(reference[i])));}
                std::printf("CSC-only source=%u core=%d unequal=%zu maxabs=%d hash=%016llx\n",source,core,unequal,largest,(unsigned long long)fingerprint(bytes.data(),bytes.size()));
            }
        }
    }
    // Same source is processed by all three contexts concurrently, then rotated.
    if(!fixed)for(unsigned source=0;source<6;source++)for(unsigned round=0;round<3;round++){
        parallel([&](unsigned slot){rks_run_info info{};require(rks_run_int8(sessions[slot].get(),&frames[source],&info)==0,rks_last_error());});
        for(unsigned slot=0;slot<3;slot++)compare("full-parallel",source,slot,baseline[source],snapshot(sessions[slot].get()));
    }
    // Finish each hardware preprocessing stage serially, then run ONLY NPU
    // concurrently over the already-bound, unchanged signed DMA inputs.
    if(!fixed)for(unsigned source=0;source<6;source++){
        for(unsigned slot=0;slot<3;slot++){rks_run_info info{};require(rks_run_int8(sessions[slot].get(),&frames[source],&info)==0,rks_last_error());}
        parallel([&](unsigned slot){for(unsigned repeat=0;repeat<5;repeat++)infer_fixed(sessions[slot].get());});
        for(unsigned slot=0;slot<3;slot++)compare("NPU-only-parallel",source,slot,baseline[source],snapshot(sessions[slot].get()));
    }
    if(!fixed)for(unsigned source=0;source<6;source++){
        for(int interpolation:{0,IM_INTERP(IM_INTERP_LINEAR,IM_INTERP_LINEAR)}){
            Snapshot pinned_reference;
            for(int core:{IM_SCHEDULER_RGA3_CORE0,IM_SCHEDULER_RGA3_CORE1,IM_SCHEDULER_RGA2_CORE0}){
                preprocess_selected(sessions[0].get(),frames[source],core,interpolation);infer_fixed(sessions[0].get());
                auto current=snapshot(sessions[0].get());if(core==IM_SCHEDULER_RGA3_CORE0)pinned_reference=current;
                std::string phase=std::string(interpolation?"LINEAR-core":"DEFAULT-core")+std::to_string(core);
                compare(phase.c_str(),source,0,pinned_reference,current);
                if(core==IM_SCHEDULER_RGA3_CORE0&&interpolation)compare("LINEAR-vs-production-default",source,0,baseline[source],current);
            }
        }
        Snapshot linear_reference;
        for(unsigned round=0;round<3;round++){
            parallel([&](unsigned slot){preprocess_selected(sessions[slot].get(),frames[source],0,IM_INTERP(IM_INTERP_LINEAR,IM_INTERP_LINEAR));infer_fixed(sessions[slot].get());});
            for(unsigned slot=0;slot<3;slot++){auto current=snapshot(sessions[slot].get());if(round==0&&slot==0)linear_reference=current;compare("LINEAR-auto-parallel",source,slot,linear_reference,current);}
        }
    }
    for(unsigned i=0;i<frames.size();i++){auto bytes=diagnostic_read(frames[i].fd,frames[i].size_bytes);auto hash=fingerprint(bytes.data(),bytes.size());std::printf("source=%u immutable=%d final_hash=%016llx\n",i,int(hash==source_hashes[i]),(unsigned long long)hash);require(hash==source_hashes[i],"retained source allocation changed");}
    std::printf("probe completed mode=%s exact_equal=%d\n",fixed?"fixed":"diagnostic",int(fixed_equal));return fixed&&!fixed_equal?1:0;
}catch(const std::exception& e){std::fprintf(stderr,"concurrency probe: %s\n",e.what());return 1;}}
