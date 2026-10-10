#pragma once
#include <stdlib.h>
#include "xpress.h"
#include "xpress9.h"
#include <stdio.h>

typedef unsigned char BYTE;
typedef int INT;
typedef unsigned int UINT;
typedef void VOID;
#define UNREFERENCED_PARAMETER(P) (void)(P)

// On Windows, export/import symbols appropriately
#if defined(_MSC_VER)
    #ifdef XPRESS9DLL_EXPORT
        #define XPRESS9DLL_API __declspec(dllexport)
    #elif defined(BUILD_STATIC)  // Add this check for static builds
        #define XPRESS9DLL_API    
    #else
        #define XPRESS9DLL_API __declspec(dllimport)
    #endif
#else
    #ifdef XPRESS9DLL_EXPORT
        #define XPRESS9DLL_API __attribute__((visibility("default")))
    #elif defined(BUILD_STATIC)
        #define XPRESS9DLL_API
    #else
        #define XPRESS9DLL_API
    #endif
#endif

typedef struct {
    XPRESS9_DECODER decoder;   // used for decompression
    XPRESS9_ENCODER encoder; // new: used for compression
} XPRESS9_CONTEXT;

XPRESS9DLL_API XPRESS9_CONTEXT* Initialize();
XPRESS9DLL_API VOID Terminate(XPRESS9_CONTEXT* context);
XPRESS9DLL_API UINT Decompress(XPRESS9_CONTEXT* context, BYTE *compressed, INT compressedSize, BYTE *original, INT maxOriginalSize);
XPRESS9DLL_API UINT Compress(XPRESS9_CONTEXT* context, BYTE *original, INT originalSize, BYTE *compressed, INT maxCompressedSize);

#define MAX_ORIGINAL_SIZE 64 * 1024