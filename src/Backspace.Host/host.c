// Native Windows startup for a framework-dependent .NET single-file bundle.
// No CLR is linked or shipped here. The two patch markers follow the .NET
// SDK apphost format; see docs/RUNTIME-PACKAGING.md and THIRD-PARTY-NOTICES.
#ifndef UNICODE
#define UNICODE
#endif
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0A00
#endif
#include <windows.h>
#include <commctrl.h>
#include <shellapi.h>
#include <shlobj.h>
#include <winhttp.h>
#include <bcrypt.h>
#include <wintrust.h>
#include <softpub.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
#include "runtime-config.h"

#define COUNT(a) (sizeof(a)/sizeof((a)[0]))
#define MISSING_RUNTIME ((int)0x80008096)
#define MAX_DOWNLOAD (100u * 1024u * 1024u)
static char managed_name[1025] = "c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2";
static volatile unsigned char bundle_marker[40] = {
    0,0,0,0,0,0,0,0, 0x8b,0x12,0x02,0xb9,0x6a,0x61,0x20,0x38,
    0x72,0x7b,0x93,0x02,0x14,0xd7,0xa0,0x32,0x13,0xf5,0xb9,0xe6,
    0xef,0xae,0x33,0x18,0xee,0x3b,0x2d,0xce,0x24,0xb3,0x6a,0xae
};
typedef int (__cdecl *bundle_main_fn)(int,const wchar_t**,const wchar_t*,const wchar_t*,const wchar_t*,int64_t);
typedef int (__cdecl *plain_main_fn)(int,const wchar_t**,const wchar_t*,const wchar_t*,const wchar_t*);
typedef void (__cdecl *error_fn)(const wchar_t*);
typedef error_fn (__cdecl *set_error_fn)(error_fn);

static void write_error(const wchar_t* text) {
    HANDLE h=GetStdHandle(STD_ERROR_HANDLE); DWORD written;
    char bytes[8192]; int n=WideCharToMultiByte(CP_UTF8,0,text,-1,bytes,sizeof(bytes)-2,NULL,NULL);
    if(n>0 && h && h!=INVALID_HANDLE_VALUE){bytes[n-1]='\n';WriteFile(h,bytes,n,&written,NULL);}
}

static BOOL module_path(wchar_t* path) {
    DWORD n=GetModuleFileNameW(NULL,path,32768); return n>0 && n<32768;
}

static BOOL runtime_location(wchar_t* root,wchar_t* fxr) {
    DWORD size=32768*sizeof(wchar_t);
    if(RegGetValueW(HKEY_LOCAL_MACHINE,L"SOFTWARE\\dotnet\\Setup\\InstalledVersions\\x64",
        L"InstallLocation",RRF_RT_REG_SZ|RRF_SUBKEY_WOW6464KEY,NULL,root,&size)!=ERROR_SUCCESS){
        PWSTR folder=NULL;
        if(FAILED(SHGetKnownFolderPath(&FOLDERID_ProgramFiles,0,NULL,&folder))) return FALSE;
        int n=swprintf(root,32768,L"%ls\\dotnet",folder);CoTaskMemFree(folder);
        if(n<0)return FALSE;
    }
    // Only machine-registered/default locations. Never load a privileged
    // service's CLR from a user-controlled DOTNET_ROOT or working directory.
    if(wcslen(root)<3||root[1]!=L':'||root[2]!=L'\\')return FALSE;
    wchar_t pattern[32768];if(swprintf(pattern,COUNT(pattern),L"%ls\\host\\fxr\\*",root)<0)return FALSE;
    WIN32_FIND_DATAW item;HANDLE find=FindFirstFileW(pattern,&item);if(find==INVALID_HANDLE_VALUE)return FALSE;
    int bestMajor=-1,bestMinor=-1,bestPatch=-1;wchar_t best[128]={0};
    do{
        int major,minor,patch;wchar_t extra;
        if(!(item.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY)||wcslen(item.cFileName)>=COUNT(best)||
            swscanf(item.cFileName,L"%d.%d.%d%lc",&major,&minor,&patch,&extra)!=3)continue;
        if(major>bestMajor||(major==bestMajor&&(minor>bestMinor||(minor==bestMinor&&patch>bestPatch)))){
            bestMajor=major;bestMinor=minor;bestPatch=patch;wcscpy(best,item.cFileName);
        }
    }while(FindNextFileW(find,&item));FindClose(find);
    return bestMajor>=10 && swprintf(fxr,32768,L"%ls\\host\\fxr\\%ls\\hostfxr.dll",root,best)>0;
}

