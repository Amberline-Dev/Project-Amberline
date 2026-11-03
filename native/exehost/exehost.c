/*
 * Runs the stock 1.0.0.126 client's own movement code from the player's League of Legends.exe: production since batch 608 (op 11, the
 * persistent mirrors); batches 596-606 added the grid path search, the planner and the update as DIAGNOSTICS against the C# port.
 *
 * The EXE file is only read. Its bytes are copied into this process's memory (the sections at their virtual addresses,
 * base relocations applied when the preferred base is taken, the import table filled with this file's own functions),
 * and two of its routines are called directly. Its entry point and C runtime start-up never run, nothing is written back
 * to disk, and no Riot bytes live in this source: it holds only addresses and the file's SHA-256.
 *
 * Usage: exehost <League of Legends.exe> <AIPath.aimesh_ngrid>, then binary requests on stdin (see Serve below).
 * Windows x86 (build.ps1) and Linux i386 (build.sh, static musl): on Linux exehost_linux.h stands in for the Win32 calls (batch 609).
 */
#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <bcrypt.h>
#else
#include "exehost_linux.h"
#endif
#include <float.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* The one executable the addresses below belong to (League of Legends.exe, 1.0.0.126, 8,363,008 bytes). */
static const uint8_t ExeSha256[32] = {
    0xad, 0x8f, 0xfd, 0xfa, 0x96, 0x20, 0x89, 0x8f, 0x4b, 0xfe, 0x85, 0x65, 0xa5, 0x6a, 0xa0, 0xc2,
    0x26, 0x3c, 0x10, 0x7d, 0xe1, 0x52, 0xf3, 0x1b, 0x7b, 0x00, 0x7c, 0xae, 0x3f, 0x63, 0x15, 0x39};

#define PREFERRED_BASE 0x400000u
#define FN_BUILD_NAV_GRID_PATH 0x750890u /* the grid path builder: ESI = grid, 11 stack arguments, ret 0x2c */
#define FN_SET_MIN_MAX_POS 0x74c2c0u     /* the lattice setter: cdecl, min xyz and max xyz (the path lattice) */
#define VAR_SSE2_AVAILABLE 0x2ca1690u    /* the C runtime start-up's SSE2 probe; the float-to-int helper reads it */
#define VAR_CLOCK 0xb43214u              /* the game clock object; the path builder stamps the path with virtual slot 4 */
#define VAR_CONSTRUCTOR_COPY 0xb0c304u   /* the value the grid constructor copies to the G_HW field */
#define VAR_LOAD_MAP_OWNER 0x2bf365cu    /* the map loader stores this address at the G_OWNER field */

/* grid fields (set by the grid constructor and the binary loader). */
#define GRID_SIZE 0x317f90u
#define G_MIN 0x3
#define G_MAX 0xf
#define G_CELLS 0x1c
#define G_HEIGHTS 0x20
#define G_HEIGHT_HEADER 0x24
#define G_PARTIAL 0x36
#define G_REGIONS 0xf4
#define G_REGIONS_SIZE 0x317e50u
#define G_HINT 0x317f48
#define G_HINT_Z 0x317f4a
#define G_HW 0x317f4c
#define G_SELF 0x317f50
#define G_WIDTH 0x317f74
#define G_HEIGHT 0x317f78
#define G_CELL_SIZE 0x317f7c
#define G_HALF 0x317f80
#define G_OWNER 0x317f84
#define CELL_SIZE 0x38
#define CELL_FLAGS 0x32

static uint8_t *image;
static uint32_t imageSize;
static uint8_t *grid;
static uint8_t *cells;
static int32_t width, height;
static const char **importNames;

static uintptr_t Va(uint32_t address) { return (uintptr_t)image + (address - PREFERRED_BASE); }

static void Fail(const char *message, const char *detail)
{
    fprintf(stderr, "exehost: %s%s%s\n", message, detail ? ": " : "", detail ? detail : "");
    fflush(stderr);
    ExitProcess(2);
}

static uint8_t *ReadAll(const wchar_t *path, DWORD *size)
{
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (file == INVALID_HANDLE_VALUE) Fail("cannot open", NULL);
    *size = GetFileSize(file, NULL);
    uint8_t *data = malloc(*size);
    DWORD read = 0;
    if (!data || !ReadFile(file, data, *size, &read, NULL) || read != *size) Fail("cannot read", NULL);
    CloseHandle(file);
    return data;
}

/* ------------------------------------------------------------------ imports */

static void __stdcall Trap(int index)
{
    fprintf(stderr, "exehost: the client code called the unsupported import %s\n", importNames[index]);
    fflush(stderr);
    ExitProcess(3);
}

static void *__cdecl OperatorNew(size_t size)
{
    void *p = malloc(size ? size : 1);
    if (!p) Fail("out of memory", NULL);
    return p;
}

static void __cdecl OperatorDelete(void *p) { free(p); }

/* MSVCR80 _CIsqrt: argument and result in ST(0). */
static __declspec(naked) void CiSqrt(void)
{
    __asm {
        fsqrt
        ret
    }
}

/* The CRT's atexit machinery, which the client's function-local statics call on first use: nothing is kept (the helper never
   runs the client's exit-time destructors), the pointers are not obfuscated. */
static void *__cdecl EncodePointer_(void *p) { return p; }
static void __cdecl Lock_(int n) { (void)n; }
static void *__cdecl DllOnExit(void *f, void *begin, void *end) { (void)begin, (void)end; return f; }
static void *__cdecl OnExit_(void *f) { return f; }

static void *Resolve(const char *dll, const char *name)
{
    if (_stricmp(dll, "KERNEL32.dll") == 0) return (void *)GetProcAddress(GetModuleHandleA("kernel32.dll"), name);
    if (_stricmp(dll, "MSVCR80.dll") != 0) return NULL;
    if (!strcmp(name, "??2@YAPAXI@Z") || !strcmp(name, "??_U@YAPAXI@Z")) return (void *)OperatorNew;
    if (!strcmp(name, "??3@YAXPAX@Z") || !strcmp(name, "??_V@YAXPAX@Z")) return (void *)OperatorDelete;
    if (!strcmp(name, "malloc")) return (void *)malloc;
    if (!strcmp(name, "free")) return (void *)free;
    if (!strcmp(name, "calloc")) return (void *)calloc;
    if (!strcmp(name, "realloc")) return (void *)realloc;
    if (!strcmp(name, "memcpy")) return (void *)memcpy;
    if (!strcmp(name, "memset")) return (void *)memset;
    if (!strcmp(name, "memmove")) return (void *)memmove;
    if (!strcmp(name, "memmove_s")) return (void *)memmove_s;
    if (!strcmp(name, "memcpy_s")) return (void *)memcpy_s;
    if (!strcmp(name, "_CIsqrt")) return (void *)CiSqrt;
    if (!strcmp(name, "_encode_pointer") || !strcmp(name, "_decode_pointer")) return (void *)EncodePointer_;
    if (!strcmp(name, "_lock") || !strcmp(name, "_unlock")) return (void *)Lock_;
    if (!strcmp(name, "__dllonexit")) return (void *)DllOnExit;
    if (!strcmp(name, "_onexit")) return (void *)OnExit_;
    return NULL;
}

/* Every import this file does not provide becomes a stub that names it and exits: push index; call Trap. */
static void ResolveImports(IMAGE_NT_HEADERS32 *nt)
{
    IMAGE_DATA_DIRECTORY dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    int total = 0;
    for (IMAGE_IMPORT_DESCRIPTOR *d = (void *)(image + dir.VirtualAddress); d->Name; d++)
        for (uint32_t *t = (void *)(image + (d->OriginalFirstThunk ? d->OriginalFirstThunk : d->FirstThunk)); *t; t++) total++;
    importNames = calloc(total, sizeof *importNames);
    uint8_t *stubs = VirtualAlloc(NULL, total * 10, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE);
    if (!importNames || !stubs) Fail("out of memory", NULL);
    int index = 0;
    for (IMAGE_IMPORT_DESCRIPTOR *d = (void *)(image + dir.VirtualAddress); d->Name; d++)
    {
        const char *dll = (const char *)(image + d->Name);
        uint32_t *names = (void *)(image + (d->OriginalFirstThunk ? d->OriginalFirstThunk : d->FirstThunk));
        uint32_t *slots = (void *)(image + d->FirstThunk);
        for (; *names; names++, slots++, index++)
        {
            const char *name = (*names & IMAGE_ORDINAL_FLAG32) ? "(ordinal)" : (const char *)(image + *names + 2);
            importNames[index] = name;
            void *target = (*names & IMAGE_ORDINAL_FLAG32) ? NULL : Resolve(dll, name);
            if (!target)
            {
                uint8_t *s = stubs + index * 10;
                s[0] = 0x68;
                memcpy(s + 1, &index, 4);
                s[5] = 0xe8;
                int32_t rel = (int32_t)((uintptr_t)Trap - (uintptr_t)(s + 10));
                memcpy(s + 6, &rel, 4);
                target = s;
            }
            *slots = (uint32_t)(uintptr_t)target;
        }
    }
}

