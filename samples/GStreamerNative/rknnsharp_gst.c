#include "rknnsharp_gst.h"
#include <gst/gst.h>
#include <gst/app/gstappsink.h>
#include <gst/allocators/gstdmabuf.h>
#include <gst/video/video.h>
#include <string.h>

struct rkgst_capture { GstElement *pipeline; GstAppSink *sink; GstBus *bus; gboolean paused; };
static int fail(char *error, uint32_t size, const char *message) {
    if (error && size) g_strlcpy(error, message, size);
    return -1;
}
static int bus_error(rkgst_capture *c, char *error, uint32_t size) {
    GstMessage *m = gst_bus_pop_filtered(c->bus, GST_MESSAGE_ERROR);
    if (!m) return 0;
    /* Native error/debug strings can contain a URL including credentials. */
    GError *e = NULL;
    gst_message_parse_error(m,&e,NULL);
    int unauthorized = e && (strstr(e->message,"401") || strstr(e->message,"Unauthorized"));
    if (error && size) g_snprintf(error, size, "GStreamer error from %s domain=%s code=%d%s (details suppressed)",
        GST_OBJECT_NAME(m->src), e ? g_quark_to_string(e->domain) : "unknown", e ? e->code : 0,
        unauthorized ? " response=401 Unauthorized" : "");
    g_clear_error(&e);
    gst_message_unref(m);
    return -1;
}
void rkgst_close(rkgst_capture *c) {
    if (!c) return;
    gst_element_set_state(c->pipeline, GST_STATE_NULL);
    gst_object_unref(c->sink);
    gst_object_unref(c->bus);
    gst_object_unref(c->pipeline);
    g_free(c);
}
static int open_source(const char *location, const char *codec, uint32_t latency,
                       int rtsp, int unpaced, rkgst_capture **out, char *error, uint32_t size) {
    if (out) *out = NULL;
    if (!out || !location || !*location || !codec ||
        (strcmp(codec,"h264") && strcmp(codec,"h265")) || latency > 10000)
        return fail(error,size,"Invalid capture arguments");
    GError *e = NULL;
    if (!gst_init_check(NULL,NULL,&e)) { g_clear_error(&e); return fail(error,size,"GStreamer initialization failed"); }
    const char *encoding = !strcmp(codec,"h264") ? "H264" : "H265";
    gchar *description = g_strdup_printf(
        "%s ! %s%s%s ! %sparse ! mppvideodec dma-feature=true arm-afbc=false format=NV12 ignore-error=false "
        /* This vendor build emits ordinary raw caps even for real DMABUF.
         * Verify GstMemory at pull time; forcing the feature yields no frames. */
        "! video/x-raw,format=NV12 "
        "! appsink name=frames max-buffers=1 drop=%s enable-last-sample=false wait-on-eos=false sync=%s",
        rtsp ? "rtspsrc name=source protocols=tcp drop-on-latency=true tcp-timeout=5000000 teardown-timeout=1000000000" : "filesrc name=source ! qtdemux",
        rtsp ? "application/x-rtp,media=video,encoding-name=" : "", rtsp ? encoding : "",
        rtsp ? (!strcmp(codec,"h264") ? " ! rtph264depay" : " ! rtph265depay") : "identity",
        codec,unpaced ? "false" : "true",unpaced ? "false" : "true");
    GstElement *pipeline = gst_parse_launch(description,&e);
    g_free(description);
    if (e || !pipeline) {
        g_clear_error(&e);
        if (pipeline) gst_object_unref(pipeline);
        return fail(error,size,"Pipeline construction failed; verify MPP, parser and RTSP plugins");
    }
    rkgst_capture *c = g_new0(rkgst_capture,1);
    c->pipeline = pipeline;
    c->sink = GST_APP_SINK(gst_bin_get_by_name(GST_BIN(pipeline),"frames"));
    c->bus = gst_element_get_bus(pipeline);
    GstElement *source = gst_bin_get_by_name(GST_BIN(pipeline),"source");
    g_object_set(source,"location",location,NULL);
    if (rtsp) g_object_set(source,"latency",latency,NULL);
    gst_object_unref(source);
    if (gst_element_set_state(pipeline,GST_STATE_PLAYING) == GST_STATE_CHANGE_FAILURE) {
        rkgst_close(c); return fail(error,size,"Pipeline failed to start");
    }
    *out = c;
    return 0;
}
int rkgst_open_rtsp(const char *url,const char *codec,uint32_t latency,rkgst_capture **out,char *error,uint32_t size) {
    if (out) *out=NULL;
    if (!url || (!g_str_has_prefix(url,"rtsp://") && !g_str_has_prefix(url,"rtsps://")))
        return fail(error,size,"A complete RTSP URL is required");
    return open_source(url,codec,latency,1,0,out,error,size);
}
int rkgst_open_file(const char *path,const char *codec,rkgst_capture **out,char *error,uint32_t size) {
    return open_source(path,codec,0,0,0,out,error,size);
}
int rkgst_open_file_unpaced(const char *path,const char *codec,rkgst_capture **out,char *error,uint32_t size) {
    return open_source(path,codec,0,0,1,out,error,size);
}
int rkgst_pause(rkgst_capture *c,char *error,uint32_t size) {
    if (!c) return fail(error,size,"Invalid pause arguments");
    if (bus_error(c,error,size)) return -1;
    GstStateChangeReturn requested=gst_element_set_state(c->pipeline,GST_STATE_PAUSED);
    if (requested==GST_STATE_CHANGE_FAILURE)
        return fail(error,size,"GStreamer failed to request PAUSED");
    GstState current=GST_STATE_VOID_PENDING,pending=GST_STATE_VOID_PENDING;
    GstStateChangeReturn settled=gst_element_get_state(c->pipeline,&current,&pending,5*GST_SECOND);
    if (settled==GST_STATE_CHANGE_FAILURE) {
        if (bus_error(c,error,size)) return -1;
        return fail(error,size,"GStreamer failed while entering PAUSED");
    }
    if ((settled!=GST_STATE_CHANGE_SUCCESS && settled!=GST_STATE_CHANGE_NO_PREROLL) ||
        current!=GST_STATE_PAUSED || pending!=GST_STATE_VOID_PENDING)
        return fail(error,size,"GStreamer PAUSED transition did not settle within 5 seconds");
    c->paused=TRUE;
    if (bus_error(c,error,size)) return -1;
    return 0;
}
void rkgst_release(rkgst_frame *f) {
    if (!f) return;
    if (f->token) gst_sample_unref((GstSample *)f->token);
    memset(f,0,sizeof(*f));
}
int rkgst_pull(rkgst_capture *c,uint32_t timeout,rkgst_frame *f,char *error,uint32_t size) {
    if (!c || !f) return fail(error,size,"Invalid pull arguments");
    if (f->token) return fail(error,size,"Release the previous frame before pulling");
    memset(f,0,sizeof(*f));
    if (c->paused) return fail(error,size,"Capture is paused for retained-sample processing");
    if (bus_error(c,error,size)) return -1;
    GstSample *sample = gst_app_sink_try_pull_sample(c->sink,(GstClockTime)timeout*GST_MSECOND);
    if (!sample) {
        if (bus_error(c,error,size)) return -1;
        return gst_app_sink_is_eos(c->sink) ? 2 : 0;
    }
    GstBuffer *buffer = gst_sample_get_buffer(sample);
    GstCaps *caps = gst_sample_get_caps(sample);
    if (!buffer || !caps || gst_caps_is_empty(caps)) {
        gst_sample_unref(sample); return fail(error,size,"Video sample has no buffer or caps");
    }
    GstVideoInfo info;
    GstVideoMeta *meta = gst_buffer_get_video_meta(buffer);
    gint afbc = 0;
    gst_structure_get_int(gst_caps_get_structure(caps,0),"arm-afbc",&afbc);
    GstVideoCropMeta *crop = gst_buffer_get_video_crop_meta(buffer);
    if (!gst_video_info_from_caps(&info,caps) || GST_VIDEO_INFO_FORMAT(&info)!=GST_VIDEO_FORMAT_NV12 ||
        !meta || meta->n_planes!=2 || meta->format!=GST_VIDEO_FORMAT_NV12 || afbc ||
        meta->width!=(guint)info.width || meta->height!=(guint)info.height ||
        info.width%2 || info.height%2 ||
        (crop && (crop->x || crop->y || crop->width!=(guint)info.width || crop->height!=(guint)info.height))) {
        gst_sample_unref(sample); return fail(error,size,"Requires linear, uncropped, even-size NV12 with two-plane GstVideoMeta");
    }
    for (guint p=0;p<2;p++) {
        guint idx,length; gsize skip,offset,maxsize;
        guint rows = p ? info.height/2 : info.height;
        if (meta->stride[p]<info.width) goto invalid_memory;
        gsize span = (gsize)meta->stride[p]*(rows-1)+info.width;
        if (!gst_buffer_find_memory(buffer,meta->offset[p],span,&idx,&length,&skip) || length!=1) goto invalid_memory;
        GstMemory *memory = gst_buffer_peek_memory(buffer,idx);
        if (!gst_is_dmabuf_memory(memory)) goto invalid_memory;
        gsize available = gst_memory_get_sizes(memory,&offset,&maxsize);
        if (skip>available || span>available-skip || offset>maxsize || skip>maxsize-offset) goto invalid_memory;
        f->fd[p] = gst_dmabuf_memory_get_fd(memory);
        if (f->fd[p]<0) goto invalid_memory;
        f->stride[p] = meta->stride[p];
        f->offset[p] = offset+skip;
        f->allocation_size[p] = maxsize;
    }
    f->struct_size=sizeof(*f); f->width=info.width; f->height=info.height;
    f->format=0x3231564e; f->n_planes=2;
    if (gst_structure_has_field(gst_caps_get_structure(caps,0),"colorimetry")) {
        if (info.colorimetry.matrix==GST_VIDEO_COLOR_MATRIX_BT601) {
            if (info.colorimetry.range==GST_VIDEO_COLOR_RANGE_16_235) f->color_space=1;
            else if (info.colorimetry.range==GST_VIDEO_COLOR_RANGE_0_255) f->color_space=2;
        } else if (info.colorimetry.matrix==GST_VIDEO_COLOR_MATRIX_BT709 &&
                   info.colorimetry.range==GST_VIDEO_COLOR_RANGE_16_235) f->color_space=3;
    }
    f->pts_ns=GST_BUFFER_PTS(buffer); f->dts_ns=GST_BUFFER_DTS(buffer);
    const GstSegment *segment=gst_sample_get_segment(sample);
    f->running_time_ns=segment ? gst_segment_to_running_time(segment,GST_FORMAT_TIME,f->pts_ns) : GST_CLOCK_TIME_NONE;
    f->arrival_monotonic_ns=(uint64_t)g_get_monotonic_time()*1000;
    f->token=sample;
    return 1;
invalid_memory:
    gst_sample_unref(sample); memset(f,0,sizeof(*f));
    return fail(error,size,"NV12 plane is not contained in a DMABUF allocation with a valid pitch");
}
