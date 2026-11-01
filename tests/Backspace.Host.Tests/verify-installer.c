// Exercise the production verification and file lock without running an installer.
#define wmain BackspaceEntry
#include "../../src/Backspace.Host/host.c"
#undef wmain

int wmain(int argc,wchar_t** argv) {
    if(argc!=2){fwprintf(stderr,L"Expected a disposable copy of the pinned runtime installer.\n");return 2;}
    DWORD error=0;
    HANDLE verified=lock_verified_installer(argv[1],&error);
    if(verified==INVALID_HANDLE_VALUE){fwprintf(stderr,L"Valid Microsoft installer rejected: %lu\n",error);return 1;}
    HANDLE writer=CreateFileW(argv[1],GENERIC_WRITE,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,NULL,OPEN_EXISTING,0,NULL);
    if(writer!=INVALID_HANDLE_VALUE){CloseHandle(writer);CloseHandle(verified);fputs("Verified bytes remained writable.\n",stderr);return 1;}
    if(GetLastError()!=ERROR_SHARING_VIOLATION||DeleteFileW(argv[1])||GetLastError()!=ERROR_SHARING_VIOLATION){CloseHandle(verified);fputs("Replacement was not blocked by the held lock.\n",stderr);return 1;}
    CloseHandle(verified);
    writer=CreateFileW(argv[1],GENERIC_WRITE,0,NULL,OPEN_EXISTING,0,NULL);
    if(writer==INVALID_HANDLE_VALUE){fputs("Verification leaked its lock.\n",stderr);return 1;}
    BYTE byte=0;DWORD count=0;BOOL changed=WriteFile(writer,&byte,1,&count,NULL);CloseHandle(writer);
    if(!changed||count!=1)return 1;
    verified=lock_verified_installer(argv[1],&error);
    if(verified!=INVALID_HANDLE_VALUE){CloseHandle(verified);fputs("Tampered installer accepted.\n",stderr);return 1;}
    if(error!=ERROR_INVALID_DATA)return 1;
    puts("PASS: authentic pinned installer accepted; write/delete blocked while verified; lock released; tampered installer rejected without execution.");
    return 0;
}
