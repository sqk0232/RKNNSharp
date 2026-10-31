#include "draw_rects.h"
#include <cstdio>
#include <climits>

int main(){
    int cases=0;
    for(int x:{0,1,2,981,1919})for(int y:{0,1,2,1079})
    for(int w:{1,2,3,55})for(int h:{1,2,3,101})for(int t:{1,2,4,INT_MAX}){
        if(w>1920-x||h>1080-y)continue;
        rks_letterbox_rect edges[4];int count=0;
        if(!rks_draw_border_rects(1920,1080,x,y,w,h,t,edges,&count)||count<1||count>4)return 1;
        for(int i=0;i<count;i++){
            const auto& e=edges[i];
            if(e.x<0||e.y<0||e.x==1||e.y==1||e.width<2||e.height<2||e.x+e.width>1920||e.y+e.height>1080)return 1;
        }
        // The complete requested inward border must remain covered.
        for(int yy=y;yy<y+h;yy++)for(int xx=x;xx<x+w;xx++){
            if(xx-x>=t&&x+w-1-xx>=t&&yy-y>=t&&y+h-1-yy>=t)continue;
            bool covered=false;
            for(int i=0;i<count;i++){const auto& e=edges[i];covered|=xx>=e.x&&xx<e.x+e.width&&yy>=e.y&&yy<e.y+e.height;}
            if(!covered)return 1;
        }
        cases++;
    }
    // Independent semantic assertions: ordinary borders stay hollow, and a
    // hardware-safe cover grows by at most one pixel on any marker side.
    for(int x:{0,1,2,10})for(int y:{0,1,2,10})for(int t:{1,2,3,4,5}){
        rks_letterbox_rect edges[4];int count=0;
        if(!rks_draw_border_rects(1920,1080,x,y,55,31,t,edges,&count)||count!=4)return 1;
        for(int i=0;i<count;i++){
            const auto& e=edges[i];
            if(e.x<std::max(0,x-1)||e.y<std::max(0,y-1)||e.x+e.width>x+56||e.y+e.height>y+32)return 1;
            const int center_x=x+27,center_y=y+15;
            if(center_x>=e.x&&center_x<e.x+e.width&&center_y>=e.y&&center_y<e.y+e.height)return 1;
        }
    }
    rks_letterbox_rect marker[4];int marker_count=0;
    if(!rks_draw_border_rects(1920,1080,981,1,55,2,2,marker,&marker_count)||marker_count!=1
        ||marker[0].x!=981||marker[0].y!=0||marker[0].width!=55||marker[0].height!=3)return 1;
    std::printf("hardware border geometry PASS: %d edge/origin/thickness cases; hollow centers, bounded growth, odd thickness 3/5 and exact video marker\n",cases);
    return 0;
}
