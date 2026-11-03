/*
 * DIAGNOSTICS: batch 609. The Linux side of exehost.c (a static i386 ELF, musl): the few Win32 calls the helper makes, done the
 * Linux way, so exehost.c itself stays one file for both systems. Included instead of <windows.h> when _WIN32 is not defined.
 *
 * What the client's routines need from the process, settled on the EXE (see the batch 609 record):
 * - the image at its ImageBase 0x400000: the helper links at a fixed base (build.sh), so the range is free and nothing is relocated;
 * - FS: the routines push and pop C++ SEH frames at fs:[0] (79 functions of the closure; the one fs:[0x18] read is in the CRT start-up,
 *   which never runs), so FS points at a zeroed TEB-like page whose fs:[0] is the chain's end (main below, set_thread_area);
 * - KERNEL32: GetTickCount (the profiler scope of the grid path builder) and QueryPerformanceCounter/Frequency (its statistics window) are
 *   the only ones on the routines' normal paths; the rest of the closure's KERNEL32 calls sit on the assert and crash-report paths and
 *   stay trapping stubs, as every unsupported import does;
 * - the client code calls with a 4-byte aligned stack (MSVC), while i386 Linux code may assume 16: build.sh compiles this file with
 *   -mstackrealign, and the C library functions the client calls go through the wrappers below, which realign before calling in;
 * - the x87 at 24-bit precision as on Windows (_controlfp_s below) and MXCSR at the Windows and Linux default 0x1f80.
 * The diagnostics requests that need Windows exception machinery (4 and 9 single-step, 8 guard pages) stop with a message here.
 */
#define _GNU_SOURCE
#include <cpuid.h>
#include <errno.h>
#include <fcntl.h>
#include <float.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <time.h>
#include <ucontext.h>
#include <unistd.h>
#include <wchar.h>

#if !defined(__i386__)
#error "exehost runs 32-bit client code: build it for i386 (x86-linux-musl)"
#endif

typedef unsigned long DWORD;
typedef long LONG;
typedef int BOOL;
typedef void *HANDLE;
typedef union
{
    struct { DWORD LowPart; LONG HighPart; } u;
    long long QuadPart;
} LARGE_INTEGER;
#define WINAPI __attribute__((stdcall))
#define CALLBACK __attribute__((stdcall))

static void Fail(const char *message, const char *detail);

/* ------------------------------------------------------------------ PE layout (the fields exehost.c reads) */

typedef struct { uint16_t e_magic, e_res[29]; int32_t e_lfanew; } IMAGE_DOS_HEADER;
typedef struct { uint16_t Machine, NumberOfSections; uint32_t TimeDateStamp, PointerToSymbolTable, NumberOfSymbols;
    uint16_t SizeOfOptionalHeader, Characteristics; } IMAGE_FILE_HEADER;
typedef struct { uint32_t VirtualAddress, Size; } IMAGE_DATA_DIRECTORY;
typedef struct
{
    uint16_t Magic;
    uint8_t before[26];
    uint32_t ImageBase;
    uint8_t between[24];
    uint32_t SizeOfImage, SizeOfHeaders;
    uint8_t after[32];
    IMAGE_DATA_DIRECTORY DataDirectory[16];
} IMAGE_OPTIONAL_HEADER32;
typedef struct { uint32_t Signature; IMAGE_FILE_HEADER FileHeader; IMAGE_OPTIONAL_HEADER32 OptionalHeader; } IMAGE_NT_HEADERS32;
typedef struct
{
    uint8_t Name[8];
    union { uint32_t PhysicalAddress, VirtualSize; } Misc;
    uint32_t VirtualAddress, SizeOfRawData, PointerToRawData, PointerToRelocations, PointerToLinenumbers;
    uint16_t NumberOfRelocations, NumberOfLinenumbers;
    uint32_t Characteristics;
} IMAGE_SECTION_HEADER;
typedef struct { uint32_t OriginalFirstThunk, TimeDateStamp, ForwarderChain, Name, FirstThunk; } IMAGE_IMPORT_DESCRIPTOR;
typedef struct { uint32_t VirtualAddress, SizeOfBlock; } IMAGE_BASE_RELOCATION;
_Static_assert(sizeof(IMAGE_DOS_HEADER) == 64 && sizeof(IMAGE_NT_HEADERS32) == 248 && sizeof(IMAGE_SECTION_HEADER) == 40 &&
    sizeof(IMAGE_IMPORT_DESCRIPTOR) == 20, "PE layout");
