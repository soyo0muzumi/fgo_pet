using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;

namespace FgoPet.Windows.Tests;

/// <summary>
/// WPF 集成测试的共享 STA 脚手架。
///
/// 此前每个测试类各存一份 <c>StaRun</c>（9 份副本、171 处调用），四处缺陷叠加：
///   1. <c>thread.Join()</c> 无超时；
///   2. STA 线程是**前台**线程；
///   3. 收尾用同步的 <c>Dispatcher.InvokeShutdown()</c>；
///   4. 没有安装 <c>DispatcherSynchronizationContext</c>。
///
/// 第 3 条才是「Windows.Tests 整体挂死」的真正根因：<c>InvokeShutdown()</c> 会一直等到
/// dispatcher 真正收摊，而测试线程此刻既没有泵消息、也泵不了（打开过 Popup / ContextMenu
/// 的线程尤其如此），于是永久阻塞在关停里。1、2 两条只是把「某条测试卡住」放大成
/// 「整个测试进程永不退出」，使门禁彻底失去意义。
///
/// 第 4 条让测试与生产不一致：生产里 <c>Application.Run</c> 一定会装上
/// <c>DispatcherSynchronizationContext</c>，事件处理器里 <c>await</c> 之后的延续会回到 UI 线程；
/// 测试里没有它，延续直接掉到线程池上，再去读 DependencyObject 就抛
/// <c>InvalidOperationException</c>；因为是 <c>async void</c>，异常无人接管，把 testhost 整个崩掉。
///
/// 本类型用后台线程、带超时的等待和消息泵把挂死判为失败，并安装同步上下文。
/// 关停请求必须在 STA 线程上泵送完成，不能投递 BeginInvokeShutdown 后直接结束线程。
/// </summary>
internal static class StaRunner
{
    /// <summary>单条 STA 测试的默认时间预算。</summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(30);

    /// <summary>泵送消息队列的默认时间预算 —— 只用于让 Pending 的 UI 工作落地，不是等待业务。</summary>
    public static TimeSpan DefaultPumpTimeout { get; } = TimeSpan.FromSeconds(2);

    /// <summary>异步等待结束后留给线程收尾的宽限期。</summary>
    private static TimeSpan ShutdownGrace { get; } = TimeSpan.FromSeconds(5);

    // Only fixed lifecycle labels are recorded, never UI text or test input.
    private sealed class ExecutionState
    {
        public volatile string Phase = "thread-start";
    }

