#include "rknnsharp.h"
#include "draw_rects.h"
#include <im2d.h>
#include <cstdio>
#include <stdexcept>
#include <sys/mman.h>
#include <sys/ioctl.h>
#include <linux/dma-buf.h>

static void check(int code,const char* what){if(code)throw std::runtime_error(what);}
static void rga_check(IM_STATUS code){if(code!=IM_STATUS_SUCCESS&&code!=IM_STATUS_NOERROR)throw std::runtime_error(imStrError(code));}
struct Image{
    rks_image* owner=nullptr;rks_frame frame{};rga_buffer_handle_t handle=0;rga_buffer_t rga{};
    unsigned char* pixels=nullptr;
    Image(){
        check(rks_allocate_image(1920,1080,RKS_RGB888,&owner,&frame),"allocate RGB");
        handle=importbuffer_fd(frame.fd,frame.size_bytes);if(!handle)throw std::runtime_error("RGA import");
        rga=wrapbuffer_handle(handle,frame.width,frame.height,RK_FORMAT_RGB_888,frame.row_stride_bytes/3,frame.height_stride);
        pixels=static_cast<unsigned char*>(mmap(nullptr,frame.size_bytes,PROT_READ,MAP_SHARED,frame.fd,0));
        if(pixels==MAP_FAILED)throw std::runtime_error("diagnostic mmap");
    }
    ~Image(){if(pixels&&pixels!=MAP_FAILED)munmap(pixels,frame.size_bytes);if(handle)releasebuffer_handle(handle);rks_release_image(owner);}
    void clear(){rga_check(imfill(rga,{0,0,1920,1080},0xff000000,1));}
    void sync(bool start){dma_buf_sync sync{};sync.flags=DMA_BUF_SYNC_READ|(start?DMA_BUF_SYNC_START:DMA_BUF_SYNC_END);check(ioctl(frame.fd,DMA_BUF_IOCTL_SYNC,&sync),"diagnostic sync");}
    bool green(int x,int y){const auto* p=pixels+y*frame.row_stride_bytes+x*3;return p[0]==0&&p[1]==255&&p[2]==0;}
};
int main(){try{
    Image actual,reference;int cases=0;size_t checked=0,ordinary_difference=0;
    const rks_rect boxes[]={{0,0,55,31},{1,1,55,31},{2,2,55,31},{3,3,55,31},{4,4,55,31},{64,64,55,31},
        {981,1,55,2},{981,1,55,101},{1865,1049,55,31},{1919,1079,1,1}};
    for(const auto& box:boxes)for(int thickness:{1,2,3,4,5}){
        actual.clear();if(rks_draw_rectangles(&actual.frame,&box,1,0xff00ff00,thickness))throw std::runtime_error(rks_last_error());
        rks_letterbox_rect edges[4];int count=0;
        check(!rks_draw_border_rects(1920,1080,box.x,box.y,box.width,box.height,thickness,edges,&count),"border geometry");
        actual.sync(true);
        // Read only the small diagnostic ROI surrounding each rectangle.
        // No readback or pixel processing is used to generate the input image.
        for(int y=std::max(0,box.y-1);y<std::min(1080,box.y+box.height+1);y++)
        for(int x=std::max(0,box.x-1);x<std::min(1920,box.x+box.width+1);x++){
            bool expected=false;
            for(int i=0;i<count;i++){const auto& e=edges[i];expected|=x>=e.x&&x<e.x+e.width&&y>=e.y&&y<e.y+e.height;}
            // Safe ordinary boxes use the vendor implementation; compare its
            // raster directly below instead of assuming its private geometry.
            bool fallback=thickness==1||box.width<=2*thickness||box.height<=2*thickness
                ||box.x<=thickness/2+1||box.y<=thickness/2+1
                ||box.x+box.width+thickness/2>=1919||box.y+box.height+thickness/2>=1079;
            if(fallback&&actual.green(x,y)!=expected){std::fprintf(stderr,"pixel mismatch rect=%d,%d,%d,%d t=%d pixel=%d,%d\n",box.x,box.y,box.width,box.height,thickness,x,y);return 1;}
            if(!fallback&&actual.green(x,y)!=expected)ordinary_difference++;
            checked++;
        }
        actual.sync(false);
        if(box.x==64&&box.y==64&&thickness>=2){
            reference.clear();rga_check(imrectangle(reference.rga,{box.x,box.y,box.width,box.height},0xff00ff00,thickness,1));
            actual.sync(true);reference.sync(true);
            for(int y=55;y<105;y++)for(int x=55;x<130;x++)if(actual.green(x,y)!=reference.green(x,y))return 1;
            reference.sync(false);actual.sync(false);
        }
        cases++;
    }
    std::printf("draw hardware pixel semantics PASS: %d cases, %zu ROI samples; hollow/solid/edge/origin1/odd-thickness, ordinary vendor raster equality; vendor-vs-inward differences=%zu\n",cases,checked,ordinary_difference);
    return 0;
}catch(const std::exception& e){std::fprintf(stderr,"draw hardware regression: %s\n",e.what());return 1;}}
