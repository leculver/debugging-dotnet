// This code is intended to reproduce a deadlock scenario where a finalizer is blocked by a lock that is held by another thread.
// This is not intended to be code that should be used in any other context.

using System;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Collections.Generic;

namespace BlockedFinalizer
{
    class BlockedFinalizer
    {
        public static object Sync { get; } = new object();
        public static ManualResetEventSlim s_event = new ManualResetEventSlim(false);

        private readonly List<object> _list = new List<object>() { new object(), new object(), new object() };

        public BlockedFinalizer()
        {
        }

        ~BlockedFinalizer()
        {
            // Signal the main thread that we have reached our finalizer.
            SetEventAsync();

            // Held by another thread which never releases it.
            lock (Sync)
            {
                Console.WriteLine("Never executes, lock(Sync) blocks forever.");
            }
        }

        private async void SetEventAsync()
        {
            await Task.Delay(2000);
            s_event.Set();
        }
        
        public static void WaitForEvent()
        {
            s_event.Wait();
        }
    }

    class Program
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void AllocateObjects()
        {
            for (int i = 0; i < 1000; i++)
                new BlockedFinalizer();
        }

        static void Main(string[] args)
        {
            // Start a thread that will hold BlockedFinalizer.Sync forever.
            Thread thread = new Thread(ThreadProc);
            thread.Start();

            // Allocate some objects.  This needs to be in a non-inlined method
            // of its own to ensure the JIT won't keep objects alive in the
            // method's stack frame.
            AllocateObjects();
            
            // Make sure those objects are collected.
            GC.Collect();

            // Ensure we've entered a finalizer.
            BlockedFinalizer.WaitForEvent();

            // Break into the debugger.
            Console.WriteLine("Repro ready.");
            Debugger.Break();
        }

        static void ThreadProc()
        {
            lock (BlockedFinalizer.Sync)
            {
                while (true)
                {
                    // Do some work...
                    Thread.Sleep(1000);
                }
            }
        }
    }
}
