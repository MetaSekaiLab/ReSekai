using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Idioms;

internal static class ReplacePass
{
    internal static Func<Expr, Expr?> Replace(Expr load, ulong value) => e => e == load ? Expr.C(value, e.Width) : null;
}
