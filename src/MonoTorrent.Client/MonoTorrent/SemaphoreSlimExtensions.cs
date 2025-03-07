﻿//
// SemaphoreSlimExtensions.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2020 Alan McGovern
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using System;
using System.Collections.Generic;
using System.Threading;

using ReusableTasks;

#nullable enable

namespace MonoTorrent
{
    static class SemaphoreSlimExtensions
    {
        internal readonly struct Releaser : IDisposable
        {
            readonly SemaphoreSlim? Semaphore;

            public Releaser (SemaphoreSlim semaphore)
                => Semaphore = semaphore;

            public void Dispose ()
                => Semaphore?.Release ();
        }

        internal static async ReusableTask<Releaser> EnterAsync (this SemaphoreSlim semaphore)
        {
            await semaphore.WaitAsync ().ConfigureAwait (false);
            return new Releaser (semaphore);
        }
    }


    class ReusableExclusiveSemaphore
    {
        public readonly struct Releaser : IDisposable
        {
            static readonly object completed = new object ();

            readonly ReusableTaskCompletionSource<object>? Task { get; }

            internal Releaser (ReusableTaskCompletionSource<object> task)
                => Task = task;

            public void Dispose ()
                => Task?.SetResult (completed);
        }

        static readonly Queue<ReusableTaskCompletionSource<object>> Cache = new Queue<ReusableTaskCompletionSource<object>> ();

        ReusableTaskCompletionSource<object>? current;

        public async ReusableTask<Releaser> EnterAsync ()
        {
            ReusableTaskCompletionSource<object> task;
            ReusableTaskCompletionSource<object>? existing;

            lock (Cache) {
                existing = current;
                if (Cache.Count == 0)
                    current = task = new ReusableTaskCompletionSource<object> ();
                else
                    current = task = Cache.Dequeue ();
            }

            if (existing != null) {
                await existing.Task;
                lock (Cache)
                    Cache.Enqueue (existing);
            }

            return new Releaser (task);
        }
    }
}
