
namespace ReSekai.Decompiler.Diagnostics;

public enum PassConvergence { Single, Stable, BudgetExhausted }
public sealed record PassStep(string Name, bool Changed, int InputStatements, int OutputStatements,
    int Rounds, PassConvergence Convergence, double Milliseconds);
