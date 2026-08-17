using System.Runtime.InteropServices;

namespace ExLlamaSharp.Native;

/// <summary>
/// Owns an <c>exl_engine_t*</c> and releases it via <c>exl_engine_destroy</c>.
/// </summary>
internal sealed class ExLlamaEngineHandle : SafeHandle
{
    public ExLlamaEngineHandle()
        : base(invalidHandleValue: nint.Zero, ownsHandle: true)
    {
    }

    public ExLlamaEngineHandle(nint handle, bool ownsHandle)
        : base(invalidHandleValue: nint.Zero, ownsHandle)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == nint.Zero;

    protected override bool ReleaseHandle()
    {
        if (!IsInvalid)
        {
            NativeMethods.EngineDestroy(handle);
            handle = nint.Zero;
        }

        return true;
    }
}
