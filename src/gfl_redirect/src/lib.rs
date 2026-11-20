use minhook::MinHook;
use std::ffi::{c_char, c_int, c_void, CStr, CString};
use std::fs::OpenOptions;
use std::io::Write;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::OnceLock;
use windows_sys::Win32::Foundation::*;
use windows_sys::Win32::Networking::WinSock::*;
use windows_sys::Win32::System::LibraryLoader::*;
use windows_sys::Win32::System::SystemServices::*;

static PROC_NAME: OnceLock<String> = OnceLock::new();

fn proc_name() -> &'static str {
    PROC_NAME.get_or_init(|| unsafe {
        let mut buf = [0u8; 260];
        let len = GetModuleFileNameA(std::ptr::null_mut(), buf.as_mut_ptr(), buf.len() as u32) as usize;
        let full = String::from_utf8_lossy(&buf[..len]);
        full.rsplit('\\').next().unwrap_or("unknown").to_string()
    })
}

fn log(msg: &str) {
    if let Ok(mut f) = OpenOptions::new().create(true).append(true).open("gfl_redirect.log") {
        let _ = writeln!(f, "[{}] {}", proc_name(), msg);
    }
}

// System version.dll loader
static SYSTEM_VERSION: AtomicUsize = AtomicUsize::new(0);

fn get_system_version() -> HMODULE {
    let mut h = SYSTEM_VERSION.load(Ordering::Relaxed);
    if h == 0 {
        let sys_path = CString::new("C:\\Windows\\System32\\version.dll").unwrap();
        h = unsafe { LoadLibraryA(sys_path.as_ptr() as _) } as usize;
        SYSTEM_VERSION.store(h, Ordering::Relaxed);
    }
    h as HMODULE
}

fn get_proc(name: &'static str) -> FARPROC {
    let h = get_system_version();
    if h.is_null() {
        return None;
    }
    let c_name = CString::new(name).unwrap();
    unsafe { GetProcAddress(h, c_name.as_ptr() as _) }
}

macro_rules! forward {
    ($fn_name:ident ($($arg:ident : $arg_ty:ty),*) -> $ret:ty) => {
        #[no_mangle]
        pub unsafe extern "system" fn $fn_name($($arg : $arg_ty),*) -> $ret {
            type FnType = unsafe extern "system" fn($($arg_ty),*) -> $ret;
            static PROC: OnceLock<usize> = OnceLock::new();
            let addr = *PROC.get_or_init(|| {
                get_proc(stringify!($fn_name)).map(|p| p as usize).unwrap_or(0)
            });
            if addr != 0 {
                let f: FnType = std::mem::transmute(addr);
                f($($arg),*)
            } else {
                std::mem::zeroed()
            }
        }
    };
}

forward!(GetFileVersionInfoA(ptszfilename: *const c_char, dwdata: u32, dwlen: u32, lpdata: *mut c_void) -> BOOL);
forward!(GetFileVersionInfoByHandle(dwflags: u32, hfile: HANDLE, lpdata: *mut c_void, dwlen: u32) -> BOOL);
forward!(GetFileVersionInfoExA(dwflags: u32, lpwstrfilename: *const c_char, dwhandle: u32, dwlen: u32, lpdata: *mut c_void) -> BOOL);
forward!(GetFileVersionInfoExW(dwflags: u32, lpwstrfilename: *const u16, dwhandle: u32, dwlen: u32, lpdata: *mut c_void) -> BOOL);
forward!(GetFileVersionInfoSizeA(ptszfilename: *const c_char, lpdwdata: *mut u32) -> u32);
forward!(GetFileVersionInfoSizeExA(dwflags: u32, lpwstrfilename: *const c_char, lpdwdata: *mut u32) -> u32);
forward!(GetFileVersionInfoSizeExW(dwflags: u32, lpwstrfilename: *const u16, lpdwdata: *mut u32) -> u32);
forward!(GetFileVersionInfoSizeW(ptszfilename: *const u16, lpdwdata: *mut u32) -> u32);
forward!(GetFileVersionInfoW(ptszfilename: *const u16, dwdata: u32, dwlen: u32, lpdata: *mut c_void) -> BOOL);
forward!(VerFindFileA(uflags: u32, szfilename: *const c_char, szwindir: *const c_char, szappdir: *const c_char, szcurdir: *mut c_char, pulcurdirler: *mut u32, szdestdir: *mut c_char, puldestdirlen: *mut u32) -> u32);
forward!(VerFindFileW(uflags: u32, szfilename: *const u16, szwindir: *const u16, szappdir: *const u16, szcurdir: *mut u16, pulcurdirler: *mut u32, szdestdir: *mut u16, puldestdirlen: *mut u32) -> u32);
forward!(VerInstallFileA(uflags: u32, szsrcfilename: *const c_char, szdestfilename: *const c_char, szsrcdir: *const c_char, szdestdir: *const c_char, szcurdir: *const c_char, sztmpfile: *mut c_char, putmpfilelen: *mut u32) -> u32);
forward!(VerInstallFileW(uflags: u32, szsrcfilename: *const u16, szdestfilename: *const u16, szsrcdir: *const u16, szdestdir: *const u16, szcurdir: *const u16, sztmpfile: *mut u16, putmpfilelen: *mut u32) -> u32);
forward!(VerLanguageNameA(wlang: u32, szlang: *mut c_char, cchlang: u32) -> u32);
forward!(VerLanguageNameW(wlang: u32, szlang: *mut u16, cchlang: u32) -> u32);
forward!(VerQueryValueA(pblock: *const c_void, lpsubblock: *const c_char, lplpbuffer: *mut *mut c_void, pulen: *mut u32) -> BOOL);
forward!(VerQueryValueW(pblock: *const c_void, lpsubblock: *const u16, lplpbuffer: *mut *mut c_void, pulen: *mut u32) -> BOOL);

