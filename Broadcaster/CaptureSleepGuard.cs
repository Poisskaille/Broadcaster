using System;
using System.Runtime.InteropServices;

namespace Broadcaster
{
    internal sealed class CaptureSleepGuard : IDisposable
    {
        [Flags]
        private enum ExecutionState : uint
        {
            SystemRequired = 0x00000001,
            DisplayRequired = 0x00000002,
            Continuous = 0x80000000
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern ExecutionState SetThreadExecutionState(ExecutionState flags);

        private bool _active;

        // Call only on the UI thread that owns this guard. This does not prevent
        // manually requested sleep, shutdown or closing a laptop lid.
        public void Update(bool captureActive)
        {
            if (_active == captureActive) return;
            ExecutionState flags = captureActive
                ? ExecutionState.Continuous | ExecutionState.DisplayRequired | ExecutionState.SystemRequired
                : ExecutionState.Continuous;
            if (SetThreadExecutionState(flags) != 0)
                _active = captureActive;
        }

        public void Dispose() => Update(false);
    }
}
