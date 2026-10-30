// Isolated RGA job A/B. CPU reads below are diagnostics only, never drawing.
#include "../src/rknnsharp.cpp"
#include <linux/dma-buf.h>
#include <cstdio>

struct Image {
    rks_image* owner=nullptr;
    rks_frame frame{};
    Image(unsigned w,unsigned h,unsigned fmt){require(rks_allocate_image(w,h,fmt,&owner,&frame)==0,rks_last_error());}
    ~Image(){rks_release_image(owner);}
};
static void legacy(const rks_frame& f,const std::vector<rks_rect>& rects,int thickness){
    auto dst=import(f);
    for(auto r:rects){
        if(uint64_t(thickness)*2>=uint64_t(std::min(r.width,r.height))){
            int w=std::max(2,r.width),h=std::max(2,r.height);
            rga(imfill(dst->image,{std::min(r.x,int(f.width)-w),std::min(r.y,int(f.height)-h),w,h},0xff00ff00,1),"legacy fill");
        }else rga(imrectangle(dst->image,{r.x,r.y,r.width,r.height},0xff00ff00,thickness,1),"legacy rectangle");
    }
}
static void batched(const rks_frame& f,const std::vector<rks_rect>& rects,int thickness){require(rks_draw_rectangles(&f,rects.data(),rects.size(),0xff00ff00,thickness)==0,rks_last_error());}
static std::vector<uint8_t> readback(const rks_frame& f){
    auto* p=static_cast<uint8_t*>(mmap(nullptr,f.size_bytes,PROT_READ,MAP_SHARED,f.fd,0));
    require(p!=MAP_FAILED,"diagnostic map failed");
    dma_buf_sync sync{DMA_BUF_SYNC_START|DMA_BUF_SYNC_READ};require(ioctl(f.fd,DMA_BUF_IOCTL_SYNC,&sync)==0,"diagnostic sync failed");
    std::vector<uint8_t> result(p,p+f.size_bytes);
    sync.flags=DMA_BUF_SYNC_END|DMA_BUF_SYNC_READ;require(ioctl(f.fd,DMA_BUF_IOCTL_SYNC,&sync)==0,"diagnostic sync end failed");
    munmap(p,f.size_bytes);return result;
}
static void fill(const rks_frame& f){auto dst=import(f);rga(imfill(dst->image,{0,0,int(f.width),int(f.height)},0xff58442a,1),"initial hardware fill");}
static std::vector<rks_rect> rectangles(unsigned count,unsigned w,unsigned h){
    std::vector<rks_rect> result;
    for(unsigned i=0;i<count;i++)result.push_back({int((i*46)%(w-160)),int((i*34)%(h-120)),160,120});
    return result;
}
static void correctness(unsigned fmt,unsigned w,unsigned h){
    Image legacy(w,h,fmt),batch(w,h,fmt);fill(legacy.frame);fill(batch.frame);
    auto before_a=readback(legacy.frame),before_b=readback(batch.frame);
    auto rects=rectangles(32,w,h);
    rects.insert(rects.end(),{{0,0,2,2},{int(w)-2,int(h)-2,2,2},{20,20,4,100},{40,40,8,8},{100,60,160,120}});
    if(fmt!=RKS_NV12)rects.push_back({int(w)-1,int(h)-1,1,1});
    ::legacy(legacy.frame,rects,2);batched(batch.frame,rects,2);
    auto a=readback(legacy.frame),b=readback(batch.frame);size_t unequal=0,padding_changed=0;
    unsigned rows=fmt==RKS_NV12?h*3/2:h,bpp=fmt==RKS_RGB888?3:fmt==RKS_RGBA8888?4:1;
    for(unsigned y=0;y<rows;y++)for(unsigned x=0;x<legacy.frame.row_stride_bytes;x++){
        size_t offset=size_t(y)*legacy.frame.row_stride_bytes+x;
        if(x<w*bpp)unequal+=a[offset]!=b[offset];
        else padding_changed+=(a[offset]!=before_a[offset])+(b[offset]!=before_b[offset]);
    }
    std::printf("correctness format=%u size=%ux%u stride=%u rectangles=%zu unequal=%zu padding_changed=%zu\n",fmt,w,h,legacy.frame.row_stride_bytes,rects.size(),unequal,padding_changed);
    require(unequal==0&&padding_changed==0,"batch pixels/padding differ");
}
static void origin_one_correctness(unsigned fmt){
    Image actual(640,480,fmt),expected(640,480,fmt);
    struct Case {rks_rect request;im_rect marker;};
    const Case cases[]={{{1,1,1,3},{0,0,3,4}},{{1,2,3,1},{0,2,4,2}},{{2,1,1,3},{2,0,2,4}},
        {{1,1,3,3},{0,0,4,4}},{{1,1,1,1},{0,0,3,3}},{{639,479,1,1},{638,478,2,2}}};
    for(auto c:cases){
        fill(actual.frame);fill(expected.frame);
        require(rks_draw_rectangles(&actual.frame,&c.request,1,0xff00ff00,2)==0,rks_last_error());
        auto dst=import(expected.frame);rga(imfill(dst->image,c.marker,0xff00ff00,1),"reference marker fill");
        require(readback(actual.frame)==readback(expected.frame),"origin-one marker pixels differ");
    }
    std::printf("origin_one format=%u cases=%zu exact_equal=1\n",fmt,sizeof(cases)/sizeof(cases[0]));
}
int main(int argc,char**){try{
    if(argc>1){
        for(auto fmt:{RKS_RGB888,RKS_RGBA8888}){
            Image image(640,480,fmt);auto dst=import(image.frame);
            for(int x:{0,1,2,3})for(int y:{0,1,2,3})for(int width:{2,3,4}){
                auto status=imfill(dst->image,{x,y,width,3},0xff00ff00,1);
                std::printf("tiny format=%u rect=%d,%d,%d,3 status=%d\n",fmt,x,y,width,int(status));
            }
        }
        return 0;
    }
    for(auto fmt:{RKS_RGB888,RKS_RGBA8888,RKS_NV12}){correctness(fmt,640,480);correctness(fmt,642,482);}
    origin_one_correctness(RKS_RGB888);origin_one_correctness(RKS_RGBA8888);
    Image image(2560,1440,RKS_RGB888);fill(image.frame);
    for(unsigned count:{1u,3u,8u,32u}){
        auto rects=rectangles(count,2560,1440);
        for(unsigned mode=0;mode<2;mode++){
            std::vector<double> times;
            for(unsigned i=0;i<110;i++){
                auto start=Clock::now();
                if(mode)batched(image.frame,rects,2);else legacy(image.frame,rects,2);
                auto elapsed=ms(start,Clock::now());if(i>=10)times.push_back(elapsed);
            }
            double total=0;for(double t:times)total+=t;std::sort(times.begin(),times.end());
            std::printf("benchmark rectangles=%u mode=%s frames=%zu average_ms=%.6f p50_ms=%.6f p95_ms=%.6f\n",count,mode?"job8":"legacy",times.size(),total/times.size(),times[50],times[95]);
        }
    }
    std::puts("PASS draw batch A/B");return 0;
}catch(const std::exception& e){std::fprintf(stderr,"FAIL %s\n",e.what());return 1;}}
