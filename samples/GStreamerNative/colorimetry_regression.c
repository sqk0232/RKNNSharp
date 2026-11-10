/* Include the bridge to exercise its private caps policy without exporting a
 * test-only ABI. This test needs GStreamer, but no decoder or hardware device. */
#include "rknnsharp_gst.c"
#include <stdio.h>

int main(void) {
    static const struct { const char *value; uint32_t expected; } cases[]={
        {"bt709",3}, {NULL,0}, {"bt601",1}, {"1:4:0:0",2},
        {"2:3:0:0",3}, {"2:4:0:0",1},
        {"2:0:0:0",0}, {"0:0:0:0",0}, {"not-a-color",0},
        {"sRGB",0}, {"bt2020",0}, {"1:3:0:0",0}, {"0:3:0:0",0},
        {NULL,0}, {"bt709",3}
    };
    static const int heights[]={480,1080};
    gst_init(NULL,NULL);
    unsigned checked=0;
    for (guint h=0;h<G_N_ELEMENTS(heights);h++) {
        GstCaps *caps=gst_caps_new_simple("video/x-raw","format",G_TYPE_STRING,"NV12",
            "width",G_TYPE_INT,640,"height",G_TYPE_INT,heights[h],NULL);
        for (guint i=0;i<G_N_ELEMENTS(cases);i++) {
            GstStructure *s=gst_caps_get_structure(caps,0);
            if (cases[i].value) gst_structure_set(s,"colorimetry",G_TYPE_STRING,cases[i].value,NULL);
            else gst_structure_remove_field(s,"colorimetry");
            uint32_t actual=explicit_color_space(caps);
            if (actual!=cases[i].expected) {
                fprintf(stderr,"height=%d colorimetry=%s expected=%u actual=%u\n",
                    heights[h],cases[i].value ? cases[i].value : "(missing)",cases[i].expected,actual);
                gst_caps_unref(caps);
                return 1;
            }
            checked++;
        }
        gst_structure_set(gst_caps_get_structure(caps,0),"colorimetry",G_TYPE_INT,3,NULL);
        if (explicit_color_space(caps)!=0) {
            fprintf(stderr,"Non-string colorimetry must remain unsupported\n");
            gst_caps_unref(caps);
            return 1;
        }
        checked++;
        gst_caps_unref(caps);
    }
    printf("explicit colorimetry regression: %u cases passed\n",checked);
    return 0;
}
