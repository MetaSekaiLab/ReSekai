using ReSekai.Decompiler.Facts;
using Mono.Cecil.Cil;

namespace ReSekai.Decompiler.Output;

internal sealed partial class CilBodyWriter
{
    private void EmitDelegate(DelegateValue value)
    {
        Value(value.Target);
        if (value.Binding == DelegateBinding.VirtualInstance)
        {
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldvirtftn, MethodReference(value.Method));
        }
        else il.Emit(OpCodes.Ldftn, MethodReference(value.Method));
        il.Emit(OpCodes.Newobj, MethodReference(DelegateFacts.Constructor(value.DelegateType)!));
    }
}
