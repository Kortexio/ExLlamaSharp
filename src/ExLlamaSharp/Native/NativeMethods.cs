using System.Runtime.InteropServices;

namespace ExLlamaSharp.Native;

/// <summary>
/// Source-generated P/Invoke bindings for <c>exllamasharp_native.dll</c> (C ABI).
/// Named distinctly from managed <c>ExLlamaSharp.dll</c> (Windows is case-insensitive).
/// </summary>
internal static partial class NativeMethods
{
    private const string Lib = "exllamasharp_native";

    /* ---- enums / status (mirror exllamasharp.h) ---- */

    public enum ExlStatus : int
    {
        Ok = 0,
        ErrInvalidArg = -1,
        ErrNotLoaded = -2,
        ErrAlreadyLoaded = -3,
        ErrBusy = -4,
        ErrOom = -5,
        ErrCancelled = -6,
        ErrInternal = -7,
        ErrNotRunning = -8,
        ErrUnsupported = -9,
    }

    public enum ExlParallelism : int
    {
        None = 0,
        Tensor = 1,
        Pipe = 2,
        Model = 3,
    }

    public enum ExlJobState : int
    {
        Waiting = 0,
        Running = 1,
        Swapped = 2,
        Finished = 3,
        Cancelled = 4,
        Failed = 5,
    }

    /* ---- structs ---- */

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ExlEngineConfig
    {
        public int Parallelism;
        public int NumDevices;
        public int* DeviceIds;
        public int MaxNumSeqs;
        public int MaxNumBatchedTokens;
        public int MaxChunkSize;
        public int PageSize;
        public int MaxPages;
        public float GpuMemoryUtilization;
        public int Seed;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ExlJobParams
    {
        public int* PromptTokens;
        public int PromptLength;
        public int MaxNewTokens;
        public float Temperature;
        public float TopP;
        public int TopK;
        public int Priority;
        public int StopTokenId;
        public void* UserData;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ExlMetrics
    {
        public long TotalPromptTokens;
        public long TotalGeneratedTokens;
        public long NumJobsWaiting;
        public long NumJobsRunning;
        public long NumJobsSwapped;
        public long NumJobsFinished;
        public long NumPagesUsed;
        public long NumPagesFree;
        public double TokensPerSecond;
        public double LastStepMs;
        public long StepCount;
    }

    /* ---- Engine lifecycle ---- */

    [LibraryImport(Lib, EntryPoint = "exl_engine_config_init")]
    public static partial void EngineConfigInit(ref ExlEngineConfig cfg);

    [LibraryImport(Lib, EntryPoint = "exl_engine_create")]
    public static unsafe partial nint EngineCreate(ExlEngineConfig* cfg);

    [LibraryImport(Lib, EntryPoint = "exl_engine_destroy")]
    public static partial void EngineDestroy(nint engine);

    [LibraryImport(Lib, EntryPoint = "exl_engine_load", StringMarshalling = StringMarshalling.Utf8)]
    public static partial ExlStatus EngineLoad(nint engine, string modelPath);

    [LibraryImport(Lib, EntryPoint = "exl_engine_unload")]
    public static partial ExlStatus EngineUnload(nint engine);

    [LibraryImport(Lib, EntryPoint = "exl_engine_start")]
    public static partial ExlStatus EngineStart(nint engine);

    [LibraryImport(Lib, EntryPoint = "exl_engine_stop")]
    public static partial ExlStatus EngineStop(nint engine);

    [LibraryImport(Lib, EntryPoint = "exl_engine_step")]
    public static partial ExlStatus EngineStep(nint engine);

    [LibraryImport(Lib, EntryPoint = "exl_engine_metrics")]
    public static partial ExlStatus EngineMetrics(nint engine, out ExlMetrics metrics);

    /* ---- Jobs ---- */

    [LibraryImport(Lib, EntryPoint = "exl_job_submit")]
    public static unsafe partial ExlStatus JobSubmit(nint engine, ExlJobParams* parameters, out nint outJob);

    [LibraryImport(Lib, EntryPoint = "exl_job_cancel")]
    public static partial ExlStatus JobCancel(nint engine, nint job);

    [LibraryImport(Lib, EntryPoint = "exl_job_state")]
    public static partial ExlJobState JobState(nint job);

    [LibraryImport(Lib, EntryPoint = "exl_job_tokens")]
    public static unsafe partial ExlStatus JobTokens(nint job, int* outTokens, ref int inoutCount);

    /* ---- Tokenization ---- */

    [LibraryImport(Lib, EntryPoint = "exl_tokenize", StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial ExlStatus Tokenize(nint engine, string text, int* outTokens, ref int inoutCount);

    [LibraryImport(Lib, EntryPoint = "exl_detokenize")]
    public static unsafe partial ExlStatus Detokenize(
        nint engine,
        int* tokens,
        int count,
        byte* outText,
        ref int inoutNbytes);

    /* ---- Diagnostics ---- */

    [LibraryImport(Lib, EntryPoint = "exl_last_error", StringMarshalling = StringMarshalling.Utf8)]
    public static partial string LastError();

    [LibraryImport(Lib, EntryPoint = "exl_version", StringMarshalling = StringMarshalling.Utf8)]
    public static partial string Version();
}