/* ------------------------------------------------------------------ image */

static int MapImage(const wchar_t *path)
{
    DWORD size;
    uint8_t *file = ReadAll(path, &size);
    uint8_t hash[32];
    if (BCryptHash(BCRYPT_SHA256_ALG_HANDLE, NULL, 0, file, size, hash, sizeof hash) != 0) Fail("SHA-256 failed", NULL);
    if (memcmp(hash, ExeSha256, sizeof hash) != 0) Fail("not the 1.0.0.126 League of Legends.exe (SHA-256 differs)", NULL);
    IMAGE_DOS_HEADER *dos = (void *)file;
    IMAGE_NT_HEADERS32 *nt = (void *)(file + dos->e_lfanew);
    imageSize = nt->OptionalHeader.SizeOfImage;
    image = VirtualAlloc((void *)(uintptr_t)nt->OptionalHeader.ImageBase, imageSize, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE);
    int relocated = image == NULL;
    if (relocated) image = VirtualAlloc(NULL, imageSize, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE);
    if (!image) Fail("cannot reserve the image", NULL);
    memcpy(image, file, nt->OptionalHeader.SizeOfHeaders);
    IMAGE_SECTION_HEADER *s = IMAGE_FIRST_SECTION(nt);
    for (int i = 0; i < nt->FileHeader.NumberOfSections; i++, s++)
    {
        uint32_t n = s->SizeOfRawData;
        if (s->Misc.VirtualSize && s->Misc.VirtualSize < n) n = s->Misc.VirtualSize;
        memcpy(image + s->VirtualAddress, file + s->PointerToRawData, n);
    }
    nt = (void *)(image + dos->e_lfanew);
    int32_t delta = (int32_t)((uintptr_t)image - nt->OptionalHeader.ImageBase);
    IMAGE_DATA_DIRECTORY rd = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_BASERELOC];
    if (delta)
        for (uint32_t o = 0; o < rd.Size;)
        {
            IMAGE_BASE_RELOCATION *b = (void *)(image + rd.VirtualAddress + o);
            uint16_t *e = (uint16_t *)(b + 1);
            for (uint32_t i = 0; i < (b->SizeOfBlock - sizeof *b) / 2; i++)
            {
                int type = e[i] >> 12;
                if (type == IMAGE_REL_BASED_HIGHLOW) *(uint32_t *)(image + b->VirtualAddress + (e[i] & 0xfff)) += delta;
                else if (type != IMAGE_REL_BASED_ABSOLUTE) Fail("unexpected relocation type", NULL);
            }
            o += b->SizeOfBlock;
        }
    ResolveImports(nt);
    free(file);
    return relocated;
}

/* ------------------------------------------------------------------ client state */

static float clockSeconds;
static __declspec(naked) void ClockNow(void) /* thiscall, no arguments, result in ST(0) */
{
    __asm {
        fld dword ptr [clockSeconds]
        ret
    }
}
static void *clockVtable[8];
static void *clockObject[1] = {clockVtable};

typedef void(__cdecl *SetMinMaxPos)(float, float, float, float, float, float);

static void LoadGrid(const wchar_t *path)
{
    DWORD size;
    uint8_t *f = ReadAll(path, &size);
    uint32_t o = 1;
    if (f[0] != 2) o += 2; /* version 3 carries a minor version (NavigationGrid.Load reads it the same way) */
    grid = calloc(1, GRID_SIZE);
    if (!grid) Fail("out of memory", NULL);
    /* the grid constructor leaves these fields; everything else starts at zero. */
    *(int16_t *)(grid + G_HINT) = 0x7fff;
    *(int16_t *)(grid + G_HINT_Z) = 0x7fff;
    *(uint32_t *)(grid + G_HW) = *(uint32_t *)Va(VAR_CONSTRUCTOR_COPY);
    *(uint8_t **)(grid + G_SELF) = grid;
    /* the grid's binary loader: the file's fields go into the object as they are. */
    memcpy(grid + G_MIN, f + o, 12), o += 12;
    memcpy(grid + G_MAX, f + o, 12), o += 12;
    float cs;
    memcpy(&cs, f + o, 4), o += 4;
    *(float *)(grid + G_CELL_SIZE) = cs;
    *(float *)(grid + G_HALF) = cs * 0.5f;
    memcpy(&width, f + o, 4), o += 4;
    memcpy(&height, f + o, 4), o += 4;
    *(int32_t *)(grid + G_WIDTH) = width;
    *(int32_t *)(grid + G_HEIGHT) = height;
    uint32_t cellBytes = (uint32_t)width * (uint32_t)height * CELL_SIZE;
    cells = OperatorNew(cellBytes);
    memcpy(cells, f + o, cellBytes), o += cellBytes;
    *(uint8_t **)(grid + G_CELLS) = cells;
    memcpy(grid + G_HEIGHT_HEADER, f + o, 16), o += 16;
    uint32_t heightBytes = *(uint32_t *)(grid + G_HEIGHT_HEADER) * *(uint32_t *)(grid + G_HEIGHT_HEADER + 4) * 4;
    uint8_t *heights = OperatorNew(heightBytes);
    memcpy(heights, f + o, heightBytes), o += heightBytes;
    *(uint8_t **)(grid + G_HEIGHTS) = heights;
    if (o + G_REGIONS_SIZE > size) Fail("the grid file has no region table", NULL);
    memcpy(grid + G_REGIONS, f + o, G_REGIONS_SIZE);
    grid[2] = 1;
    for (int32_t i = 0; i < width * height; i++)
        if (*(uint16_t *)(cells + i * CELL_SIZE + CELL_FLAGS) & 0x100) *(uint16_t *)grid |= 2;
    /* the map loader, after a successful binary load. */
    *(uint32_t *)(grid + G_OWNER) = (uint32_t)Va(VAR_LOAD_MAP_OWNER);
    float mn[3], mx[3];
    memcpy(mn, grid + G_MIN, 12);
    memcpy(mx, grid + G_MAX, 12);
    ((SetMinMaxPos)Va(FN_SET_MIN_MAX_POS))(mn[0], mn[1], mn[2], mx[0], mx[1], mx[2]);
    free(f);
}

static void PrepareClientState(void)
{
    /* What the C runtime start-up would have stored: SSE2 present (the float-to-int helper then truncates with cvttsd2si). */
    *(int32_t *)Va(VAR_SSE2_AVAILABLE) = IsProcessorFeaturePresent(PF_XMMI64_INSTRUCTIONS_AVAILABLE) ? 1 : 0;
    clockVtable[4] = (void *)ClockNow;
    *(void **)Va(VAR_CLOCK) = clockObject;
    /* The client runs its game code with the x87 at 24-bit precision (Direct3D 9 sets it at device creation). */
    unsigned int cw;
    _controlfp_s(&cw, _PC_24, _MCW_PC);
}

/* ------------------------------------------------------------------ calls */

#pragma pack(push, 1)
typedef struct
{
    int16_t sx, sz, gx, gz, hx, hz;
    float startPos[3], goalPos[3];
    float speed, radius;
    int32_t cap;
    uint8_t pathFlag, ctx[3];
} AStarRequest;

typedef struct
{
    uint8_t ret, partial;
    uint16_t count;
    uint32_t nanoseconds;
} AStarHead;
#pragma pack(pop)

static uint8_t CallBuildNavGridPath(uint8_t *path, uint32_t hint, int16_t *start, float *startPos, int16_t *goal, float *goalPos,
    uint8_t *ctx, uint8_t *actor, float p9, int32_t cap, float p11)
{
    /* Every local is read before the first push: the compiler may address locals relative to ESP. */
    uint32_t args[11];
    args[0] = (uint32_t)(uintptr_t)path;
    args[1] = (uint32_t)(uintptr_t)&hint; /* a pointer to the packed look-ahead cell (the look-ahead routine returns its out-parameter) */
    args[2] = (uint32_t)(uintptr_t)start;
    args[3] = (uint32_t)(uintptr_t)startPos;
    args[4] = (uint32_t)(uintptr_t)goal;
    args[5] = (uint32_t)(uintptr_t)goalPos;
    args[6] = (uint32_t)(uintptr_t)ctx;
    args[7] = (uint32_t)(uintptr_t)actor;
    memcpy(&args[8], &p9, 4);
    args[9] = (uint32_t)cap;
    memcpy(&args[10], &p11, 4);
    uint32_t *a = args;
    void *fn = (void *)Va(FN_BUILD_NAV_GRID_PATH);
    uint8_t *g = grid;
    uint8_t r;
    __asm {
        mov eax, a
        mov ecx, fn
        mov edx, g
        push ebx
        push esi
        push edi
        mov esi, edx
        mov ebx, ecx
        push dword ptr [eax + 40]
        push dword ptr [eax + 36]
        push dword ptr [eax + 32]
        push dword ptr [eax + 28]
        push dword ptr [eax + 24]
        push dword ptr [eax + 20]
        push dword ptr [eax + 16]
        push dword ptr [eax + 12]
        push dword ptr [eax + 8]
        push dword ptr [eax + 4]
        push dword ptr [eax]
        call ebx
        pop edi
        pop esi
        pop ebx
        mov r, al
    }
    return r;
}