static int run_managed(int argc,const wchar_t** argv) {
    wchar_t exe[32768],root[32768],fxr[32768],app[32768],name[1025];
    if(!module_path(exe))return 4;
    if(!runtime_location(root,fxr))return MISSING_RUNTIME;
    if(!MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,managed_name,-1,name,COUNT(name)))return 4;
    // The unpatched template must never execute a sibling chosen by a user.
    if(wcscmp(name,L"Backspace.dll")!=0)return 4;
    wcscpy(app,exe);wchar_t* slash=wcsrchr(app,L'\\');if(!slash)return 4;*(slash+1)=0;
    if(wcslen(app)+wcslen(name)>=COUNT(app))return 4;wcscat(app,name);
    HMODULE library=LoadLibraryExW(fxr,NULL,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
    if(!library)return MISSING_RUNTIME;
    set_error_fn errors=(set_error_fn)GetProcAddress(library,"hostfxr_set_error_writer");
    if(errors)errors(write_error);
    int64_t offset=0;for(int n=0;n<8;n++)offset|=((int64_t)bundle_marker[n])<<(8*n);
    int code=4;
    if(offset){
        bundle_main_fn main=(bundle_main_fn)GetProcAddress(library,"hostfxr_main_bundle_startupinfo");
        if(main)code=main(argc,argv,exe,root,app,offset);
    }else{
        plain_main_fn main=(plain_main_fn)GetProcAddress(library,"hostfxr_main_startupinfo");
        if(main)code=main(argc,argv,exe,root,app);
    }
    // Do not unload a host that may have initialized the CLR. The process
    // is about to exit; missing-runtime installation resumes in a new process.
    return code;
}

typedef struct {
    HWND window;HANDLE thread;volatile LONG cancelled,finished;DWORD error;
    wchar_t folder[32768],installer[32768];
} install_job;
static void progress(install_job* job,const wchar_t* text){SendMessageW(job->window,TDM_SET_ELEMENT_TEXT,TDE_CONTENT,(LPARAM)text);}
static BOOL cancelled(install_job* job){return InterlockedCompareExchange(&job->cancelled,0,0)!=0;}