#define IMAGE_FIRST_SECTION(nt) ((IMAGE_SECTION_HEADER *)((uint8_t *)&(nt)->OptionalHeader + (nt)->FileHeader.SizeOfOptionalHeader))
#define IMAGE_DIRECTORY_ENTRY_IMPORT 1
#define IMAGE_DIRECTORY_ENTRY_BASERELOC 5
#define IMAGE_ORDINAL_FLAG32 0x80000000u
#define IMAGE_REL_BASED_ABSOLUTE 0
#define IMAGE_REL_BASED_HIGHLOW 3

/* ------------------------------------------------------------------ C library entry points the client calls */

/* The client calls these with the stack 4-byte aligned. Each wrapper realigns (-mstackrealign, no sibling calls) before calling in. */
static void *ShimMalloc(size_t n) { return malloc(n); }
static void ShimFree(void *p) { free(p); }
static void *ShimCalloc(size_t n, size_t size) { return calloc(n, size); }
static void *ShimRealloc(void *p, size_t n) { return realloc(p, n); }
static void *ShimMemcpy(void *d, const void *s, size_t n) { return memcpy(d, s, n); }
static void *ShimMemset(void *d, int c, size_t n) { return memset(d, c, n); }
static void *ShimMemmove(void *d, const void *s, size_t n) { return memmove(d, s, n); }
/* The CRT's checked copies: on a bad argument the CRT's invalid-parameter handler ends the process, so does this. */
static int memcpy_s(void *d, size_t size, const void *s, size_t n)
{
    if (!n) return 0;
    if (!d || !s || size < n) Fail("memcpy_s: invalid parameter", NULL);
    memcpy(d, s, n);
    return 0;
}
static int memmove_s(void *d, size_t size, const void *s, size_t n)
{
    if (!n) return 0;
    if (!d || !s || size < n) Fail("memmove_s: invalid parameter", NULL);
    memmove(d, s, n);
    return 0;
}
#define malloc ShimMalloc
#define free ShimFree
#define calloc ShimCalloc
#define realloc ShimRealloc
#define memcpy ShimMemcpy
#define memset ShimMemset
#define memmove ShimMemmove
#define _stricmp strcasecmp

/* ------------------------------------------------------------------ process, memory, time */

#define ExitProcess(code) _exit((int)(code))

#define MEM_COMMIT 0x1000
#define MEM_RESERVE 0x2000
#define PAGE_READWRITE 0x04
#define PAGE_EXECUTE_READWRITE 0x40
#define PAGE_GUARD 0x100
#ifndef MAP_FIXED_NOREPLACE
#define MAP_FIXED_NOREPLACE 0x100000
#endif

/* Committed, zeroed memory; at the given address or not at all, as VirtualAlloc does. */
static void *VirtualAlloc(void *address, size_t size, DWORD type, DWORD protection)
{
    (void)type;
    int prot = PROT_READ | PROT_WRITE | (protection == PAGE_EXECUTE_READWRITE ? PROT_EXEC : 0);
    void *p = mmap(address, size, prot, MAP_PRIVATE | MAP_ANONYMOUS | (address ? MAP_FIXED_NOREPLACE : 0), -1, 0);
    if (p == MAP_FAILED) return NULL;
    if (address && p != address) /* a kernel before 4.17 takes MAP_FIXED_NOREPLACE as a hint */
    {
        munmap(p, size);
        return NULL;
    }
    return p;
}

static BOOL VirtualProtect(void *address, size_t size, DWORD protection, DWORD *old)
{
    if (protection & PAGE_GUARD) Fail("the field watch (request 8) runs on Windows only", NULL);
    *old = PAGE_READWRITE;
    return mprotect(address, size, PROT_READ | PROT_WRITE) == 0;
}

static BOOL WINAPI QueryPerformanceCounter(LARGE_INTEGER *counter)
{
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    counter->QuadPart = (long long)t.tv_sec * 1000000000LL + t.tv_nsec;
    return 1;
}

