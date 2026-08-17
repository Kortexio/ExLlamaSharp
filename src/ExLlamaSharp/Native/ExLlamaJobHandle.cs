using System.Runtime.InteropServices;

namespace ExLlamaSharp.Native;

/// <summary>
/// Non-owning view of an <c>exl_job_t*</c>. The native engine retains ownership;
/// this handle only tracks the pointer for polling/cancel and does not free it.
/// </summary>
internal sealed class ExLlamaJobHandle : SafeHandle
{
    public ExLlamaJobHandle()
        : base(invalidHandleValue: nint.Zero, ownsHandle: false)
    {
    }

    public ExLlamaJobHandle(nint handle)
        : base(invalidHandleValue: nint.Zero, ownsHandle: false)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == nint.Zero;

    protected override bool ReleaseHandle()
    {
        // Engine owns the job; nothing to free on the managed side.
        handle = nint.Zero;
        return true;
    }
}