/* ------------------------------------------------------------------ the planner (batch 600)

   The client's build-path routine (then its smooth-path wrapper), as the issue-order routine calls them, on an actor mirror
   of the mover, with every other unit registered in the client's own actor grid (the nav-cell actor lists and the MultiGrid)
   by the client's own add-actor routine. */
#define FN_BUILD_PATH 0x749c90u       /* EAX = actor, stack: path, click; ret 8 */
#define FN_SMOOTH_PATH 0x7493c0u      /* stdcall (actor, path) */
#define FN_ADD_ACTOR 0x8a3c70u        /* the cell's add-actor routine: ESI = actor, stack: nav mesh; ret 4 */
#define FN_CELL_REMOVE 0x880a00u      /* the cell's actor list without the actor: ECX = cell, EDX = actor */
#define FN_MULTIGRID_REMOVE 0x4f20d0u /* stdcall (actor) */
#define FN_MULTIGRID_CTOR 0x454740u   /* the multi-grid's static constructor */
#define VAR_NAV_MESH 0x2bf365cu       /* the nav mesh: its first field is the TestNavGrid; every actor's +0x5c points here */
#define VAR_GAME_CLOCK 0x2c8ed64u     /* the game clock: {float now; ...; running flag at +8} (the clock getter makes it lazily) */
#define VAR_STUCK_FUNCTOR 0x2c7e508u  /* Loki functors made by the static initializers */
#define VAR_OCCUPIED_FUNCTOR 0x2c7e50cu
#define VTBL_STUCK_FUNCTOR 0xa0bcf4u
#define FN_STUCK_CALLBACK 0x7513f0u
#define VTBL_OCCUPIED_FUNCTOR 0xa0bd04u
#define FN_OCCUPIED_CALLBACK 0x74c620u

/* actor fields the planner and the actor tests read (packed: the position follows the flag byte). */
#define ACTOR_SIZE 0x300
#define A_NAV_MESH 0x5c
#define A_CLEAN 0x60
#define A_CELL 0x78
#define A_VEL_X 0x8c
#define A_VEL_Z 0x94
#define A_SPEED 0x98
#define A_MOVING 0x9c
#define A_END_X 0x100
#define A_END_Z 0x108
#define A_LAST_DT 0x138
#define A_GHOSTED 0x254
#define A_GHOST_PROOF 0x255
#define A_REPATH 0x258
#define A_RADIUS 0x26c
#define A_FLAG 0x290
#define A_POS_X 0x291
#define A_POS_Z 0x299

#define MAX_ACTORS 512
static uint8_t *mirrors[MAX_ACTORS];
static int registered;
static uint8_t *moverMirror;
static struct { float now; uint32_t unused; uint32_t running; uint32_t pad[5]; } gameClock = {0.0f, 0, 1, {0}};
static void *functors[2][2];

typedef void(__cdecl *Plain)(void);

