using ReSekai.Decompiler.Recovery;

namespace ReSekai.Decompiler.Pipeline;

/// <summary>
/// --keep-going 的逐方法保护：在独立线程上恢复一个方法，崩溃或超时变成带原因的洞。
/// 超时后取消恢复并等待线程退出；失败后更换恢复器，避免复用不完整缓存。
/// 栈溢出不能捕获，这里给足够大的栈，真的溢出时进程仍然会退出。
/// </summary>
internal sealed class MethodGuard(Func<MethodDecompiler> create, TimeSpan limit, Action<string> log)
{
    private const int StackBytes = 64 * 1024 * 1024;
    private int crashes, timeouts;

    public int Crashes => crashes;
    public int Timeouts => timeouts;

    /// <summary>worker 在失败后换成新的。</summary>
    public MethodRecoveryResult Run(MethodRef method, ulong entry, ref MethodDecompiler worker, Func<MethodDecompiler, MethodRecoveryResult> recover)
    {
        MethodRecoveryResult? result = null;
        Exception? failure = null;
        var current = worker;
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var thread = new Thread(() =>
        {
            RecoveryCancellation.Token = token;
            try
            {
                RecoveryCancellation.Check();
                result = recover(current);
            }
            // 这里是这个选项唯一的用途：记录失败并继续，原因写进洞。
            catch (Exception e) { failure = e; }
        }, StackBytes) { IsBackground = true };
        thread.Start();
        string reason;
        var timedOut = !thread.Join(limit);
        if (timedOut)
        {
            Interlocked.Increment(ref timeouts);
            reason = $"恢复 超过时限 {limit.TotalSeconds:F0} s";
            cancellation.Cancel();
            if (!thread.Join(TimeSpan.FromSeconds(5)))
                throw new TimeoutException($"0x{entry:x} {method}：{reason}，恢复线程未响应取消");
        }
        else if (failure is not null)
        {
            Interlocked.Increment(ref crashes);
            var at = failure.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
            reason = $"恢复 崩溃 {failure.GetType().Name}：{failure.Message}（{at}）";
        }
        else return result!;
        log($"0x{entry:x} {method}：{reason}");
        worker = create();
        var hole = new RecoveryHole(entry, reason);
        if (timedOut) hole = hole with { Code = RecoveryHole.TimeoutCode };
        return new MethodRecoveryResult(method, entry, null, [hole]);
    }
}
