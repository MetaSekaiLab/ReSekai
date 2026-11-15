using System.Numerics;
using ReSekai.Decompiler.Ir;

namespace ReSekai.Decompiler.Recovery;

public sealed partial class MethodDecompiler
{
    /// <summary>值类型的位标志测试可能被 IL2CPP 收窄为 ldrb；按字段布局把该字节的掩码还原到整个无符号字段。</summary>
    private MethodRecoveryResult? RestorePackedFlag(MethodRef method, ulong entry)
    {
        var definition = method.Method;
        if (definition.IsStatic || definition.Parameters.Count != 0 || !definition.ReturnType.Equals(types.Boolean)
            || definition.Owner is not { IsValueType: true, Size: 4 } owner
            || owner.Fields.Where(f => !f.IsStatic).ToArray() is not [{ Offset: 0 } field] || !field.Type.Equals(types.UInt32)
            || facts.Code.Word(entry) is not { } load || (load & 0xffc003ff) != 0x39400008
            || facts.Code.Word(entry + 4) is not { } test) return null;

        int offset = (int)((load >> 10) & 0xfff);
        int first = (int)((test >> 16) & 63);
        int last = (int)((test >> 10) & 63);
        if (offset > 3 || first >= 32 || last >= 31) return null;
        uint mask;
        Cond condition = Cond.Ne;
        const uint ret = 0xd65f03c0;
        var next = facts.Code.Word(entry + 8);
        if ((test & 0xffc003ff) == 0x7200011f && facts.Code.Word(entry + 12) == ret
            && next is 0x1a9f07e0 or 0x1a9f17e0)
        {
            mask = BitOperations.RotateRight((1u << (last + 1)) - 1, first) & 255;
            if (next == 0x1a9f17e0) condition = Cond.Eq;
        }
        else if (next == ret && (test & 0xffc003ff) == 0x53000100 && first == last && first < 8)
            mask = 1u << first;
        else if (next == ret && test == 0x12000100)
            mask = 1;
        else return null;

        var self = new ArgumentValue(-1, new ByRefRef(method.DeclaringType), 64);
        var value = new FieldValue(self, new(field, method.DeclaringType), types.UInt32, 32);
        var bits = new BinaryValue(BinOp.And, value, new ConstantValue(mask << (8 * offset), types.UInt32, 32), types.UInt32, 32);
        var result = new ComparisonValue(condition, bits, new ConstantValue(0, types.UInt32, 32), types.Boolean);
        return new(method, entry, new ManagedBody(method, [new ManagedBlock(entry, [new ManagedReturn(entry, result)])]), []);
    }
}
