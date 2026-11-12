namespace ReSekai.Decompiler.Diagnostics;

internal static class RecoveryCancellation
{
    [ThreadStatic]
    internal static CancellationToken Token;

    internal static void Check() => Token.ThrowIfCancellationRequested();
}
