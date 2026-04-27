{{ self.add_import("System.Threading")}}
{{ self.add_import("System.Threading.Tasks")}}

{% if self.include_once_check("ConcurrentHandleMap.cs") %}{% include "ConcurrentHandleMap.cs" %}{% endif %}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate void UniFfiFutureCallback(IntPtr continuationHandle, byte pollResult);

// Holds per-foreign-future state: a CancellationTokenSource plus a lock that guards
// whether the completion callback is safe to invoke.  Both the completion path and
// the Rust drop-callback path acquire this lock, which guarantees that:
//   • if the drop fires first  → _dropped is true when the completion path checks it,
//     so cb() is never called with freed uniffiCallbackData.
//   • if the completion fires first → the drop callback is blocked until cb() returns,
//     so Rust cannot free uniffiCallbackData while cb() is still running.
internal class UniffiForeignFutureHandle {
    internal readonly CancellationTokenSource Cts = new CancellationTokenSource();
    private readonly object _lock = new object();
    private bool _dropped = false;

    // Called by the Rust drop callback.  Marks the future as dropped (preventing any
    // concurrent completion invocation) and cancels the CancellationTokenSource.
    // Blocks until any in-progress TryInvokeCallback has finished.
    internal void MarkDropped() {
        lock (_lock) {
            _dropped = true;
            Cts.Cancel();
        }
    }

    // Invokes action only if Rust has not yet dropped this future.
    // Holds the lock across the invocation so MarkDropped() cannot return (and thus
    // cannot let Rust free uniffiCallbackData) until we are done.
    internal void TryInvokeCallback(Action invoke) {
        lock (_lock) {
            if (!_dropped) {
                invoke();
            }
        }
    }
}

internal static class _UniFFIAsync {
    internal const byte UNIFFI_RUST_FUTURE_POLL_READY = 0;
    // internal const byte UNIFFI_RUST_FUTURE_POLL_MAYBE_READY = 1;

    internal static ConcurrentHandleMap<TaskCompletionSource<byte>> _async_handle_map = new ConcurrentHandleMap<TaskCompletionSource<byte>>();
    public static ConcurrentHandleMap<UniffiForeignFutureHandle> _foreign_futures_map = new ConcurrentHandleMap<UniffiForeignFutureHandle>();

    // FFI type for Rust future continuations
    internal class UniffiRustFutureContinuationCallback
    {
        public static UniFfiFutureCallback callback = Callback;

        public static void Callback(IntPtr continuationHandle, byte pollResult)
        {
            if (_async_handle_map.Remove((ulong)continuationHandle.ToInt64(), out TaskCompletionSource<byte> task))
            {
                task.SetResult(pollResult);
            }
            // else: continuation already completed (e.g. waker called more than once), ignore
        }
    }

    public class UniffiForeignFutureDroppedCallbackImpl
    {
        public static _UniFFILib.UniffiForeignFutureDroppedCallback callback = Callback;

        public static void Callback(ulong handle)
        {
            if (_foreign_futures_map.Remove(handle, out UniffiForeignFutureHandle futureHandle))
            {
                futureHandle.MarkDropped();
            }
            // else: handle already removed, ignore
        }
    }

    public delegate F CompleteFuncDelegate<F>(ulong handle, ref UniffiRustCallStatus status);

    public delegate void CompleteActionDelegate(ulong handle, ref UniffiRustCallStatus status);

    private static async Task PollFuture(ulong rustFuture, Action<ulong, IntPtr, ulong> pollFunc)
    {
        byte pollResult;
        do 
        {
            var tcs = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
            IntPtr callback = Marshal.GetFunctionPointerForDelegate(UniffiRustFutureContinuationCallback.callback);
            ulong mapEntry = _async_handle_map.Insert(tcs);
            pollFunc(rustFuture, callback, mapEntry);
            pollResult = await tcs.Task;
        }
        while(pollResult != UNIFFI_RUST_FUTURE_POLL_READY);
    }

    public static async Task<T> UniffiRustCallAsync<T, F, E>(
        ulong rustFuture,
        Action<ulong, IntPtr, ulong> pollFunc,
        CompleteFuncDelegate<F> completeFunc,
        Action<ulong> freeFunc,
        Func<F, T> liftFunc,
        CallStatusErrorHandler<E> errorHandler
    ) where E : System.Exception
    {
        try {
            await PollFuture(rustFuture, pollFunc);
            var result = _UniffiHelpers.RustCallWithError(errorHandler, (ref UniffiRustCallStatus status) => completeFunc(rustFuture, ref status));
            return liftFunc(result);
        }
        finally
        {
            freeFunc(rustFuture);
        }
    }

    public static async Task UniffiRustCallAsync<E>(
        ulong rustFuture,
        Action<ulong, IntPtr, ulong> pollFunc,
        CompleteActionDelegate completeFunc,
        Action<ulong> freeFunc,
        CallStatusErrorHandler<E> errorHandler
    ) where E : System.Exception
    {
         try {
            await PollFuture(rustFuture, pollFunc);
            _UniffiHelpers.RustCallWithError(errorHandler, (ref UniffiRustCallStatus status) => completeFunc(rustFuture, ref status));

        }
        finally
        {
            freeFunc(rustFuture);
        }
    }
}