static BOOL WINAPI QueryPerformanceFrequency(LARGE_INTEGER *frequency)
{
    frequency->QuadPart = 1000000000LL;
    return 1;
}

static DWORD WINAPI GetTickCount(void)
{
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (DWORD)((unsigned long long)t.tv_sec * 1000u + (unsigned long long)t.tv_nsec / 1000000u);
}

/* The KERNEL32 imports the routines reach on their normal paths; every other one becomes a trapping stub that names itself. */
static HANDLE GetModuleHandleA(const char *name) { (void)name; return (HANDLE)1; }
static void *GetProcAddress(HANDLE module, const char *name)
{
    (void)module;
    if (!strcmp(name, "GetTickCount")) return (void *)GetTickCount;
    if (!strcmp(name, "QueryPerformanceCounter")) return (void *)QueryPerformanceCounter;
    if (!strcmp(name, "QueryPerformanceFrequency")) return (void *)QueryPerformanceFrequency;
    return NULL;
}

#define PF_XMMI64_INSTRUCTIONS_AVAILABLE 10
static BOOL IsProcessorFeaturePresent(DWORD feature)
{
    unsigned a, b, c, d;
    return feature == PF_XMMI64_INSTRUCTIONS_AVAILABLE && __get_cpuid(1, &a, &b, &c, &d) && (d >> 26 & 1);
}

/* The MSVC control-word call, for its one use: the x87 precision field (bits 8-9; 24-bit is 00), the other bits kept. */
#define _MCW_PC 0x00030000u
#define _PC_64 0x00000000u
#define _PC_53 0x00010000u
#define _PC_24 0x00020000u
static int _controlfp_s(unsigned int *current, unsigned int value, unsigned int mask)
{
    unsigned short cw;
    __asm__ volatile("fnstcw %0" : "=m"(cw));
    if (mask & _MCW_PC)
    {
        unsigned pc = value & _MCW_PC;
        cw = (unsigned short)((cw & ~0x300u) | (pc == _PC_24 ? 0u : pc == _PC_53 ? 0x200u : 0x300u));
        __asm__ volatile("fldcw %0" : : "m"(cw));
    }
    if (current) *current = value & mask;
    return 0;
}

/* ------------------------------------------------------------------ SHA-256 (FIPS 180-4), for the EXE gate */

#define BCRYPT_SHA256_ALG_HANDLE ((void *)0x41)
static uint32_t Ror(uint32_t x, int n) { return x >> n | x << (32 - n); }
static void Sha256Block(uint32_t h[8], const uint8_t *p)
{
    static const uint32_t k[64] = {
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01,
        0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc,
        0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147,
        0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116, 0x1e376c08,
        0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
        0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2};
    uint32_t w[64], s[8];
    for (int i = 0; i < 16; i++) w[i] = (uint32_t)p[4 * i] << 24 | (uint32_t)p[4 * i + 1] << 16 | (uint32_t)p[4 * i + 2] << 8 | p[4 * i + 3];
    for (int i = 16; i < 64; i++)
        w[i] = w[i - 16] + (Ror(w[i - 15], 7) ^ Ror(w[i - 15], 18) ^ w[i - 15] >> 3) + w[i - 7] +
               (Ror(w[i - 2], 17) ^ Ror(w[i - 2], 19) ^ w[i - 2] >> 10);
    for (int i = 0; i < 8; i++) s[i] = h[i];
    for (int i = 0; i < 64; i++)
    {
        uint32_t t1 = s[7] + (Ror(s[4], 6) ^ Ror(s[4], 11) ^ Ror(s[4], 25)) + ((s[4] & s[5]) ^ (~s[4] & s[6])) + k[i] + w[i];
        uint32_t t2 = (Ror(s[0], 2) ^ Ror(s[0], 13) ^ Ror(s[0], 22)) + ((s[0] & s[1]) ^ (s[0] & s[2]) ^ (s[1] & s[2]));
        memmove(s + 1, s, 7 * sizeof *s);
        s[4] += t1;
        s[0] = t1 + t2;
    }
    for (int i = 0; i < 8; i++) h[i] += s[i];
}