    public static void Run(Action action, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        var budget = timeout ?? DefaultTimeout;
        Exception? failure = null;
        var state = new ExecutionState();

        // IsBackground 是关键：即便下面的超时兜底生效、测试被判失败，卡住的 STA 线程也不会
        // 再把进程吊住。没有这一行，Join 超时只是让"挂死"变成"慢一点的挂死"。
        var thread = new Thread(() =>
        {
            try
            {
                state.Phase = "dispatcher-initialization";
                InstallDispatcherSynchronizationContext();
                state.Phase = "test-action";
                action();
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                state.Phase = "dispatcher-shutdown";
                try { ShutdownDispatcher(); }
                catch (Exception error) { failure ??= error; }
                state.Phase = "thread-exit";
            }
        })
        {
            IsBackground = true,
            Name = "fgo-pet-sta",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(budget))
        {
            throw CreateTimeoutException(thread, budget, state.Phase);
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>
    /// <see cref="Run(Action, TimeSpan?)"/> 的异步版本：异步测试用 <c>await StaRun(async () =&gt; …)</c> 调用。
    /// 超时语义与同步版一致 —— 挂死判失败，不判成功。
    /// </summary>
    public static Task RunAsync(Func<Task> action, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        var budget = timeout ?? DefaultTimeout;
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new ExecutionState();

        var thread = new Thread(() =>
        {
            Exception? failure = null;
            try
            {
                state.Phase = "dispatcher-initialization";
                InstallDispatcherSynchronizationContext();
                state.Phase = "test-action";
                var work = action();
                state.Phase = "awaiting-test-action";

                // 不能写 work.GetAwaiter().GetResult()：装了 DispatcherSynchronizationContext 之后，
                // 异步延续要靠这个线程泵消息才能跑，而 GetResult() 会把线程堵死 —— 自锁。
                // 所以一边泵一边等，等到了再 GetResult() 取回业务异常。
                if (!WaitForCompletion(work, budget))
                {
                    throw CreateTimeoutException(Thread.CurrentThread, budget, state.Phase);
                }

                work.GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                state.Phase = "dispatcher-shutdown";
                try { ShutdownDispatcher(); }
                catch (Exception error) { failure ??= error; }
                state.Phase = "thread-exit";
            }

            // Shutdown belongs to the test. Publishing success before it finishes
            // would prevent the outer timeout from reporting a hung cleanup.
            if (failure is not null) completion.TrySetException(failure);
            else completion.TrySetResult(null);
        })
        {
            IsBackground = true,
            Name = "fgo-pet-sta-async",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // Do not Join here: the caller may need to release work awaited by the STA.
        // The returned task includes dispatcher shutdown AND actual thread exit.
        return ObserveThreadAsync(thread, completion.Task, budget, state);
    }

    private static async Task ObserveThreadAsync(Thread thread, Task completion, TimeSpan budget, ExecutionState state)
    {
        using var deadline = new CancellationTokenSource(budget + ShutdownGrace);
        Exception? failure = null;
        try
        {
            await completion.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // A late fault must be observed even after the outer timeout was reported.
            _ = completion.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw CreateTimeoutException(thread, budget + ShutdownGrace, state.Phase);
        }
        catch (Exception error)
        {
            failure = error;
        }

        try
        {
            // Completion is signalled at the end of the thread delegate, just before
            // the OS thread exits. Join(0) observes that exit without blocking a worker.
            while (!thread.Join(TimeSpan.Zero))
                await Task.Delay(1, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw CreateTimeoutException(thread, budget + ShutdownGrace, state.Phase);
        }

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static TimeoutException CreateTimeoutException(Thread thread, TimeSpan budget, string phase) =>
        new($"STA 测试线程在 {budget.TotalSeconds:0.#}s 内未结束，判定为挂死。" +
            $"phase={phase}; threadState={thread.ThreadState}; threadId={thread.ManagedThreadId}。" +
            "此记录只定位超时阶段，不预先认定为 Dispatcher 关停或业务逻辑错误。");

    /// <summary>
    /// 泵送当前 Dispatcher 的消息队列，直到 ApplicationIdle **或**超时（以先到者为准）。
    /// 直接用 <c>Dispatcher.Invoke(..., ApplicationIdle)</c> 在 Popup 打开时永不返回。
    /// </summary>
    public static void Pump(TimeSpan? timeout = null) => Pump(Dispatcher.CurrentDispatcher, timeout);

    /// <summary>泵送指定 Dispatcher 的消息队列，直到 ApplicationIdle **或**超时。</summary>
    public static void Pump(Dispatcher dispatcher, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        var budget = timeout ?? DefaultPumpTimeout;

        // exitWhenRequested: false —— 由我们自己决定何时退出，避免 dispatcher 关停时提前跳出。
        var frame = new DispatcherFrame(exitWhenRequested: false);
        var stop = new DispatcherOperationCallback(_ =>
        {
            frame.Continue = false;
            return null;
        });

        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, stop, null);

        // 超时兜底不能用 DispatcherTimer：它靠 WM_TIMER 驱动，一旦消息队列里有持续高于
        // ApplicationIdle 的工作，兜底就可能被无限延后。改成外部线程计时器主动往队列投一条
        // Normal 优先级消息 —— 既置 Continue=false，又能把阻塞在 GetMessage 里的循环唤醒。
        // Normal 优先级高于 ApplicationIdle，所以这条消息一定先于空闲回调被处理。
        using var guard = new Timer(
            _ =>
            {
                try
                {
                    dispatcher.BeginInvoke(DispatcherPriority.Normal, stop, null);
                }
                catch (InvalidOperationException)
                {
                    // Dispatcher 已关停，投不进去了 —— 直接结束帧，让 Pump 返回。
                    frame.Continue = false;
                }
            },
            null,
            budget,
            Timeout.InfiniteTimeSpan);

        PushFrame(dispatcher, frame);
    }

    /// <summary>
    /// 装上生产环境本来就有的同步上下文。没有它，事件处理器里 await 之后的延续会掉到线程池上，
    /// 再去读 DependencyObject 就直接抛 InvalidOperationException（async void → 崩掉整个 testhost）。
    /// </summary>
    private static void InstallDispatcherSynchronizationContext()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
    }

    /// <summary>
    /// 一边泵消息一边等 <paramref name="work"/> 完成；返回它是否已经完成。
    /// 超时后返回 false，由调用方判失败 —— 不能无限等。
    /// </summary>
    private static bool WaitForCompletion(Task work, TimeSpan budget)
    {
        if (work.IsCompleted)
        {
            return true;
        }

        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame(exitWhenRequested: false);
        var stop = new DispatcherOperationCallback(_ =>
        {
            frame.Continue = false;
            return null;
        });

        // 任务完成时唤醒帧。必须投一条 Normal 优先级的消息：只置 Continue=false 的话，
        // 循环还堵在 GetMessage 里，没人唤醒就永远不会去检查 Continue。
        work.ContinueWith(
            _ =>
            {
                try
                {
                    dispatcher.BeginInvoke(DispatcherPriority.Normal, stop, null);
                }
                catch (InvalidOperationException)
                {
                    frame.Continue = false;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        using var guard = new Timer(
            _ =>
            {
                try
                {
                    dispatcher.BeginInvoke(DispatcherPriority.Normal, stop, null);
                }
                catch (InvalidOperationException)
                {
                    frame.Continue = false;
                }
            },
            null,
            budget,
            Timeout.InfiniteTimeSpan);

        PushFrame(dispatcher, frame);
        return work.IsCompleted;
    }

    /// <summary>
    /// 推帧并泵消息。PushFrame 是静态方法：把帧推到**当前线程**的 dispatcher 上。
    /// 调用方本来就跑在 STA 线程上，所以这里等价于 dispatcher 自身；显式校验一次，
    /// 避免有人从别的线程误用后拿到一个永不退出的帧。
    /// </summary>
    private static void PushFrame(Dispatcher dispatcher, DispatcherFrame frame)
    {
        if (!ReferenceEquals(dispatcher, Dispatcher.CurrentDispatcher))
        {
            throw new InvalidOperationException(
                "StaRunner 的泵送只能在拥有该 Dispatcher 的线程上调用。");
        }

        Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// 在所属线程上请求关停并泵送到完成；外层等待仍负责超时判定。
    /// </summary>
    private static void ShutdownDispatcher()
    {
        var dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
        if (dispatcher is null || dispatcher.HasShutdownFinished)
        {
            return;
        }

        // Posting alone leaves WPF's native HWND callbacks attached to a dead
        // managed thread. Run the dispatcher so shutdown can release them before
        // the STA exits; Send priority avoids idle starvation from open popups.
        if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        Dispatcher.Run();
    }
}
