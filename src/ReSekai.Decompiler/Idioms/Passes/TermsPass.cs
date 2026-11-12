using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Idioms;

internal static class TermsPass
{
    internal static List<Expr> Terms(Expr e) => e is Binary { Op: BinOp.Add } b ? [.. TermsPass.Terms(b.Left), .. TermsPass.Terms(b.Right)] : [e];
}