static DWORD download_runtime(install_job* job) {
    DWORD result=ERROR_INVALID_DATA,received,total=0;
    HANDLE file=INVALID_HANDLE_VALUE;HINTERNET session=NULL,connection=NULL,request=NULL;
    BCRYPT_ALG_HANDLE algorithm=NULL;BCRYPT_HASH_HANDLE hash=NULL;
    BYTE digest[64],buffer[65536];
    wchar_t host[256],urlPath[2048];URL_COMPONENTS url={sizeof(url)};
    url.lpszHostName=host;url.dwHostNameLength=COUNT(host);url.lpszUrlPath=urlPath;url.dwUrlPathLength=COUNT(urlPath);
    if(!WinHttpCrackUrl(RUNTIME_URL,0,0,&url)||url.nScheme!=INTERNET_SCHEME_HTTPS)goto done;
    session=WinHttpOpen(L"Backspace runtime setup/1",WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,NULL,NULL,0);
    if(!session)goto done;
    WinHttpSetTimeouts(session,15000,15000,15000,15000);
    connection=WinHttpConnect(session,host,url.nPort,0);if(!connection)goto done;
    request=WinHttpOpenRequest(connection,L"GET",urlPath,NULL,WINHTTP_NO_REFERER,WINHTTP_DEFAULT_ACCEPT_TYPES,WINHTTP_FLAG_SECURE);
    if(!request)goto done;
    DWORD redirect=WINHTTP_OPTION_REDIRECT_POLICY_NEVER;
    if(!WinHttpSetOption(request,WINHTTP_OPTION_REDIRECT_POLICY,&redirect,sizeof(redirect)))goto done;
    if(!WinHttpSendRequest(request,NULL,0,NULL,0,0,0)||!WinHttpReceiveResponse(request,NULL))goto done;
    DWORD status=0,length=sizeof(status);
    if(!WinHttpQueryHeaders(request,WINHTTP_QUERY_STATUS_CODE|WINHTTP_QUERY_FLAG_NUMBER,NULL,&status,&length,NULL)||status!=200)goto done;
    file=CreateFileW(job->installer,GENERIC_WRITE,0,NULL,CREATE_NEW,FILE_ATTRIBUTE_TEMPORARY,NULL);if(file==INVALID_HANDLE_VALUE)goto done;
    if(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA512_ALGORITHM,NULL,0)<0||BCryptCreateHash(algorithm,&hash,NULL,0,NULL,0,0)<0)goto done;
    for(;;){
        if(cancelled(job)){result=ERROR_CANCELLED;goto done;}
        if(!WinHttpReadData(request,buffer,sizeof(buffer),&received))goto done;
        if(!received)break;
        if(received>MAX_DOWNLOAD-total)goto done;total+=received;
        DWORD written;if(!WriteFile(file,buffer,received,&written,NULL)||written!=received||BCryptHashData(hash,buffer,received,0)<0)goto done;
        wchar_t message[160];swprintf(message,COUNT(message),L"Downloading Microsoft .NET… %u MB received.\nBackspace and your data stay where they are.",total/(1024*1024));progress(job,message);
    }
    if(!total||!FlushFileBuffers(file)||BCryptFinishHash(hash,digest,sizeof(digest),0)<0)goto done;
    for(int n=0;n<64;n++)if(digest[n]!=RUNTIME_SHA512[n])goto done;
    result=ERROR_SUCCESS;
done:
    if(hash)BCryptDestroyHash(hash);if(algorithm)BCryptCloseAlgorithmProvider(algorithm,0);
    if(file!=INVALID_HANDLE_VALUE)CloseHandle(file);
    if(request)WinHttpCloseHandle(request);if(connection)WinHttpCloseHandle(connection);if(session)WinHttpCloseHandle(session);
    return result;
}

// Keep the verified bytes locked against replacement through installation.
// Recheck the hash after opening this handle: the downloader has closed its
// output, so another process could otherwise replace it before elevation.
static HANDLE lock_verified_installer(const wchar_t* path,DWORD* error) {
    HANDLE file=CreateFileW(path,GENERIC_READ,FILE_SHARE_READ,NULL,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,NULL);
    if(file==INVALID_HANDLE_VALUE){*error=GetLastError();return file;}
    BCRYPT_ALG_HANDLE algorithm=NULL;BCRYPT_HASH_HANDLE hash=NULL;
    BYTE digest[64],buffer[65536];DWORD received,total=0;BOOL valid=FALSE;
    *error=ERROR_INVALID_DATA;
    if(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA512_ALGORITHM,NULL,0)<0||BCryptCreateHash(algorithm,&hash,NULL,0,NULL,0,0)<0)goto done;
    for(;;){
        if(!ReadFile(file,buffer,sizeof(buffer),&received,NULL))goto done;
        if(!received)break;
        if(received>MAX_DOWNLOAD-total)goto done;total+=received;
        if(BCryptHashData(hash,buffer,received,0)<0)goto done;
    }
    if(!total||BCryptFinishHash(hash,digest,sizeof(digest),0)<0)goto done;
    for(int n=0;n<64;n++)if(digest[n]!=RUNTIME_SHA512[n])goto done;
    WINTRUST_FILE_INFO info={sizeof(info)};info.pcwszFilePath=path;info.hFile=file;
    WINTRUST_DATA trust={sizeof(trust)};trust.dwUIChoice=WTD_UI_NONE;trust.fdwRevocationChecks=WTD_REVOKE_WHOLECHAIN;
    trust.dwUnionChoice=WTD_CHOICE_FILE;trust.pFile=&info;trust.dwStateAction=WTD_STATEACTION_VERIFY;
    GUID policy=WINTRUST_ACTION_GENERIC_VERIFY_V2;
    LONG verified=WinVerifyTrust(NULL,&policy,&trust);trust.dwStateAction=WTD_STATEACTION_CLOSE;WinVerifyTrust(NULL,&policy,&trust);
    if(verified!=ERROR_SUCCESS){*error=(DWORD)verified;goto done;}
    valid=TRUE;*error=ERROR_SUCCESS;