// Hook definitions
type FnGetAddrInfoA = unsafe extern "system" fn(
    p_nodename: *const c_char,
    p_servicename: *const c_char,
    p_hints: *const ADDRINFOA,
    pp_result: *mut *mut ADDRINFOA,
) -> c_int;

type FnGetAddrInfoW = unsafe extern "system" fn(
    p_nodename: *const u16,
    p_servicename: *const u16,
    p_hints: *const ADDRINFOW,
    pp_result: *mut *mut ADDRINFOW,
) -> c_int;

type FnConnect = unsafe extern "system" fn(
    s: SOCKET,
    name: *const SOCKADDR,
    namelen: c_int,
) -> c_int;

type FnVerifyCertPolicy = unsafe extern "system" fn(
    psz_policy_oid: usize,
    p_chain_context: *const u8,
    p_policy_para: *const u8,
    p_policy_status: *mut u8,
) -> BOOL;

static ORIG_GETADDRINFOA: AtomicUsize = AtomicUsize::new(0);
static ORIG_GETADDRINFOW: AtomicUsize = AtomicUsize::new(0);
static ORIG_CONNECT: AtomicUsize = AtomicUsize::new(0);
static ORIG_VERIFY_CERT: AtomicUsize = AtomicUsize::new(0);

fn should_redirect_host(lower: &str) -> bool {
    lower.contains("sunborngame.com") || lower.contains("girlfrontline") || lower.contains("micateam")
}

unsafe extern "system" fn hook_getaddrinfo(
    p_nodename: *const c_char,
    p_servicename: *const c_char,
    p_hints: *const ADDRINFOA,
    pp_result: *mut *mut ADDRINFOA,
) -> c_int {
    let orig: FnGetAddrInfoA = std::mem::transmute(ORIG_GETADDRINFOA.load(Ordering::Relaxed));
    if !p_nodename.is_null() {
        if let Ok(name) = CStr::from_ptr(p_nodename).to_str() {
            let lower = name.to_ascii_lowercase();
            if should_redirect_host(&lower) {
                log(&format!("getaddrinfoA redirected: {} -> 127.0.0.1", name));
                let local = CString::new("127.0.0.1").unwrap();
                return orig(local.as_ptr(), p_servicename, p_hints, pp_result);
            }
        }
    }
    orig(p_nodename, p_servicename, p_hints, pp_result)
}

unsafe extern "system" fn hook_getaddrinfow(
    p_nodename: *const u16,
    p_servicename: *const u16,
    p_hints: *const ADDRINFOW,
    pp_result: *mut *mut ADDRINFOW,
) -> c_int {
    let orig: FnGetAddrInfoW = std::mem::transmute(ORIG_GETADDRINFOW.load(Ordering::Relaxed));
    if !p_nodename.is_null() {
        let mut len = 0;
        while len < 512 && *p_nodename.add(len) != 0 {
            len += 1;
        }
        let slice = std::slice::from_raw_parts(p_nodename, len);
        if let Ok(name) = String::from_utf16(slice) {
            let lower = name.to_ascii_lowercase();
            if should_redirect_host(&lower) {
                log(&format!("getaddrinfoW redirected: {} -> 127.0.0.1", name));
                let local: Vec<u16> = "127.0.0.1\0".encode_utf16().collect();
                return orig(local.as_ptr(), p_servicename, p_hints, pp_result);
            }
        }
    }
    orig(p_nodename, p_servicename, p_hints, pp_result)
}

unsafe extern "system" fn hook_connect(
    s: SOCKET,
    name: *const SOCKADDR,
    namelen: c_int,
) -> c_int {
    let orig: FnConnect = std::mem::transmute(ORIG_CONNECT.load(Ordering::Relaxed));
    if !name.is_null() && namelen >= std::mem::size_of::<SOCKADDR_IN>() as i32 {
        let sin = name as *mut SOCKADDR_IN;
        if (*sin).sin_family == AF_INET {
            let ip = u32::from_be((*sin).sin_addr.S_un.S_addr);
            let port = u16::from_be((*sin).sin_port);
            // 127.0.0.1 is 0x7F000001
            if ip == 0x7F000001 {
                if port == 80 {
                    log("connect: Remapping 127.0.0.1:80 -> 127.0.0.1:5000");
                    (*sin).sin_port = u16::to_be(5000);
                } else if port == 443 {
                    log("connect: Allowing 127.0.0.1:443 (HTTPS)");
                }
            }
        }
    }
    orig(s, name, namelen)
}

