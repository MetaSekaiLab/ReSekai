namespace ReSekai.Decompiler.Managed;

/// <summary>未恢复之处：编号与来源独立于可读说明，派生诊断保留根因编号。</summary>
public sealed record RecoveryHole(ulong Address, string Reason, bool Derived = false)
{
    internal const string TimeoutCode = "recovery.timeout";
    public string Code { get; init; } = "recovery.unsupported";
    public string Category => RecoveryCategories.Of(Code);
    public int Id { get; init; }
    public int? CauseId { get; init; }
}
public enum RuntimeDeclarationKind { Delegate, PlatformInvoke }
public sealed record MethodRecoveryResult(MethodRef Method, ulong Entry, ManagedBody? Body, IReadOnlyList<RecoveryHole> Holes)
{
    public IReadOnlyList<PassStep> Passes { get; init; } = [];
    public IReadOnlyList<ReSekai.Decompiler.Diagnostics.AnalysisStep> Analyses { get; init; } = [];
    public IReadOnlyList<ReSekai.Decompiler.Diagnostics.RuleTrace> Rules { get; init; } = [];
    public IReadOnlyList<ReSekai.Decompiler.Managed.CallResolution> Calls { get; init; } = [];
    /// <summary>L2 的拒绝记录与未消化的运行时痕迹，用于归因方法的洞。</summary>
    public IReadOnlyList<ReSekai.Decompiler.Diagnostics.RuleRejection> Rejections { get; init; } = [];
    /// <summary>已核验的运行时声明；没有恢复原生方法体，不计入方法体及字节覆盖率。</summary>
    public RuntimeDeclarationKind? Declaration { get; init; }
    public NativeImport? PlatformImport { get; init; }
    /// <summary>补回源码所依据的发布者参考；仅用于源码输出。</summary>
    public string? ReferenceSource { get; init; }
    /// <summary>有洞时已恢复部分组成的方法体：洞写成 <see cref="ManagedHole"/>/<see cref="HoleValue"/>，只供阅读，不进执行对照与指标。</summary>
    public ManagedBody? Partial { get; init; }
}