done:
    if(hash)BCryptDestroyHash(hash);if(algorithm)BCryptCloseAlgorithmProvider(algorithm,0);
    if(!valid){CloseHandle(file);return INVALID_HANDLE_VALUE;}return file;
}

static DWORD WINAPI install_worker(void* argument) {
    install_job* job=argument;DWORD result=ERROR_CANCELLED;HANDLE verifiedFile=INVALID_HANDLE_VALUE;
    wchar_t temp[32768];GUID id;wchar_t guid[40];
    if(!GetTempPathW(COUNT(temp),temp)||FAILED(CoCreateGuid(&id))||!StringFromGUID2(&id,guid,COUNT(guid)))goto done;
    if(swprintf(job->folder,COUNT(job->folder),L"%lsBackspace-runtime-%ls",temp,guid)<0||!CreateDirectoryW(job->folder,NULL))goto done;
    if(swprintf(job->installer,COUNT(job->installer),L"%ls\\dotnet-runtime.exe",job->folder)<0)goto done;
    result=download_runtime(job);if(result||cancelled(job)){if(cancelled(job))result=ERROR_CANCELLED;goto done;}
    progress(job,L"Checking Microsoft’s signature…");
    verifiedFile=lock_verified_installer(job->installer,&result);
    if(verifiedFile==INVALID_HANDLE_VALUE)goto done;
    if(cancelled(job)){result=ERROR_CANCELLED;goto done;}
    SendMessageW(job->window,TDM_ENABLE_BUTTON,IDCANCEL,FALSE);
    progress(job,L"Approve Microsoft’s installer in the Windows prompt.\nBackspace will open when installation finishes. No restart will be forced.");
    SHELLEXECUTEINFOW start={sizeof(start)};start.fMask=SEE_MASK_NOCLOSEPROCESS|SEE_MASK_NOASYNC;
    start.hwnd=job->window;start.lpVerb=L"runas";start.lpFile=job->installer;
    start.lpParameters=L"/install /passive /norestart";start.nShow=SW_SHOWNORMAL;
    if(!ShellExecuteExW(&start)){result=GetLastError();goto done;}
    WaitForSingleObject(start.hProcess,INFINITE);GetExitCodeProcess(start.hProcess,&result);CloseHandle(start.hProcess);
done:
    if(verifiedFile!=INVALID_HANDLE_VALUE)CloseHandle(verifiedFile);
    if(job->installer[0])DeleteFileW(job->installer);
    if(job->folder[0])RemoveDirectoryW(job->folder);
    job->error=result;InterlockedExchange(&job->finished,1);
    SendMessageW(job->window,TDM_ENABLE_BUTTON,IDOK,TRUE);
    PostMessageW(job->window,TDM_CLICK_BUTTON,IDOK,0);return 0;
}

static HRESULT CALLBACK install_callback(HWND window,UINT notice,WPARAM button,LPARAM unused,LONG_PTR data) {
    (void)unused;install_job* job=(install_job*)data;
    if(notice==TDN_CREATED){
        job->window=window;SendMessageW(window,TDM_SET_MARQUEE_PROGRESS_BAR,TRUE,0);
        SendMessageW(window,TDM_SET_PROGRESS_BAR_MARQUEE,TRUE,0);
        SendMessageW(window,TDM_ENABLE_BUTTON,IDOK,FALSE);
        job->thread=CreateThread(NULL,0,install_worker,job,0,NULL);
        if(!job->thread){job->error=GetLastError();job->finished=1;PostMessageW(window,TDM_CLICK_BUTTON,IDCANCEL,0);}
    }
    if(notice==TDN_BUTTON_CLICKED){
        if(InterlockedCompareExchange(&job->finished,0,0))return S_OK;
        if(button==IDCANCEL){InterlockedExchange(&job->cancelled,1);progress(job,L"Cancelling the download…");}
        return S_FALSE;
    }
    return S_OK;
}

