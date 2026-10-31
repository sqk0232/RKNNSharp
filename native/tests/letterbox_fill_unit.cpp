#include "letterbox_fill.h"
#include <algorithm>
#include <cstdio>
#include <cstdlib>

static int failures = 0;
static void require(bool cond, const char* what) {
    if (!cond) { std::fprintf(stderr, "%s\n", what); failures++; }
}

static void check_geometry(int image_w, int image_h, int left, int top, int resize_w, int resize_h) {
    rks_letterbox_rect rects[4]{};
    int count = -1;
    bool ok = rks_letterbox_border_rects(image_w, image_h, left, top, resize_w, resize_h, rects, &count);
    if (!ok || count < 0 || count > 4) {
        std::fprintf(stderr, "rejected %dx%d content %d,%d %dx%d\n", image_w, image_h, left, top, resize_w, resize_h);
        failures++;
        return;
    }
    for (int i = 0; i < count; i++) {
        const auto& rect = rects[i];
        if (rect.width < 2 || rect.height < 2 || rect.x == 1 || rect.y == 1 || rect.x < 0 || rect.y < 0 ||
            rect.x > image_w - rect.width || rect.y > image_h - rect.height) {
            std::fprintf(stderr, "unsafe rect %d,%d %dx%d on %dx%d\n", rect.x, rect.y, rect.width, rect.height, image_w, image_h);
            failures++;
            return;
        }
    }
    for (int y = 0; y < image_h; y++) for (int x = 0; x < image_w; x++) {
        bool border = x < left || x >= left + resize_w || y < top || y >= top + resize_h;
        if (!border) continue;
        bool covered = false;
        for (int i = 0; i < count; i++) {
            const auto& rect = rects[i];
            if (x >= rect.x && x < rect.x + rect.width && y >= rect.y && y < rect.y + rect.height) { covered = true; break; }
        }
        if (!covered) {
            std::fprintf(stderr, "uncovered border %d,%d image %dx%d content %d,%d %dx%d\n",
                x, y, image_w, image_h, left, top, resize_w, resize_h);
            failures++;
            return;
        }
    }
}

static void expect_bars(int image_w, int image_h, int left, int top, int resize_w, int resize_h,
    int expected_count, long long expected_area) {
    rks_letterbox_rect rects[4]{};
    int count = 0;
    require(rks_letterbox_border_rects(image_w, image_h, left, top, resize_w, resize_h, rects, &count), "bar geometry rejected");
    require(count == expected_count, "unexpected bar count");
    long long area = 0;
    for (int i = 0; i < count; i++) area += static_cast<long long>(rects[i].width) * rects[i].height;
    require(area == expected_area, "unexpected bar area");
    check_geometry(image_w, image_h, left, top, resize_w, resize_h);
}

int main() {
    long long checked = 0;
    for (int image_w = 2; image_w <= 12; image_w++) for (int image_h = 2; image_h <= 12; image_h++)
        for (int resize_w = 2; resize_w <= image_w; resize_w++) for (int resize_h = 2; resize_h <= image_h; resize_h++)
            for (int left = 0; left <= image_w - resize_w; left++) for (int top = 0; top <= image_h - resize_h; top++) {
                check_geometry(image_w, image_h, left, top, resize_w, resize_h);
                checked++;
            }
    const int sizes[] = {2, 3, 15, 16, 17, 31, 32, 33, 63, 64, 639, 640, 641};
    for (int image_w : sizes) for (int image_h : sizes) {
        for (int resize_w = 2; resize_w <= image_w; resize_w += std::max(1, (image_w - 2) / 6))
            for (int resize_h = 2; resize_h <= image_h; resize_h += std::max(1, (image_h - 2) / 6)) {
                int left = (image_w - resize_w) / 2, top = (image_h - resize_h) / 2;
                check_geometry(image_w, image_h, left, top, resize_w, resize_h);
                check_geometry(image_w, image_h, 0, top, resize_w, resize_h);
                check_geometry(image_w, image_h, image_w - resize_w, image_h - resize_h, resize_w, resize_h);
                checked += 3;
            }
    }
    // 1920x1080 into 640x640: only the top and bottom bars remain.
    expect_bars(640, 640, 0, 140, 640, 360, 2, 640ll * 140 * 2);
    expect_bars(640, 640, 140, 0, 360, 640, 2, 140ll * 640 * 2);
    rks_letterbox_rect wide[4]{};
    int wide_count = 0;
    require(rks_letterbox_border_rects(640, 640, 0, 140, 640, 360, wide, &wide_count), "wide bars rejected");
    require(wide_count == 2 && wide[0].x == 0 && wide[0].y == 0 && wide[0].width == 640 && wide[0].height == 140, "top bar mismatch");
    require(wide[1].x == 0 && wide[1].y == 500 && wide[1].width == 640 && wide[1].height == 140, "bottom bar mismatch");

    rks_letterbox_rect invalid[4]{};
    int count = 7;
    require(!rks_letterbox_border_rects(1, 8, 0, 0, 1, 2, invalid, &count), "tiny image accepted");
    require(!rks_letterbox_border_rects(8, 8, -1, 0, 2, 2, invalid, &count), "negative left accepted");
    require(!rks_letterbox_border_rects(8, 8, 0, 0, 9, 2, invalid, &count), "overflow width accepted");
    require(!rks_letterbox_border_rects(8, 8, 0, 7, 2, 2, invalid, &count), "overflow height accepted");

    if (failures) {
        std::fprintf(stderr, "letterbox_fill_unit failed: %d\n", failures);
        return 1;
    }
    std::printf("letterbox_fill_unit passed geometries=%lld wide640_border_pixels=%lld full_pixels=%d\n",
        checked, 640ll * 140 * 2, 640 * 640);
    return 0;
}
