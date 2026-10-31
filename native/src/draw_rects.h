#pragma once
#include "letterbox_fill.h"
#include <algorithm>
#include <cstdint>

// Explicit inward border strips avoid imrectangle's unchecked intermediate
// rectangles (including origin 1). Each strip uses the same bounded hardware
// fill cover as a small marker: minimum 2 pixels, origin 1 grows toward zero.
static inline bool rks_draw_border_rects(int image_w,int image_h,int x,int y,
    int w,int h,int thickness,rks_letterbox_rect* rects,int* count){
    *count=0;
    if(thickness<=0||w<=0||h<=0||x<0||y<0||w>image_w-x||h>image_h-y)return false;
    if(int64_t(thickness)*2>=std::min(w,h))
        return rks_letterbox_push(rects,count,image_w,image_h,x,y,w,h);
    return rks_letterbox_push(rects,count,image_w,image_h,x,y,w,thickness)
        &&rks_letterbox_push(rects,count,image_w,image_h,x,y+h-thickness,w,thickness)
        &&rks_letterbox_push(rects,count,image_w,image_h,x,y,thickness,h)
        &&rks_letterbox_push(rects,count,image_w,image_h,x+w-thickness,y,thickness,h);
}
