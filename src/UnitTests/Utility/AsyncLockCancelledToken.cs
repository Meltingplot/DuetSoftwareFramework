using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace UnitTests.Utility;

/// <summary>
/// DuetSharedLibrary.AsyncLock with a cancellation token that is cancelled already. It replaced Nito.AsyncEx.AsyncLock in most places (bb5edd5e), which takes
/// a free lock in that case and cancels only a wait for the lock (Nito.AsyncEx.Coordination.xml of 5.1.2, AsyncLock.LockAsync(CancellationToken): "If this is
/// already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available)"). The callers were written for
/// that, e.g. the event log, which locks with ApplicationStopping (Logger.cs:224, 258), see <see cref="Startup.ExecutedStageShutdown"/>
/// </summary>
[TestFixture]
public class AsyncLockCancelledToken
{
    private static CancellationToken Cancelled => new(true);

    [Test]
    public async Task FreeLockIsTakenAsynchronously()
    {
        DuetSharedLibrary.AsyncLock asyncLock = new();
        using (await asyncLock.LockAsync(Cancelled))
        {
            // The lock is held now
            await Assert.CatchAsync<OperationCanceledException>(async () => await asyncLock.LockAsync(Cancelled));
        }

        // And free again
        using (await asyncLock.LockAsync(Cancelled)) { }
    }

    [Test]
    public void FreeLockIsTaken()
    {
        DuetSharedLibrary.AsyncLock asyncLock = new();
        using (asyncLock.Lock(Cancelled))
        {
            Assert.Catch<OperationCanceledException>(() => asyncLock.Lock(Cancelled));
        }
        using (asyncLock.Lock(Cancelled)) { }
    }

    /// <summary>
    /// A held lock is not waited for with a cancelled token, and the attempt does not consume the lock
    /// </summary>
    [Test]
    public async Task HeldLockIsNotWaitedFor()
    {
        DuetSharedLibrary.AsyncLock asyncLock = new();
        Task waiter;
        using (await asyncLock.LockAsync())
        {
            waiter = Task.Run(async () =>
            {
                using (await asyncLock.LockAsync()) { }
            });
            await Task.Delay(40);       // let the waiter queue up

            Task<OperationCanceledException?> cancelled = Task.Run(async () => await Assert.CatchAsync<OperationCanceledException>(async () => await asyncLock.LockAsync(Cancelled)));
            Assert.That(await cancelled.WaitAsync(TimeSpan.FromSeconds(5)), Is.Not.Null);
            Assert.That(waiter.IsCompleted, Is.False);
        }

        // The queued waiter gets the lock, not the cancelled attempt
        await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        using (await asyncLock.LockAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token)) { }
    }
}
