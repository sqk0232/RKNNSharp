#pragma once

// Cover letterbox padding with solid RGB fills. Rectangles may overlap the
// resize destination: librga 1.10.1 rejects a fill origin of exactly 1 and
// extents below 2, so a 1px border grows into the region the following resize
// overwrites. Returns false when no safe cover exists; the caller then fills
// the whole frame, which the resize also overwrites in the content rectangle.
struct rks_letterbox_rect { int x, y, width, height; };

static inline bool rks_letterbox_push(rks_letterbox_rect* rects, int* count,
    int image_w, int image_h, int x, int y, int w, int h) {
    if (w <= 0 || h <= 0) return true;
    if (w == 1) {
        if (x + 2 <= image_w) w = 2;
        else if (x >= 1) { x -= 1; w = 2; }
        else return false;
    }
    if (h == 1) {
        if (y + 2 <= image_h) h = 2;
        else if (y >= 1) { y -= 1; h = 2; }
        else return false;
    }
    // Growing left/up keeps the original far edge, so a 1px origin moves to 0.
    if (x == 1) { x = 0; w += 1; }
    if (y == 1) { y = 0; h += 1; }
    if (x < 0 || y < 0 || w < 2 || h < 2 || x > image_w - w || y > image_h - h || x == 1 || y == 1)
        return false;
    if (*count >= 4) return false;
    rects[(*count)++] = rks_letterbox_rect{x, y, w, h};
    return true;
}

static inline bool rks_letterbox_border_rects(int image_w, int image_h, int left, int top,
    int resize_w, int resize_h, rks_letterbox_rect* rects, int* count) {
    *count = 0;
    if (image_w < 2 || image_h < 2 || resize_w < 2 || resize_h < 2) return false;
    if (left < 0 || top < 0 || resize_w > image_w - left || resize_h > image_h - top) return false;
    int right = image_w - left - resize_w;
    int bottom = image_h - top - resize_h;
    return rks_letterbox_push(rects, count, image_w, image_h, 0, 0, image_w, top)
        && rks_letterbox_push(rects, count, image_w, image_h, 0, top + resize_h, image_w, bottom)
        && rks_letterbox_push(rects, count, image_w, image_h, 0, top, left, resize_h)
        && rks_letterbox_push(rects, count, image_w, image_h, left + resize_w, top, right, resize_h);
}
