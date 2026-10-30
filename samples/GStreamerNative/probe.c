#include "rknnsharp_gst.h"
#include <stdio.h>
#include <stdlib.h>
#include <inttypes.h>
#include <string.h>
#include <fcntl.h>

int main(int argc,char **argv) {
    if (argc<3) { fprintf(stderr,"usage: probe file|file-unpaced PATH [h264|h265] [frames] OR probe rtsp URL_ENV_VAR [h264|h265] [frames]\n"); return 2; }
    const char *codec=argc>3?argv[3]:"h264";
    int limit=argc>4?atoi(argv[4]):12;
    if (limit<1 || limit>2000) return 2;
    char error[256]={0}; rkgst_capture *c=NULL;
    int rtsp=!strcmp(argv[1],"rtsp");
    int unpaced=!strcmp(argv[1],"file-unpaced");
    int rc=rtsp?rkgst_open_rtsp(getenv(argv[2]),codec,100,&c,error,sizeof(error)):
          unpaced?rkgst_open_file_unpaced(argv[2],codec,&c,error,sizeof(error)):
                  rkgst_open_file(argv[2],codec,&c,error,sizeof(error));
    if (rc) { fprintf(stderr,"%s\n",error); return 1; }
    int frames=0,timeouts=0; uint64_t previous=0;
    for (int attempt=0;frames<limit && attempt<limit+10;attempt++) {
        rkgst_frame f={0}; rc=rkgst_pull(c,3000,&f,error,sizeof(error));
        if (rc<0) { fprintf(stderr,"%s\n",error); break; }
        if (rc==2) { puts("EOS"); break; }
        if (!rc) { timeouts++; continue; }
        if (fcntl(f.fd[0],F_GETFD)<0 || fcntl(f.fd[1],F_GETFD)<0) { rkgst_release(&f); rc=-1; break; }
        if (frames && f.pts_ns!=UINT64_MAX && f.pts_ns<previous) { fprintf(stderr,"PTS regressed\n"); rkgst_release(&f); rc=-1; break; }
        previous=f.pts_ns;
        printf("frame=%d abi=%u %ux%u color=%u fd=%d/%d pitch=%d/%d offset=%"PRIu64"/%"PRIu64" allocation=%"PRIu64"/%"PRIu64" pts=%"PRIu64" running=%"PRIu64" monotonic=%"PRIu64"\n",
          ++frames,f.struct_size,f.width,f.height,f.color_space,f.fd[0],f.fd[1],f.stride[0],f.stride[1],f.offset[0],f.offset[1],f.allocation_size[0],f.allocation_size[1],f.pts_ns,f.running_time_ns,f.arrival_monotonic_ns);
        rkgst_release(&f);
    }
    rkgst_close(c);
    printf("frames=%d timeouts=%d\n",frames,timeouts);
    return rc<0 || frames==0 ? 1 : 0;
}