static void InitPlanner(void)
{
    /* What the client's start-up and map load would have left: the MultiGrid built by its static constructor (run here), the two
       callback functors (made here as their initializers make them, without the Loki small-object pool), the game clock, and the
       nav mesh pointing at the grid. */
    ((Plain)Va(FN_MULTIGRID_CTOR))();
    functors[0][0] = (void *)Va(VTBL_STUCK_FUNCTOR);
    functors[0][1] = (void *)Va(FN_STUCK_CALLBACK);
    functors[1][0] = (void *)Va(VTBL_OCCUPIED_FUNCTOR);
    functors[1][1] = (void *)Va(FN_OCCUPIED_CALLBACK);
    *(void **)Va(VAR_STUCK_FUNCTOR) = functors[0];
    *(void **)Va(VAR_OCCUPIED_FUNCTOR) = functors[1];
    *(void **)Va(VAR_GAME_CLOCK) = &gameClock;
    *(uint8_t **)Va(VAR_NAV_MESH) = grid;
    for (int i = 0; i < MAX_ACTORS; i++) mirrors[i] = VirtualAlloc(NULL, 4096, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    moverMirror = VirtualAlloc(NULL, 4096, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    if (!moverMirror || !mirrors[MAX_ACTORS - 1]) Fail("out of memory", NULL);
}

static void AddActor(uint8_t *actor)
{
    void *fn = (void *)Va(FN_ADD_ACTOR);
    void *mesh = (void *)Va(VAR_NAV_MESH);
    __asm {
        mov eax, mesh
        mov ecx, fn
        mov edx, actor
        push ebx
        push esi
        push edi
        mov esi, edx
        push eax
        call ecx
        pop edi
        pop esi
        pop ebx
    }
}

static void RemoveActor(uint8_t *actor)
{
    uint32_t cell = *(uint32_t *)(actor + A_CELL);
    void *cellRemove = (void *)Va(FN_CELL_REMOVE);
    void *gridRemove = (void *)Va(FN_MULTIGRID_REMOVE);
    if (cell)
    {
        __asm {
            mov ecx, cell
            mov edx, actor
            mov eax, cellRemove
            push ebx
            push esi
            push edi
            call eax
            pop edi
            pop esi
            pop ebx
        }
        *(uint32_t *)(actor + A_CELL) = 0;
    }
    __asm {
        mov eax, actor
        mov ecx, gridRemove
        push ebx
        push esi
        push edi
        push eax
        call ecx
        pop edi
        pop esi
        pop ebx
    }
}

static void CallBuildPath(uint8_t *actor, uint8_t *path, float *click)
{
    void *fn = (void *)Va(FN_BUILD_PATH);
    __asm {
        mov eax, click
        mov ecx, path
        mov edx, fn
        push ebx
        push esi
        push edi
        push eax
        push ecx
        mov eax, actor
        call edx
        pop edi
        pop esi
        pop ebx
    }
}

static void CallSmoothPath(uint8_t *actor, uint8_t *path)
{
    void *fn = (void *)Va(FN_SMOOTH_PATH);
    __asm {
        mov eax, path
        mov ecx, actor
        mov edx, fn
        push ebx
        push esi
        push edi
        push eax
        push ecx
        call edx
        pop edi
        pop esi
        pop ebx
    }
}

#pragma pack(push, 1)
typedef struct
{
    float x, z, radius, speed, lastDt, velX, velZ, endX, endZ, cleanStamp;
    int32_t repathCount;
    uint8_t ghosted, ghostProof, flag290, moving;
} PlanActor;

typedef struct
{
    float now;
    float x, z, radius, speed, velX, velZ;
    int32_t repathCount;
    uint8_t ghosted, ghostProof, flag290, pad;
    float clickX, clickZ;
    uint32_t count;
} PlanRequest;
#pragma pack(pop)

static void FillActor(uint8_t *a, const PlanActor *p)
{
    memset(a, 0, ACTOR_SIZE);
    *(uint32_t *)(a + A_NAV_MESH) = (uint32_t)Va(VAR_NAV_MESH);
    memcpy(a + A_CLEAN, &p->cleanStamp, 4);
    memcpy(a + A_VEL_X, &p->velX, 4);
    memcpy(a + A_VEL_Z, &p->velZ, 4);
    memcpy(a + A_SPEED, &p->speed, 4);
    a[A_MOVING] = p->moving;
    memcpy(a + A_END_X, &p->endX, 4);
    memcpy(a + A_END_Z, &p->endZ, 4);
    memcpy(a + A_LAST_DT, &p->lastDt, 4);
    a[A_GHOSTED] = p->ghosted;
    a[A_GHOST_PROOF] = p->ghostProof;
    memcpy(a + A_REPATH, &p->repathCount, 4);
    memcpy(a + A_RADIUS, &p->radius, 4);
    a[A_FLAG] = p->flag290;
    memcpy(a + A_POS_X, &p->x, 4);
    memcpy(a + A_POS_Z, &p->z, 4);
}

/* Registers the request's units (in the order given), plans for the mover (never registered: BuildPath would unregister it for
   the search and register it again afterwards), and writes the smoothed path's points (x, y, z) to out. Returns the count. */
static uint32_t Plan(const PlanRequest *q, const PlanActor *units, float *out, uint32_t capacity, uint32_t *planNs)
{
    LARGE_INTEGER t0, t1, f;
    for (int i = 0; i < registered; i++) RemoveActor(mirrors[i]);
    registered = 0;
    gameClock.now = q->now;
    if (q->count > MAX_ACTORS) Fail("too many units", NULL);
    for (uint32_t i = 0; i < q->count; i++)
    {
        FillActor(mirrors[i], &units[i]);
        AddActor(mirrors[i]);
        registered++;
    }
    PlanActor m = {q->x, q->z, q->radius, q->speed, 0.0f, q->velX, q->velZ, q->x, q->z, 0.0f, q->repathCount, q->ghosted,
        q->ghostProof, q->flag290, 0};
    FillActor(moverMirror, &m);
    uint8_t path[0x60] = {0};
    float click[3] = {q->clickX, 0.0f, q->clickZ};
    QueryPerformanceCounter(&t0);
    CallBuildPath(moverMirror, path, click);
    /* A failed BuildNavigationPath (return 0) leaves the path empty. IssueOrder smooths it all the same, which makes a walk from
       the snapped position to the path's zero end point, (1, 1) on Map1's lattice; the server stops the unit instead (the port's
       null), so an empty path is answered as no plan. */
    if (*(float **)(path + 0x24) != *(float **)(path + 0x28)) CallSmoothPath(moverMirror, path);
    QueryPerformanceCounter(&t1);
    QueryPerformanceFrequency(&f);
    *planNs = (uint32_t)((t1.QuadPart - t0.QuadPart) * 1000000000LL / f.QuadPart);
    float *begin = *(float **)(path + 0x24), *end = *(float **)(path + 0x28);
    uint32_t n = begin ? (uint32_t)(end - begin) / 3 : 0;
    if (n > capacity) n = capacity;
    if (n) memcpy(out, begin, n * 12);
    free(begin);
    return n;
}

/* ------------------------------------------------------------------ the contact solver (batch 602, prototype)

   The client's update routine (stdcall (actor, dt, flag), ret 0xc) on a mirror that carries its own path (the path object: index,
   end point, points vector) and is registered in the client's actor grid like the other units. The owner
   interface answers 0 to the per-neighbour hook (virtual slot 2, thiscall with two arguments); the move notification
   is left empty. */
#define FN_UPDATE 0x74af90u
#define A_PATH 0xa0
#define A_PATH_END 0xb4
#define A_PATH_BEGIN 0xc4
#define A_PATH_LAST 0xc8
#define A_PATH_CAP 0xcc
#define A_ARRIVAL 0x64
#define A_ATTEMPTS 0x74
#define A_OWNER_HOOK 0x4c
#define A_PATH_ON 0x9c

static __declspec(naked) void OwnerHook(void) /* thiscall (other actor, kind): 0 = carry on */
{
    __asm {
        xor eax, eax
        ret 8
    }
}
static void *ownerVtable[8];
static void *ownerObject[1] = {ownerVtable};

/* the path-finished notification of the stop on arrival (virtual slot 2, no arguments). */
static __declspec(naked) void Notify(void)
{
    __asm { ret }
}
static void *notifyVtable[8];
static void *notifyObject[1] = {notifyVtable};
/* the actor's implementation pointer (a shared pointer target); the stop reaches the actor back through it. */
static void *pimpl[16];

#pragma pack(push, 1)
typedef struct
{
    float now, dt;
    PlanActor self;
    float arrivalStamp;
    int32_t attempts;
    /* the path's collision byte: the client's path-follow routine writes 1 for a 0x64/0xC1 path
       and 0 for a 0x67 dash; the stop and the constructor write 1. The update's flag
       argument: the unit's flag bit 0, set only by the create call's flag 1, which only a SpawnMinion with the
       IgnoreCollision bit passes. The path's speed, the word and the follow unit come from the
       same call's speed block: zero for every 0x64/0xC1 path, so the mirror's zeroes. */
    uint8_t collisions, updateFlag, pad[2];
    uint32_t index, points;      /* then points * (x, y, z) */
} UpdateRequest;

typedef struct
{
    float x, y, z, velX, velY, velZ, cleanStamp, arrivalStamp;
    int32_t attempts, repathCount;
    uint32_t index, points;
    uint8_t pathOn, pad[3];
    uint32_t ns;
} UpdateReply;
#pragma pack(pop)

static uint32_t updateFlag;

static void CallUpdate(uint8_t *actor, float dt)
{
    void *fn = (void *)Va(FN_UPDATE);
    uint32_t flag = updateFlag;
    __asm {
        mov eax, actor
        mov ecx, dt
        mov edx, fn
        push ebx
        push esi
        push edi
        push flag
        push ecx
        push eax
        call edx
        pop edi
        pop esi
        pop ebx
    }
}

/* DIAGNOSTICS: field watch. While it runs, every mirror page is a guard page, and the exception handler marks each byte offset
   the client reads or writes, separately for the mover (0) and the other units (1). */
static volatile LONG watching;
static uint8_t *rearm[8];
static int rearmCount;
static uint8_t watchMarks[4][ACTOR_SIZE]; /* mover read, mover write, units read, units write */
static int watchUpdate;

static void Guard(DWORD protection, uint32_t count)
{
    DWORD old;
    VirtualProtect(moverMirror, 4096, protection, &old);
    for (uint32_t i = 0; i < count; i++) VirtualProtect(mirrors[i], 4096, protection, &old);
}

static void SteppedUpdate(uint8_t *actor, float dt);

/* Registers the units, then one Update of the mover; the units are removed afterwards. */
static void Update(const UpdateRequest *q, const float *points, const PlanActor *units, uint32_t count, UpdateReply *r)
{
    for (int i = 0; i < registered; i++) RemoveActor(mirrors[i]);
    registered = 0;
    gameClock.now = q->now;
    if (count > MAX_ACTORS) Fail("too many units", NULL);
    for (uint32_t i = 0; i < count; i++)
    {
        FillActor(mirrors[i], &units[i]);
        AddActor(mirrors[i]);
        registered++;
    }
    uint8_t *a = moverMirror;
    FillActor(a, &q->self);
    ownerVtable[2] = (void *)OwnerHook;
    *(void **)(a + A_OWNER_HOOK) = ownerObject;
    notifyVtable[2] = (void *)Notify;
    *(void **)(a + 0x58) = notifyObject;
    pimpl[1] = a;
    a[0xd5] = q->collisions; /* the neighbour collector skips every neighbour of an actor whose collision byte is clear */
    updateFlag = q->updateFlag;
    *(void **)(a + 4) = pimpl;
    memcpy(a + A_ARRIVAL, &q->arrivalStamp, 4);
    memcpy(a + A_ATTEMPTS, &q->attempts, 4);
    float *pts = OperatorNew(q->points * 12 + 12);
    memcpy(pts, points, q->points * 12);
    *(uint32_t *)(a + A_PATH) = q->index;
    *(uint32_t *)(a + A_PATH + 4) = (uint32_t)Va(VAR_NAV_MESH);
    if (q->points) memcpy(a + A_PATH_END, pts + (q->points - 1) * 3, 12);
    *(float **)(a + A_PATH_BEGIN) = pts;
    *(float **)(a + A_PATH_LAST) = pts + q->points * 3;
    *(float **)(a + A_PATH_CAP) = pts + q->points * 3 + 3;
    a[A_PATH_ON] = q->points ? 1 : 0;
    AddActor(a);
    LARGE_INTEGER t0, t1, f;
    QueryPerformanceCounter(&t0);
    if (watchUpdate == 1)
    {
        memset(watchMarks, 0, sizeof watchMarks);
        Guard(PAGE_READWRITE | PAGE_GUARD, count);
        watching = 1;
    }
    if (watchUpdate == 2) SteppedUpdate(a, q->dt);
    else CallUpdate(a, q->dt);
    if (watchUpdate == 1)
    {
        watching = 0;
        Guard(PAGE_READWRITE, count);
    }
    QueryPerformanceCounter(&t1);
    QueryPerformanceFrequency(&f);
    memcpy(&r->x, a + A_POS_X, 12);
    memcpy(&r->velX, a + A_VEL_X, 12);
    memcpy(&r->cleanStamp, a + A_CLEAN, 4);
    memcpy(&r->arrivalStamp, a + A_ARRIVAL, 4);
    memcpy(&r->attempts, a + A_ATTEMPTS, 4);
    memcpy(&r->repathCount, a + A_REPATH, 4);
    r->index = *(uint32_t *)(a + A_PATH);
    float *b = *(float **)(a + A_PATH_BEGIN), *e = *(float **)(a + A_PATH_LAST);
    r->points = b ? (uint32_t)(e - b) / 3 : 0;
    r->pathOn = a[A_PATH_ON];
    r->ns = (uint32_t)((t1.QuadPart - t0.QuadPart) * 1000000000LL / f.QuadPart);
    RemoveActor(a);
    free(b);
}

/* Batch 606: one frame in one call. Every body of the frame is registered from its snapshot, then each entry in the frame's order
   either runs the client's Update on its own mirror (kind 0, its path and stopwatches as the server's update starts; kind 2 then takes the
   server's result too) or takes the
   server's result for it (kind 1: removed, refilled from its end row and added again), so each update sees the bodies as the frame
   left them so far. Request: FrameHeader, then count entries (FrameEntry, then its points). Reply: u32 ns in all, u32 ns in the
   updates, then one UpdateReply per kind-0 entry, in order. */
#pragma pack(push, 1)
typedef struct
{
    float now, dt;
    uint32_t count;
} FrameHeader;

typedef struct
{
    uint8_t kind, collisions, updateFlag, pad;
    PlanActor start, end;
    float arrivalStamp;
    int32_t attempts;
    uint32_t index, points; /* then points * (x, y, z) */
} FrameEntry;
#pragma pack(pop)

static uint32_t Frame(const uint8_t *p, uint32_t length, uint8_t *out, uint32_t capacity)
{
    LARGE_INTEGER t0, t1, f, u0, u1;
    QueryPerformanceCounter(&t0);
    QueryPerformanceFrequency(&f);
    for (int i = 0; i < registered; i++) RemoveActor(mirrors[i]);
    registered = 0;
    const FrameHeader *h = (const FrameHeader *)p;
    if (length < sizeof *h || h->count > MAX_ACTORS) Fail("bad frame request", NULL);
    gameClock.now = h->now;
    static const FrameEntry *entries[MAX_ACTORS];
    const uint8_t *at = p + sizeof *h;
    for (uint32_t i = 0; i < h->count; i++)
    {
        entries[i] = (const FrameEntry *)at;
        at += sizeof(FrameEntry) + entries[i]->points * 12;
        if (at > p + length) Fail("short frame request", NULL);
        FillActor(mirrors[i], &entries[i]->start);
        AddActor(mirrors[i]);
        registered++;
    }
    uint32_t n = 8;
    int64_t updateTicks = 0;
    for (uint32_t i = 0; i < h->count; i++)
    {
        const FrameEntry *e = entries[i];
        uint8_t *a = mirrors[i];
        if (e->kind == 1)
        {
            RemoveActor(a);
            FillActor(a, &e->end);
            AddActor(a);
            continue;
        }
        if (n + sizeof(UpdateReply) > capacity) Fail("frame reply too long", NULL);
        ownerVtable[2] = (void *)OwnerHook;
        *(void **)(a + A_OWNER_HOOK) = ownerObject;
        notifyVtable[2] = (void *)Notify;
        *(void **)(a + 0x58) = notifyObject;
        pimpl[1] = a;
        *(void **)(a + 4) = pimpl;
        a[0xd5] = e->collisions;
        updateFlag = e->updateFlag;
        memcpy(a + A_ARRIVAL, &e->arrivalStamp, 4);
        memcpy(a + A_ATTEMPTS, &e->attempts, 4);
        float *pts = OperatorNew(e->points * 12 + 12);
        memcpy(pts, e + 1, e->points * 12);
        *(uint32_t *)(a + A_PATH) = e->index;
        *(uint32_t *)(a + A_PATH + 4) = (uint32_t)Va(VAR_NAV_MESH);
        if (e->points) memcpy(a + A_PATH_END, pts + (e->points - 1) * 3, 12);
        *(float **)(a + A_PATH_BEGIN) = pts;
        *(float **)(a + A_PATH_LAST) = pts + e->points * 3;
        *(float **)(a + A_PATH_CAP) = pts + e->points * 3 + 3;
        a[A_PATH_ON] = e->points ? 1 : 0;
        QueryPerformanceCounter(&u0);
        CallUpdate(a, h->dt);
        QueryPerformanceCounter(&u1);
        updateTicks += u1.QuadPart - u0.QuadPart;
        UpdateReply *r = (UpdateReply *)(out + n);
        n += sizeof *r;
        memcpy(&r->x, a + A_POS_X, 12);
        memcpy(&r->velX, a + A_VEL_X, 12);
        memcpy(&r->cleanStamp, a + A_CLEAN, 4);
        memcpy(&r->arrivalStamp, a + A_ARRIVAL, 4);
        memcpy(&r->attempts, a + A_ATTEMPTS, 4);
        memcpy(&r->repathCount, a + A_REPATH, 4);
        r->index = *(uint32_t *)(a + A_PATH);
        float *b = *(float **)(a + A_PATH_BEGIN), *last = *(float **)(a + A_PATH_LAST);
        r->points = b ? (uint32_t)(last - b) / 3 : 0;
        r->pathOn = a[A_PATH_ON];
        r->ns = (uint32_t)((u1.QuadPart - u0.QuadPart) * 1000000000LL / f.QuadPart);
        /* The mirror stays registered as a body for the rest of the frame, without its path (the snapshot rows carry none either).
           Kind 2: then it takes the server's result, so a difference does not carry into the later updates of the frame. */
        free(b);
        *(float **)(a + A_PATH_BEGIN) = *(float **)(a + A_PATH_LAST) = *(float **)(a + A_PATH_CAP) = NULL;
        if (e->kind == 2)
        {
            RemoveActor(a);
            FillActor(a, &e->end);
            AddActor(a);
        }
    }
    QueryPerformanceCounter(&t1);
    uint32_t total = (uint32_t)((t1.QuadPart - t0.QuadPart) * 1000000000LL / f.QuadPart);
    uint32_t updates = (uint32_t)(updateTicks * 1000000000LL / f.QuadPart);
    memcpy(out, &total, 4);
    memcpy(out + 4, &updates, 4);
    return n;
}

/* ------------------------------------------------------------------ persistent mirrors (batch 608, production)

   The server's movement runs here (MovementSystem.ExeHost.cs). Each slot holds one actor mirror for the whole match, registered in the
   client's actor grid, so a request carries only what changed since the last one, in the frame's order: a body's row (Set), its
   removal, its path, the client's Update of a unit, and the client's planner for a click. Request: MirrorHeader, then count records
   (MirrorRecord and its payload). Reply: u32 ns in all, u32 ns in the updates, then for each Update record a MirrorUpdateOut, its
   collider slots and (when the update bent the path) the path's points, and for each Plan record u32 count, u32 partial and the points. */
#define MAX_SLOTS 512
#define P_SLOT 0x400  /* the slot number, on the mirror's page outside the actor */
#define P_FLAG 0x404  /* the update's flag argument (obj+0xb8 bit 0, see UpdateRequest) */
#define P_PIMPL 0x410 /* the mirror's implementation pointer; it points back at the actor (the stop goes through it) */
#define G_STUCK_FOUND 0x34 /* the stuck-actor check sets this grid byte when it finds a stuck actor */
#define AT_NEIGHBOUR_HOOK 0x74b4e5u /* the return address of Update's call of the owner hook over the neighbour list */
#define MAX_COLLIDERS 64
#define MAX_PATH_POINTS 1024

static uint8_t *slotPage[MAX_SLOTS];
static uint8_t slotLive[MAX_SLOTS];

#pragma pack(push, 1)
typedef struct
{
    float now, dt;
    uint32_t count;
} MirrorHeader;

typedef struct
{
    uint8_t kind, pad;
    uint16_t slot;
} MirrorRecord;

typedef struct
{
    PlanActor row; /* the moving byte is ignored: the path record owns +0x9c */
    float arrivalStamp;
    int32_t attempts;
    uint8_t collisions, updateFlag, pad[2];
} MirrorRow;

typedef struct
{
    float x, z, velX, velZ, cleanStamp, arrivalStamp, intendedX, intendedZ;
    int32_t attempts, repathCount;
    uint32_t index, points;
    uint8_t pathOn, held, bent, intendedSeen;
    uint16_t colliders, pad;
} MirrorUpdateOut;
#pragma pack(pop)

enum { RecSet = 1, RecRemove = 2, RecPath = 3, RecUpdate = 4, RecPlan = 5 };

/* What the owner hook saw during the current Update: the mover's +0x8c/+0x94 at the first call (the step AssembleWaypointList made, before
   HandleActorCollision changes it) and the neighbours the gather listed as colliders. */
static uint32_t neighbourHookReturn;
static int hookSeen, hookColliders;
static float hookIntended[2];
static uint16_t hookCollider[MAX_COLLIDERS];

static void __cdecl NoteNeighbour(const uint8_t *other, const uint32_t *entry, const uint8_t *mover)
{
    if (!hookSeen)
    {
        hookSeen = 1;
        memcpy(&hookIntended[0], mover + A_VEL_X, 4);
        memcpy(&hookIntended[1], mover + A_VEL_Z, 4);
    }
    /* The list entry (12 bytes): the actor, its squared distance, and the neighbour collector's kind (0 near, 1 collider, 2 avoid). */
    if (entry[2] == 1 && hookColliders < MAX_COLLIDERS)
    {
        uint32_t slot;
        memcpy(&slot, (const uint8_t *)((uintptr_t)other & ~(uintptr_t)0xfff) + P_SLOT, 4);
        hookCollider[hookColliders++] = (uint16_t)slot;
    }
}

/* a persistent mirror's slot 2 answers 0 (carry on) like OwnerHook. At the neighbour loop ESI holds the list
   entry and EBX the mover, which this fixed build (SHA-256 above) keeps there across the call. */
static __declspec(naked) void TracingHook(void)
{
    __asm {
        mov eax, [esp]
        cmp eax, neighbourHookReturn
        jne done
        push ebx
        push esi
        push dword ptr [esp + 12]
        call NoteNeighbour
        add esp, 12
    done:
        xor eax, eax
        ret 8
    }
}
static void *tracingVtable[8];
static void *tracingObject[1] = {tracingVtable};

static uint8_t *SlotActor(uint16_t slot)
{
    if (slot >= MAX_SLOTS) Fail("bad slot", NULL);
    if (!slotPage[slot])
    {
        slotPage[slot] = VirtualAlloc(NULL, 4096, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        if (!slotPage[slot]) Fail("out of memory", NULL);
    }
    return slotPage[slot];
}

static void FreePath(uint8_t *a)
{
    free(*(float **)(a + A_PATH_BEGIN));
    *(float **)(a + A_PATH_BEGIN) = *(float **)(a + A_PATH_LAST) = *(float **)(a + A_PATH_CAP) = NULL;
    *(uint32_t *)(a + A_PATH) = 0;
    a[A_PATH_ON] = 0;
}

static void SetMirror(uint16_t slot, const MirrorRow *m)
{
    uint8_t *a = SlotActor(slot);
    const PlanActor *p = &m->row;
    if (!slotLive[slot])
    {
        memset(a, 0, 4096);
        *(uint32_t *)(a + A_NAV_MESH) = (uint32_t)Va(VAR_NAV_MESH);
        *(void **)(a + A_OWNER_HOOK) = tracingObject;
        *(void **)(a + 0x58) = notifyObject;
        ((void **)(a + P_PIMPL))[1] = a;
        *(void **)(a + 4) = a + P_PIMPL;
        *(uint32_t *)(a + P_SLOT) = slot;
    }
    /* The grid entry follows the position and the radius: out before they change (the removal finds the actor where it was), in after. */
    int moved = slotLive[slot] && (memcmp(a + A_POS_X, &p->x, 4) || memcmp(a + A_POS_Z, &p->z, 4) || memcmp(a + A_RADIUS, &p->radius, 4));
    if (moved) RemoveActor(a);
    memcpy(a + A_CLEAN, &p->cleanStamp, 4);
    memcpy(a + A_VEL_X, &p->velX, 4);
    memcpy(a + A_VEL_Z, &p->velZ, 4);
    memcpy(a + A_SPEED, &p->speed, 4);
    memcpy(a + A_END_X, &p->endX, 4);
    memcpy(a + A_END_Z, &p->endZ, 4);
    memcpy(a + A_LAST_DT, &p->lastDt, 4);
    a[A_GHOSTED] = p->ghosted;
    a[A_GHOST_PROOF] = p->ghostProof;
    memcpy(a + A_REPATH, &p->repathCount, 4);
    memcpy(a + A_RADIUS, &p->radius, 4);
    a[A_FLAG] = p->flag290;
    memcpy(a + A_POS_X, &p->x, 4);
    memcpy(a + A_POS_Z, &p->z, 4);
    memcpy(a + A_ARRIVAL, &m->arrivalStamp, 4);
    memcpy(a + A_ATTEMPTS, &m->attempts, 4);
    a[0xd5] = m->collisions;
    *(uint32_t *)(a + P_FLAG) = m->updateFlag;
    if (!slotLive[slot] || moved) AddActor(a);
    slotLive[slot] = 1;
}

static uint8_t *LiveActor(uint16_t slot)
{
    if (slot >= MAX_SLOTS || !slotLive[slot]) Fail("slot not registered", NULL);
    return slotPage[slot];
}

/* The path as the client's path-follow routine leaves it: index, points (x, absolute height, z), the end point and its flag set; no points
   turns the path off. */
static void SetPath(uint8_t *a, uint32_t index, uint32_t count, const float *points)
{
    FreePath(a);
    if (!count) return;
    float *pts = OperatorNew(count * 12 + 12);
    memcpy(pts, points, count * 12);
    *(uint32_t *)(a + A_PATH) = index;
    *(uint32_t *)(a + A_PATH + 4) = (uint32_t)Va(VAR_NAV_MESH);
    memcpy(a + A_PATH_END, pts + (count - 1) * 3, 12);
    *(float **)(a + A_PATH_BEGIN) = pts;
    *(float **)(a + A_PATH_LAST) = pts + count * 3;
    *(float **)(a + A_PATH_CAP) = pts + count * 3 + 3;
    a[A_PATH_ON] = 1;
}

static uint32_t PathCount(const uint8_t *a)
{
    const float *b = *(float *const *)(a + A_PATH_BEGIN), *e = *(float *const *)(a + A_PATH_LAST);
    return b ? (uint32_t)(e - b) / 3 : 0;
}

static float pathBefore[MAX_PATH_POINTS * 3];

static uint32_t Mirrors(const uint8_t *p, uint32_t length, uint8_t *out, uint32_t capacity)
{
    LARGE_INTEGER t0, t1, f, u0, u1;
    QueryPerformanceCounter(&t0);
    QueryPerformanceFrequency(&f);
    const MirrorHeader *h = (const MirrorHeader *)p;
    if (length < sizeof *h) Fail("bad mirror request", NULL);
    gameClock.now = h->now;
    const uint8_t *at = p + sizeof *h, *end = p + length;
    uint32_t n = 8;
    int64_t updateTicks = 0;
    for (uint32_t i = 0; i < h->count; i++)
    {
        if (at + sizeof(MirrorRecord) > end) Fail("short mirror request", NULL);
        const MirrorRecord *r = (const MirrorRecord *)at;
        at += sizeof *r;
        if (r->kind == RecSet)
        {
            if (at + sizeof(MirrorRow) > end) Fail("short mirror row", NULL);
            SetMirror(r->slot, (const MirrorRow *)at);
            at += sizeof(MirrorRow);
        }
        else if (r->kind == RecRemove)
        {
            uint8_t *a = LiveActor(r->slot);
            RemoveActor(a);
            FreePath(a);
            slotLive[r->slot] = 0;
        }
        else if (r->kind == RecPath)
        {
            uint32_t q[2];
            if (at + 8 > end) Fail("short path", NULL);
            memcpy(q, at, 8);
            if (q[1] > MAX_PATH_POINTS || at + 8 + q[1] * 12 > end) Fail("bad path", NULL);
            SetPath(LiveActor(r->slot), q[0], q[1], (const float *)(at + 8));
            at += 8 + q[1] * 12;
        }
        else if (r->kind == RecUpdate)
        {
            /* The mover's height for its frame: AssembleWaypointList's slope term reads wp.y - pos.y, the path holds the heights. Every body
               stays on one plane otherwise (y 0), as the batch 606 differential had them. */
            if (at + 4 > end) Fail("short update", NULL);
            uint8_t *a = LiveActor(r->slot);
            memcpy(a + 0x295, at, 4);
            at += 4;
            uint32_t before = PathCount(a);
            if (before > MAX_PATH_POINTS) Fail("path too long", NULL);
            if (before) memcpy(pathBefore, *(float **)(a + A_PATH_BEGIN), before * 12);
            uint8_t found = grid[G_STUCK_FOUND];
            grid[G_STUCK_FOUND] = 0;
            hookSeen = hookColliders = 0;
            updateFlag = *(uint32_t *)(a + P_FLAG);
            QueryPerformanceCounter(&u0);
            CallUpdate(a, h->dt);
            QueryPerformanceCounter(&u1);
            updateTicks += u1.QuadPart - u0.QuadPart;
            /* HasStuckActor runs in Update only at the two forced-stop sites, and the site is reached
               exactly when it answers yes. */
            uint8_t held = grid[G_STUCK_FOUND];
            grid[G_STUCK_FOUND] = found | held;
            memset(a + 0x295, 0, 4);
            uint32_t count = PathCount(a);
            int bent = count != before || (count && memcmp(pathBefore, *(float **)(a + A_PATH_BEGIN), count * 12));
            uint32_t need = sizeof(MirrorUpdateOut) + hookColliders * 2 + (bent ? count * 12 : 0);
            if (n + need > capacity) Fail("mirror reply too long", NULL);
            MirrorUpdateOut *o = (MirrorUpdateOut *)(out + n);
            memcpy(&o->x, a + A_POS_X, 4);
            memcpy(&o->z, a + A_POS_Z, 4);
            memcpy(&o->velX, a + A_VEL_X, 4);
            memcpy(&o->velZ, a + A_VEL_Z, 4);
            memcpy(&o->cleanStamp, a + A_CLEAN, 4);
            memcpy(&o->arrivalStamp, a + A_ARRIVAL, 4);
            o->intendedX = hookIntended[0];
            o->intendedZ = hookIntended[1];
            memcpy(&o->attempts, a + A_ATTEMPTS, 4);
            memcpy(&o->repathCount, a + A_REPATH, 4);
            o->index = *(uint32_t *)(a + A_PATH);
            o->points = count;
            o->pathOn = a[A_PATH_ON];
            o->held = held;
            o->bent = (uint8_t)bent;
            o->intendedSeen = (uint8_t)hookSeen;
            o->colliders = (uint16_t)hookColliders;
            o->pad = 0;
            n += sizeof *o;
            memcpy(out + n, hookCollider, hookColliders * 2);
            n += hookColliders * 2;
            if (bent && count) memcpy(out + n, *(float **)(a + A_PATH_BEGIN), count * 12), n += count * 12;
        }
        else if (r->kind == RecPlan)
        {
            /* The client's build-path routine and the smooth-path wrapper on the mover's own registered mirror (the build takes it out of the grid for
               the search and puts it back), with the units the mover's client cannot see taken out of the grid for the call. */
            uint32_t q[3];
            if (at + 12 > end) Fail("short plan", NULL);
            memcpy(q, at, 12);
            uint32_t hidden = q[2];
            if (at + 12 + hidden * 2 > end) Fail("short plan", NULL);
            const uint16_t *hide = (const uint16_t *)(at + 12);
            for (uint32_t k = 0; k < hidden; k++) RemoveActor(LiveActor(hide[k]));
            uint8_t path[0x60] = {0};
            float click[3];
            memcpy(&click[0], &q[0], 4);
            click[1] = 0.0f;
            memcpy(&click[2], &q[1], 4);
            uint8_t *a = LiveActor(r->slot);
            grid[G_PARTIAL] = 0; /* BuildNavGridPath sets it when its search ran out (the server charges such a plan the search cap) */
            CallBuildPath(a, path, click);
            /* A failed BuildNavigationPath leaves the path empty, and the server answers no plan (see Plan). */
            if (*(float **)(path + 0x24) != *(float **)(path + 0x28)) CallSmoothPath(a, path);
            for (uint32_t k = 0; k < hidden; k++) AddActor(LiveActor(hide[k]));
            at += 12 + hidden * 2;
            float *b = *(float **)(path + 0x24), *e = *(float **)(path + 0x28);
            uint32_t count = b ? (uint32_t)(e - b) / 3 : 0;
            if (n + 8 + count * 12 > capacity) Fail("mirror reply too long", NULL);
            uint32_t partial = grid[G_PARTIAL];
            memcpy(out + n, &count, 4);
            memcpy(out + n + 4, &partial, 4);
            if (count) memcpy(out + n + 8, b, count * 12);
            n += 8 + count * 12;
            free(b);
        }
        else Fail("unknown mirror record", NULL);
    }
    QueryPerformanceCounter(&t1);
    uint32_t total = (uint32_t)((t1.QuadPart - t0.QuadPart) * 1000000000LL / f.QuadPart);
    uint32_t updates = (uint32_t)(updateTicks * 1000000000LL / f.QuadPart);
    memcpy(out, &total, 4);
    memcpy(out + 4, &updates, 4);
    return n;
}

static void InitMirrors(void)
{
    neighbourHookReturn = (uint32_t)Va(AT_NEIGHBOUR_HOOK);
    tracingVtable[2] = (void *)TracingHook;
    notifyVtable[2] = (void *)Notify;
}

/* DIAGNOSTICS: single-stepping (trap flag) for the emulator estimate (instruction counts and mix) and the pop trace. */
#define AT_AFTER_POP 0x750c50u /* the grid path builder, the instruction after its call to the open-list pop */
#define MAX_POPS 20000
static volatile LONG stepping, entered;
static uint64_t stepsInImage, stepsOutside; /* client instructions executed; calls out of the image (not stepped) */
static uint32_t *executed; /* one bit per image byte offset: instructions executed at least once */
#pragma pack(push, 1)
static struct { int16_t x, z; float f; } pops[MAX_POPS];
#pragma pack(pop)
static uint32_t popCount;

static LONG CALLBACK OnException(EXCEPTION_POINTERS *e)
{
    uint32_t eip = e->ContextRecord->Eip;
    int inImage = eip >= (uintptr_t)image && eip < (uintptr_t)image + imageSize;
    if (watching && e->ExceptionRecord->ExceptionCode == STATUS_GUARD_PAGE_VIOLATION)
    {
        uintptr_t address = e->ExceptionRecord->ExceptionInformation[1];
        uint8_t *page = (uint8_t *)(address & ~(uintptr_t)0xfff);
        uint32_t offset = (uint32_t)(address & 0xfff);
        int unit = page != moverMirror;
        int write = e->ExceptionRecord->ExceptionInformation[0] == 1;
        if (offset < ACTOR_SIZE) watchMarks[unit * 2 + write][offset] = 1;
        if (rearmCount < 8) rearm[rearmCount++] = page;
        e->ContextRecord->EFlags |= 0x100;
        return EXCEPTION_CONTINUE_EXECUTION;
    }
    if (e->ExceptionRecord->ExceptionCode == EXCEPTION_SINGLE_STEP && rearmCount)
    {
        DWORD old;
        for (int i = 0; i < rearmCount; i++) VirtualProtect(rearm[i], 4096, PAGE_READWRITE | PAGE_GUARD, &old);
        rearmCount = 0;
        if (!stepping) e->ContextRecord->EFlags &= ~0x100u;
        return EXCEPTION_CONTINUE_EXECUTION;
    }
    if (e->ExceptionRecord->ExceptionCode == EXCEPTION_SINGLE_STEP && stepping)
    {
        CONTEXT *c = e->ContextRecord;
        if (c->Dr7 & 1)
        {
            /* Back from a call out of the image (the breakpoint on its return address): step again. */
            c->Dr7 &= ~1u;
            c->Dr0 = 0;
            c->Dr6 = 0;
        }
        else if (!inImage && entered)
        {
            /* The image called out (an import such as operator new). Single-stepping through a system call of the WOW64 layer
               kills the process, so run the callee free and catch the return with a hardware breakpoint. Leaving through
               the final return ends the stepping. */
            uint32_t back = *(uint32_t *)(uintptr_t)c->Esp;
            if (back >= (uintptr_t)image && back < (uintptr_t)image + imageSize)
            {
                stepsOutside++;
                c->Dr0 = back;
                c->Dr7 = (c->Dr7 & ~0xf0003u) | 1u; /* local enable, execute, length 1 */
            }
            else stepping = 0;
            c->EFlags &= ~0x100u;
            return EXCEPTION_CONTINUE_EXECUTION;
        }
        if (inImage)
        {
            entered = 1;
            stepsInImage++;
            uint32_t off = eip - (uint32_t)(uintptr_t)image;
            executed[off >> 5] |= 1u << (off & 31);
            if (eip == Va(AT_AFTER_POP) && popCount < MAX_POPS)
            {
                /* The popped record and its key sit at [esp + 0x54] and [esp + 0x58] right after the pop. */
                uint8_t *esp = (uint8_t *)(uintptr_t)e->ContextRecord->Esp;
                uint8_t *rec = *(uint8_t **)(esp + 0x54);
                memcpy(&pops[popCount].x, rec + 0x18, 2);
                memcpy(&pops[popCount].z, rec + 0x1a, 2);
                memcpy(&pops[popCount].f, esp + 0x58, 4);
                popCount++;
            }
        }
        c->EFlags |= 0x100;
        return EXCEPTION_CONTINUE_EXECUTION;
    }
    if (e->ExceptionRecord->ExceptionCode == EXCEPTION_SINGLE_STEP) return EXCEPTION_CONTINUE_EXECUTION;
    uint32_t top = *(uint32_t *)(uintptr_t)e->ContextRecord->Esp; /* the caller, when the fault is a call through null */
    int topInImage = top >= (uintptr_t)image && top < (uintptr_t)image + imageSize;
    fprintf(stderr, "exehost: exception %08lx at %s%08x (stack top %s%08x)\n", e->ExceptionRecord->ExceptionCode,
        inImage ? "client " : "", inImage ? (unsigned)(eip - (uint32_t)(uintptr_t)image + PREFERRED_BASE) : (unsigned)eip,
        topInImage ? "client " : "", topInImage ? (unsigned)(top - (uint32_t)(uintptr_t)image + PREFERRED_BASE) : (unsigned)top);
    fflush(stderr);
    ExitProcess(4);
    return EXCEPTION_CONTINUE_SEARCH;
}

/* An exception no vectored handler saw (for example one raised while the frame-based handlers were being walked). */
static LONG WINAPI OnUnhandled(EXCEPTION_POINTERS *e)
{
    uint32_t eip = e->ContextRecord->Eip;
    int inImage = eip >= (uintptr_t)image && eip < (uintptr_t)image + imageSize;
    fprintf(stderr, "exehost: unhandled exception %08lx at %s%08x, stepping %ld, %llu steps\n", e->ExceptionRecord->ExceptionCode,
        inImage ? "client " : "", inImage ? (unsigned)(eip - (uint32_t)(uintptr_t)image + PREFERRED_BASE) : (unsigned)eip,
        stepping, (unsigned long long)stepsInImage);
    fflush(stderr);
    ExitProcess(5);
    return EXCEPTION_EXECUTE_HANDLER;
}

/* DIAGNOSTICS: one Update single-stepped, so the executed-address bitmap (request 5) shows which code it ran. */
static __declspec(noinline) void SteppedUpdate(uint8_t *actor, float dt)
{
    entered = 0;
    stepping = 1;
    __asm {
        pushfd
        or dword ptr [esp], 0x100
        popfd
    }
    CallUpdate(actor, dt);
    stepping = 0;
    __asm {
        pushfd
        and dword ptr [esp], 0xfffffeff
        popfd
    }
}

static __declspec(noinline) uint8_t SteppedCall(uint8_t *path, uint32_t hint, int16_t *start, float *startPos, int16_t *goal,
    float *goalPos, uint8_t *ctx, uint8_t *actor, int32_t cap)
{
    entered = 0;
    stepping = 1;
    __asm {
        pushfd
        or dword ptr [esp], 0x100
        popfd
    }
    uint8_t r = CallBuildNavGridPath(path, hint, start, startPos, goal, goalPos, ctx, actor, 0.0f, cap, -1.0f);
    stepping = 0;
    __asm {
        pushfd
        and dword ptr [esp], 0xfffffeff
        popfd
    }
    return r;
}

/* ------------------------------------------------------------------ protocol */

static HANDLE in, out;

static void ReadExact(void *buffer, DWORD n)
{
    for (DWORD done = 0; done < n;)
    {
        DWORD got = 0;
        if (!ReadFile(in, (uint8_t *)buffer + done, n - done, &got, NULL) || got == 0) ExitProcess(0);
        done += got;
    }
}

static void Reply(const void *payload, uint32_t n)
{
    DWORD wrote;
    WriteFile(out, &n, 4, &wrote, NULL);
    if (n) WriteFile(out, payload, n, &wrote, NULL);
}

enum { OpPing = 1, OpStamp = 2, OpAStar = 3, OpAStarStepped = 4, OpPlan = 6, OpUpdate = 7, OpUpdateWatched = 8, OpUpdateStepped = 9, OpFrame = 10,
    OpMirrors = 11 };

/* Requests: u32 op, u32 length, payload. Replies: u32 length, payload. */
static void Serve(void)
{
    static uint8_t buffer[1 << 20];
    static uint8_t reply[1 << 20];
    LARGE_INTEGER frequency;
    QueryPerformanceFrequency(&frequency);
    for (;;)
    {
        uint32_t head[2];
        ReadExact(head, 8);
        if (head[1] > sizeof buffer) Fail("request too large", NULL);
        ReadExact(buffer, head[1]);
        if (head[0] == OpPing) { Reply(buffer, head[1]); continue; }
        if (head[0] == OpStamp)
        {
            /* One byte per cell, 1 = the caller's view says walkable. A cell the view blocks gets the turret stamp's 0x42
               (the turret's create routine); a cell the file blocks but the view does not is counted, so the caller can see that the
               inputs differ. */
            uint32_t stamped = 0, mismatched = 0;
            for (int32_t i = 0; i < width * height && (uint32_t)i < head[1]; i++)
            {
                uint16_t *flags = (uint16_t *)(cells + i * CELL_SIZE + CELL_FLAGS);
                if (!buffer[i] && !(*flags & 0x02)) *flags |= 0x42, stamped++;
                else if (buffer[i] && (*flags & 0x42)) mismatched++;
            }
            uint32_t r[2] = {stamped, mismatched};
            Reply(r, sizeof r);
            continue;
        }
        if (head[0] == OpAStar || head[0] == OpAStarStepped)
        {
            AStarRequest q;
            memcpy(&q, buffer, sizeof q);
            uint8_t actor[0x300] = {0};
            actor[0x290] = q.pathFlag;
            memcpy(actor + 0x98, &q.speed, 4);
            memcpy(actor + 0x26c, &q.radius, 4);
            uint8_t ctx[4] = {q.ctx[0], q.ctx[1], q.ctx[2], 0};
            uint8_t path[0x60] = {0};
            int16_t start[2] = {q.sx, q.sz}, goal[2] = {q.gx, q.gz};
            uint32_t hint = (uint16_t)q.hx | ((uint32_t)(uint16_t)q.hz << 16);
            LARGE_INTEGER t0, t1;
            uint8_t r;
            QueryPerformanceCounter(&t0);
            if (head[0] == OpAStar)
                r = CallBuildNavGridPath(path, hint, start, q.startPos, goal, q.goalPos, ctx, actor, 0.0f, q.cap, -1.0f);
            else
            {
                stepsInImage = stepsOutside = 0;
                popCount = 0;
                r = SteppedCall(path, hint, start, q.startPos, goal, q.goalPos, ctx, actor, q.cap);
            }
            QueryPerformanceCounter(&t1);
            float *begin = *(float **)(path + 0x24), *end = *(float **)(path + 0x28);
            AStarHead h = {r, grid[G_PARTIAL], (uint16_t)(begin ? (end - begin) / 3 : 0),
                (uint32_t)((t1.QuadPart - t0.QuadPart) * 1000000000LL / frequency.QuadPart)};
            uint32_t n = sizeof h;
            memcpy(reply, &h, sizeof h);
            if (head[0] == OpAStarStepped)
            {
                memcpy(reply + n, &stepsInImage, 8), n += 8;
                memcpy(reply + n, &stepsOutside, 8), n += 8;
            }
            if (begin && n + h.count * 12u <= sizeof reply) memcpy(reply + n, begin, h.count * 12u), n += h.count * 12u;
            free(begin);
            if (head[0] == OpAStarStepped)
            {
                /* DIAGNOSTICS: the pop trace (cell and key of every pop), after the points. */
                memcpy(reply + n, &popCount, 4), n += 4;
                memcpy(reply + n, pops, popCount * sizeof pops[0]), n += popCount * sizeof pops[0];
            }
            Reply(reply, n);
            continue;
        }
        if (head[0] == OpFrame)
        {
            Reply(reply, Frame(buffer, head[1], reply, sizeof reply));
            continue;
        }
        if (head[0] == OpMirrors)
        {
            Reply(reply, Mirrors(buffer, head[1], reply, sizeof reply));
            continue;
        }
        if (head[0] == OpUpdate || head[0] == OpUpdateWatched || head[0] == OpUpdateStepped)
        {
            watchUpdate = head[0] == OpUpdateWatched ? 1 : head[0] == OpUpdateStepped ? 2 : 0;
            /* UpdateRequest, its points, u32 count, count PlanActor rows. Reply: UpdateReply, then the path's points. */
            const UpdateRequest *q = (const UpdateRequest *)buffer;
            const float *pts = (const float *)(buffer + sizeof *q);
            const uint8_t *rest = buffer + sizeof *q + q->points * 12;
            uint32_t count;
            memcpy(&count, rest, 4);
            UpdateReply r;
            Update(q, pts, (const PlanActor *)(rest + 4), count, &r);
            if (watchUpdate != 1) { Reply(&r, sizeof r); continue; }
            memcpy(reply, &r, sizeof r);
            memcpy(reply + sizeof r, watchMarks, sizeof watchMarks);
            Reply(reply, sizeof r + sizeof watchMarks);
            continue;
        }
        if (head[0] == OpPlan)
        {
            /* PlanRequest, then count PlanActor rows. Reply: u32 points, u32 ns planning, u32 ns in all (with the unit sync), points. */
            LARGE_INTEGER t0, t1;
            QueryPerformanceCounter(&t0);
            const PlanRequest *q = (const PlanRequest *)buffer;
            if (head[1] < sizeof *q || head[1] < sizeof *q + q->count * sizeof(PlanActor)) Fail("short plan request", NULL);
            uint32_t r[3];
            r[0] = Plan(q, (const PlanActor *)(buffer + sizeof *q), (float *)(reply + 12), (sizeof reply - 12) / 12, &r[1]);
            QueryPerformanceCounter(&t1);
            r[2] = (uint32_t)((t1.QuadPart - t0.QuadPart) * 1000000000LL / frequency.QuadPart);
            memcpy(reply, r, 12);
            Reply(reply, 12 + r[0] * 12);
            continue;
        }
        if (head[0] == 5)
        {
            /* The image offsets executed while stepping, as a bitmap (for the instruction-mix report). */
            Reply(executed, imageSize / 8);
            continue;
        }
        Fail("unknown request", NULL);
    }
}

int wmain(int argc, wchar_t **argv)
{
    if (argc < 3) Fail("usage: exehost <League of Legends.exe> <AIPath.aimesh_ngrid>", NULL);
    in = GetStdHandle(STD_INPUT_HANDLE);
    out = GetStdHandle(STD_OUTPUT_HANDLE);
    AddVectoredExceptionHandler(1, OnException);
    SetUnhandledExceptionFilter(OnUnhandled);
    int relocated = MapImage(argv[1]);
    executed = calloc(imageSize / 32 + 1, 4);
    PrepareClientState();
    LoadGrid(argv[2]);
    InitPlanner();
    InitMirrors();
    uint32_t ready[4] = {(uint32_t)(uintptr_t)image, (uint32_t)relocated, (uint32_t)width, (uint32_t)height};
    Reply(ready, sizeof ready);
    Serve();
    return 0;
}
