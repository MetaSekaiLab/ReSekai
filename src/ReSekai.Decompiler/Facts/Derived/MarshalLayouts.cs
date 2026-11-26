using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Machine.Flow;
using ReSekai.Decompiler.Machine.Lifting;
using ReSekai.Decompiler.Model;

namespace ReSekai.Decompiler.Facts.Derived;

/// <summary>
/// 字段的封送描述（MarshalAs）。元数据只留下 HasFieldMarshal 标志，描述本身被裁掉；IL2CPP 按它生成了封送函数，从函数里读回来：
/// 内嵌的定长数组在复制之前检查托管数组的长度不小于声明的元素数，不足就抛出。不需要封送函数的 bool 字段只能是单字节。
/// </summary>
internal static class MarshalLayouts
{
    private const int ArrayLengthOffset = 0x18;

    public static void Apply(BaseFacts facts)
    {
        var functions = facts.Registrations.Entries.Where(e => e.Kind == EntryKind.MarshalToNative && e.Detail is not null)
            .GroupBy(e => e.Detail!).ToDictionary(g => g.Key, g => g.Select(e => e.Address).Distinct().ToArray());
        foreach (var type in facts.Model.Types)
        {
            var marshaled = type.Fields.Where(f => !f.IsStatic && (f.Attributes & System.Reflection.FieldAttributes.HasFieldMarshal) != 0).ToArray();
            if (marshaled.Length == 0) continue;
            if (!functions.TryGetValue(type.FullName, out var entries))
            {
                foreach (var field in marshaled)
                    if (field.Type is DefinitionRef { Definition.FullName: "System.Boolean" }) field.Marshal = new(FieldMarshalKind.Byte, 0);
                continue;
            }
            if (entries is not [var entry] || !marshaled.Any(f => f.Type is ArrayRef)) continue;
            var lengths = Lengths(facts, entry);
            foreach (var field in marshaled)
                if (field.Type is ArrayRef && lengths.TryGetValue(field.Offset, out var length)) field.Marshal = new(FieldMarshalKind.FixedArray, length);
        }
    }

    /// <summary>各数组字段（按字段在对象里的偏移）要求的最小长度；同一字段的检查不一致时不算。</summary>
    private static Dictionary<int, int> Lengths(BaseFacts facts, ulong entry)
    {
        var found = new Dictionary<int, HashSet<int>>();
        var lifted = new FunctionLifter(facts) { InlinesClosures = true }.Lift(entry, null, null, simplify: true, promoteFrameSlots: false);
        if (!lifted.Succeeded) return [];
        var definitions = lifted.Function.Blocks.SelectMany(b => b.Statements).OfType<Define>().ToDictionary(d => d.Target, d => d.Value);
        Expr Resolve(Expr expression) => expression is Use use && use.Width == use.Variable.Width && definitions.TryGetValue(use.Variable, out var value) ? Resolve(value) : expression;
        (Expr Base, long Offset) Split(Expr address) => Resolve(address) switch
        {
            Binary { Op: BinOp.Add, Left: var left, Right: Const right } when Split(left) is var (inner, offset) => (inner, offset + right.Signed),
            var other => (other, 0),
        };
        // 对象是第一个参数；结构体从首字段开始，引用类型的偏移含对象头。
        int? Field(Expr pointer) => Resolve(pointer) is Load { Width: 64, Address: var address } && Split(address) is (Use { Variable: { Version: 0, Storage: { Bank: Bank.Gpr, Index: 0 } } }, var offset and >= 0) ? (int)offset : null;
        foreach (var branch in lifted.Function.Blocks.SelectMany(b => b.Statements).OfType<Branch>())
        {
            if (branch.Condition is not Compare { Left: var left, Right: Const bound } compare || Resolve(left) is not Load { Address: var location }
                || Split(location) is not (var array, ArrayLengthOffset) || Field(array) is not { } field) continue;
            int? length = compare.Cond switch
            {
                Cond.Ult or Cond.Slt or Cond.Uge or Cond.Sge => (int)bound.Value,
                Cond.Ule or Cond.Sle or Cond.Ugt or Cond.Sgt => (int)bound.Value + 1,
                Cond.Eq or Cond.Ne when bound.Value == 0 => 1,
                _ => null,
            };
            // 长度不足的一侧抛出，不返回。
            var starts = lifted.Function.Blocks.ToDictionary(b => b.Start);
            var fails = new[] { branch.Target, branch.Fallthrough }.Any(t => starts.TryGetValue(t, out var block) && block.Successors.Count == 0 && !block.Statements.Any(s => s is Return));
            if (length is not { } declared || declared <= 0 || !fails) continue;
            if (!found.TryGetValue(field, out var set)) found[field] = set = [];
            set.Add(declared);
        }
        return found.Where(f => f.Value.Count == 1).ToDictionary(f => f.Key, f => f.Value.Single());
    }
}