static DWORD install_runtime(void) {
    install_job job={0};TASKDIALOGCONFIG dialog={sizeof(dialog)};
    dialog.dwFlags=TDF_SHOW_MARQUEE_PROGRESS_BAR|TDF_ALLOW_DIALOG_CANCELLATION|TDF_SIZE_TO_CONTENT;
    dialog.dwCommonButtons=TDCBF_OK_BUTTON|TDCBF_CANCEL_BUTTON;
    dialog.pszWindowTitle=L"Backspace";dialog.pszMainInstruction=L"Preparing Backspace";
    dialog.pszContent=L"Downloading Microsoft .NET…";dialog.pfCallback=install_callback;dialog.lpCallbackData=(LONG_PTR)&job;
    HRESULT shown=TaskDialogIndirect(&dialog,NULL,NULL,NULL);
    if(job.thread){WaitForSingleObject(job.thread,INFINITE);CloseHandle(job.thread);}
    return FAILED(shown)?ERROR_INVALID_FUNCTION:job.error;
}

int wmain(int argc,wchar_t** argv) {
    BOOL retry=argc>1 && wcscmp(argv[argc-1],L"--runtime-setup-complete")==0;
    if(retry)argc--;
    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32|LOAD_LIBRARY_SEARCH_USER_DIRS);
    int result=run_managed(argc,(const wchar_t**)argv);
    if(result!=MISSING_RUNTIME)return result;
    // Service, scheduled, command-line, update and elevated helper invocations
    // fail promptly. They must never hang waiting for invisible UI or consent.
    DWORD session=0;ProcessIdToSessionId(GetCurrentProcessId(),&session);
    BOOL interactive=session!=0&&(argc==1||(argc==3&&wcscmp(argv[1],L"--restore")==0));
    write_error(L"Backspace needs Microsoft .NET 10 (x64). Open Backspace normally to install it; this unattended operation did not run.");
    if(!interactive)return 4;
    if(retry){MessageBoxW(NULL,L"Microsoft’s installer finished, but Backspace still cannot load .NET 10 (x64). Restart Windows if the installer requested it, then try again. Your Backspace files and data were preserved.",L"Backspace could not start",MB_OK|MB_ICONWARNING);return 4;}
    if(MessageBoxW(NULL,L"Backspace needs Microsoft .NET to run.\n\nDownload and install it now? Windows may ask for administrator approval. Backspace and your data will stay in their current folder.",
        L"One-time setup for Backspace",MB_OKCANCEL|MB_ICONINFORMATION)!=IDOK)return ERROR_CANCELLED;
    for(;;){
        DWORD installed=install_runtime();
        if(installed==ERROR_CANCELLED||installed==1602||installed==1223)return ERROR_CANCELLED;
        if(installed==0||installed==3010){
            // A new process avoids keeping a previous hostfxr version loaded.
            wchar_t exe[32768];if(!module_path(exe))return 4;
            STARTUPINFOW startup={sizeof(startup)};PROCESS_INFORMATION child={0};
            size_t length=wcslen(GetCommandLineW())+40;
            wchar_t* command=calloc(length,sizeof(wchar_t));
            if(command)swprintf(command,length,L"%ls --runtime-setup-complete",GetCommandLineW());
            BOOL started=command&&CreateProcessW(exe,command,NULL,NULL,FALSE,0,NULL,NULL,&startup,&child);free(command);
            if(!started)return 4;CloseHandle(child.hThread);CloseHandle(child.hProcess);return 0;
        }
        wchar_t error[500];swprintf(error,COUNT(error),L"Microsoft .NET could not be installed (error %lu).\n\nCheck your connection and try again. Your Backspace files and data were not changed. You can also install .NET 10 (x64) from Microsoft’s website and reopen Backspace.",installed);
        if(MessageBoxW(NULL,error,L"Backspace setup",MB_RETRYCANCEL|MB_ICONWARNING)!=IDRETRY)return 4;
    }
}