unsafe fn clear_chain_errors(ctx: *const u8) {
    if ctx.is_null() {
        return;
    }
    *(ctx.add(4) as *mut u32) = 0;
    let n_chains = *(ctx.add(0xC) as *const u32) as usize;
    let chains = *(ctx.add(0x10) as *const *const *const u8);
    if chains.is_null() {
        return;
    }
    for i in 0..n_chains.min(16) {
        let simple = *chains.add(i);
        if simple.is_null() {
            continue;
        }
        *(simple.add(4) as *mut u32) = 0;
        let n_elems = *(simple.add(0xC) as *const u32) as usize;
        let elems = *(simple.add(0x10) as *const *const u8);
        if elems.is_null() {
            continue;
        }
        for j in 0..n_elems.min(16) {
            let el = *(elems as *const *const u8).add(j);
            if !el.is_null() {
                *(el.add(0x10) as *mut u32) = 0;
            }
        }
    }
}

unsafe extern "system" fn hook_verify_cert_policy(
    psz_policy_oid: usize,
    p_chain_context: *const u8,
    p_policy_para: *const u8,
    p_policy_status: *mut u8,
) -> BOOL {
    let orig: FnVerifyCertPolicy = std::mem::transmute(ORIG_VERIFY_CERT.load(Ordering::Relaxed));
    let ok = orig(psz_policy_oid, p_chain_context, p_policy_para, p_policy_status);
    // CERT_CHAIN_POLICY_SSL = 4
    if psz_policy_oid != 4 || p_chain_context.is_null() || p_policy_para.is_null() || p_policy_status.is_null() {
        return ok;
    }
    let extra = *(p_policy_para.add(8) as *const *const u8);
    if !extra.is_null() {
        let name_ptr = *(extra.add(0x10) as *const *const u16);
        if !name_ptr.is_null() {
            let mut len = 0;
            while len < 512 && *name_ptr.add(len) != 0 {
                len += 1;
            }
            let server_name = String::from_utf16_lossy(std::slice::from_raw_parts(name_ptr, len)).to_ascii_lowercase();
            if should_redirect_host(&server_name) || server_name == "127.0.0.1" || server_name == "localhost" {
                if *(p_policy_status.add(4) as *const u32) != 0 {
                    *(p_policy_status.add(4) as *mut u32) = 0;
                    *(p_policy_status.add(8) as *mut i32) = -1;
                    *(p_policy_status.add(0xC) as *mut i32) = -1;
                    clear_chain_errors(p_chain_context);
                    log(&format!("Accepted local TLS cert for: {}", server_name));
                }
                return 1;
            }
        }
    }
    ok
}

fn install_hooks() {
    log("Installing socket + TLS hooks via MinHook...");
    unsafe {
        let ws2 = CString::new("ws2_32.dll").unwrap();
        let h_ws2 = LoadLibraryA(ws2.as_ptr() as _);
        if !h_ws2.is_null() {
            if let Some(p) = GetProcAddress(h_ws2, CString::new("getaddrinfo").unwrap().as_ptr() as _) {
                if let Ok(orig) = MinHook::create_hook(p as _, hook_getaddrinfo as _) {
                    ORIG_GETADDRINFOA.store(orig as usize, Ordering::Relaxed);
                }
            }
            if let Some(p) = GetProcAddress(h_ws2, CString::new("GetAddrInfoW").unwrap().as_ptr() as _) {
                if let Ok(orig) = MinHook::create_hook(p as _, hook_getaddrinfow as _) {
                    ORIG_GETADDRINFOW.store(orig as usize, Ordering::Relaxed);
                }
            }
            if let Some(p) = GetProcAddress(h_ws2, CString::new("connect").unwrap().as_ptr() as _) {
                if let Ok(orig) = MinHook::create_hook(p as _, hook_connect as _) {
                    ORIG_CONNECT.store(orig as usize, Ordering::Relaxed);
                }
            }
        }

        let crypt32 = CString::new("crypt32.dll").unwrap();
        let h_crypt32 = LoadLibraryA(crypt32.as_ptr() as _);
        if !h_crypt32.is_null() {
            if let Some(p) = GetProcAddress(h_crypt32, CString::new("CertVerifyCertificateChainPolicy").unwrap().as_ptr() as _) {
                if let Ok(orig) = MinHook::create_hook(p as _, hook_verify_cert_policy as _) {
                    ORIG_VERIFY_CERT.store(orig as usize, Ordering::Relaxed);
                    log("Hooked CertVerifyCertificateChainPolicy successfully");
                }
            }
        }

        if MinHook::enable_all_hooks().is_ok() {
            log("All MinHook hooks enabled successfully!");
        }
    }
}

#[no_mangle]
pub unsafe extern "system" fn DllMain(_hinst: HINSTANCE, reason: u32, _reserved: *mut c_void) -> BOOL {
    if reason == DLL_PROCESS_ATTACH {
        DisableThreadLibraryCalls(_hinst);
        log("=== version.dll loaded ===");
        install_hooks();
    }
    1
}