/* BCryptHash's shape, SHA-256 only (exehost.c's one call). */
static long BCryptHash(void *algorithm, void *secret, DWORD secretLength, uint8_t *input, DWORD length, uint8_t *output, DWORD outputLength)
{
    (void)secret, (void)secretLength;
    if (algorithm != BCRYPT_SHA256_ALG_HANDLE || outputLength != 32) return -1;
    uint32_t h[8] = {0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19};
    uint8_t last[128] = {0};
    DWORD whole = length & ~63u, rest = length - whole;
    for (DWORD o = 0; o < whole; o += 64) Sha256Block(h, input + o);
    memcpy(last, input + whole, rest);
    last[rest] = 0x80;
    DWORD tail = rest < 56 ? 64 : 128;
    unsigned long long bits = (unsigned long long)length * 8;
    for (int i = 0; i < 8; i++) last[tail - 1 - i] = (uint8_t)(bits >> (8 * i));
    for (DWORD o = 0; o < tail; o += 64) Sha256Block(h, last + o);
    for (int i = 0; i < 32; i++) output[i] = (uint8_t)(h[i / 4] >> (24 - 8 * (i % 4)));
    return 0;
}

/* ------------------------------------------------------------------ files and pipes (a HANDLE is a file descriptor) */

#define GENERIC_READ 0x80000000u
#define FILE_SHARE_READ 1
#define OPEN_EXISTING 3
#define INVALID_HANDLE_VALUE ((HANDLE)(intptr_t)-1)
#define STD_INPUT_HANDLE ((DWORD)-10)
#define STD_OUTPUT_HANDLE ((DWORD)-11)

static HANDLE CreateFileW(const wchar_t *path, DWORD access, DWORD share, void *security, DWORD disposition, DWORD flags, HANDLE templ)
{
    (void)access, (void)share, (void)security, (void)disposition, (void)flags, (void)templ;
    char name[4096];
    if (wcstombs(name, path, sizeof name) >= sizeof name) return INVALID_HANDLE_VALUE;
    return (HANDLE)(intptr_t)open(name, O_RDONLY | O_CLOEXEC);
}

static DWORD GetFileSize(HANDLE file, DWORD *high)
{
    (void)high;
    struct stat s;
    return fstat((int)(intptr_t)file, &s) == 0 ? (DWORD)s.st_size : 0;
}

static BOOL ReadFile(HANDLE file, void *buffer, DWORD n, DWORD *got, void *overlapped)
{
    (void)overlapped;
    ssize_t r;
    do r = read((int)(intptr_t)file, buffer, n);
    while (r < 0 && errno == EINTR);
    *got = r > 0 ? (DWORD)r : 0;
    return r >= 0;
}

static BOOL WriteFile(HANDLE file, const void *buffer, DWORD n, DWORD *wrote, void *overlapped)
{
    (void)overlapped;
    DWORD done = 0;
    while (done < n)
    {
        ssize_t r = write((int)(intptr_t)file, (const uint8_t *)buffer + done, n - done);
        if (r < 0 && errno == EINTR) continue;
        if (r <= 0) break;
        done += (DWORD)r;
    }
    *wrote = done;
    return done == n;
}

static BOOL CloseHandle(HANDLE h) { return close((int)(intptr_t)h) == 0; }
static HANDLE GetStdHandle(DWORD which) { return (HANDLE)(intptr_t)(which == STD_INPUT_HANDLE ? 0 : 1); }

/* ------------------------------------------------------------------ faults: a crash in the client code is reported as on Windows */

typedef struct { DWORD ExceptionCode; uintptr_t ExceptionInformation[2]; } EXCEPTION_RECORD;
typedef struct { DWORD Eip, Esp, EFlags, Dr0, Dr6, Dr7; } CONTEXT;
typedef struct { EXCEPTION_RECORD *ExceptionRecord; CONTEXT *ContextRecord; } EXCEPTION_POINTERS;
#define STATUS_GUARD_PAGE_VIOLATION 0x80000001u
#define EXCEPTION_SINGLE_STEP 0x80000004u
#define EXCEPTION_CONTINUE_EXECUTION (-1)
#define EXCEPTION_CONTINUE_SEARCH 0
#define EXCEPTION_EXECUTE_HANDLER 1

typedef LONG(CALLBACK *VectoredHandler)(EXCEPTION_POINTERS *);
static VectoredHandler vectoredHandler;

static void OnSignal(int signal, siginfo_t *info, void *context)
{
    int breakpoint = signal == SIGTRAP && info->si_code == SI_KERNEL; /* int3, the client's hard-assert stop */
    if (signal == SIGTRAP && !breakpoint)
    {
        fputs("exehost: single-stepping (requests 4 and 9) runs on Windows only\n", stderr);
        _exit(3);
    }
    greg_t *r = ((ucontext_t *)context)->uc_mcontext.gregs;
    EXCEPTION_RECORD record = {breakpoint ? 0x80000003u : signal == SIGILL ? 0xc000001du : signal == SIGFPE ? 0xc0000094u : 0xc0000005u,
        {(r[REG_ERR] & 2) != 0, (uintptr_t)info->si_addr}};
    /* Windows reports a breakpoint at the int3 itself, Linux after it. */
    CONTEXT c = {(DWORD)r[REG_EIP] - (DWORD)breakpoint, (DWORD)r[REG_ESP], (DWORD)r[REG_EFL], 0, 0, 0};
    EXCEPTION_POINTERS e = {&record, &c};
    vectoredHandler(&e); /* reports the fault and exits */
    _exit(4);
}

static void *AddVectoredExceptionHandler(unsigned long first, VectoredHandler handler)
{
    (void)first;
    vectoredHandler = handler;
    struct sigaction a;
    memset(&a, 0, sizeof a);
    a.sa_sigaction = OnSignal;
    a.sa_flags = SA_SIGINFO;
    sigemptyset(&a.sa_mask);
    int signals[] = {SIGSEGV, SIGBUS, SIGILL, SIGFPE, SIGTRAP};
    for (unsigned i = 0; i < sizeof signals / sizeof *signals; i++) sigaction(signals[i], &a, NULL);
    signal(SIGPIPE, SIG_IGN); /* a closed pipe ends the helper through the next read, as on Windows */
    return (void *)handler;
}

#define SetUnhandledExceptionFilter(filter) ((void)(filter))

/* ------------------------------------------------------------------ the entry point: arguments, FS, floating point */

/* set_thread_area's descriptor (<asm/ldt.h>). */
struct user_desc
{
    unsigned int entry_number, base_addr, limit;
    unsigned int seg_32bit : 1, contents : 2, read_exec_only : 1, limit_in_pages : 1, seg_not_present : 1, useable : 1;
};

int wmain(int argc, wchar_t **argv);

int main(int argc, char **argv)
{
    /* FS: a zeroed page for the client's SEH frames; fs:[0] = -1 ends the chain, fs:[0x18] points at itself as a TEB does. musl keeps its
       own thread pointer in GS. */
    uint32_t *teb = VirtualAlloc(NULL, 4096, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    if (!teb) Fail("out of memory", NULL);
    teb[0] = 0xffffffffu;
    teb[0x18 / 4] = (uint32_t)(uintptr_t)teb;
    struct user_desc d = {(unsigned)-1, (unsigned)(uintptr_t)teb, 0xfff, 1, 0, 0, 0, 0, 1};
    if (syscall(SYS_set_thread_area, &d) != 0) Fail("set_thread_area failed (is IA32 emulation on?)", NULL);
    unsigned short selector = (unsigned short)(d.entry_number << 3 | 3);
    __asm__ volatile("movw %0, %%fs" : : "r"(selector));
    /* MXCSR as Windows starts a thread: every exception masked, round to nearest, no flush-to-zero or denormals-are-zero. */
    unsigned int mxcsr = 0x1f80;
    __asm__ volatile("ldmxcsr %0" : : "m"(mxcsr));
    wchar_t **wide = calloc((size_t)argc + 1, sizeof *wide);
    for (int i = 0; wide && i < argc; i++)
    {
        size_t n = strlen(argv[i]) + 1;
        wide[i] = calloc(n, sizeof **wide);
        if (!wide[i] || mbstowcs(wide[i], argv[i], n) == (size_t)-1) Fail("bad argument", argv[i]);
    }
    if (!wide) Fail("out of memory", NULL);
    return wmain(argc, wide);
}
