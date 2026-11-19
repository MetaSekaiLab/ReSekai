using ReSekai.Decompiler.Input;
using ReSekai.Decompiler.Ir;
using ReSekai.Decompiler.Managed;
using ReSekai.Decompiler.Model;
using ReSekai.Decompiler.Output;
using ReSekai.Decompiler.Recovery;

static class LiveRecoveryChecks
{
    public static void AddressTakenArgument()
    {
        string assembly = typeof(AddressTakenArgumentFixture).Assembly.Location;
        var source = Decompilation.Decompile(assembly, Path.GetDirectoryName(assembly)!,
            type => type.Name == nameof(AddressTakenArgumentFixture), assemblyAttributes: false).Single();
        if (!source.Source.Contains("ReadBuffer(length: ReplaceBuffer(ref buffer), buffer: buffer)", StringComparison.Ordinal))
            throw new Exception("读取缓冲区实参之前必须完成 ref 调用");
    }

    public static int Run(string binary, string metadata)
    {
        var facts = FactsBuilder.Build(binary, metadata, inferUnregistered: false);
        var types = new ManagedTypes(facts.Model, facts.Layouts);
        var decompiler = new MethodDecompiler(facts, types);
        CheckNumber(Recover(0x63f2354, "Sekai.NumberView.UpdateNumber"));
        CheckDispose(Recover(0xab705fc, "Sekai.Rendering.SekaiUIBlurSetupPass.Dispose"));
        CheckSelectedField(Recover(0x656e5e0, "Sekai.ScreenLayerGachaResult.InitializeDisplay"));
        CheckResolutionDescriptor(Recover(0xa468aa4, "ResoDynamix.Runtime.Scripts.ResoDynamixController.Update"));
        var moveNext = facts.EntriesAt(0x4cdd3d0).Select(candidate => candidate.Method).OfType<MethodRef>()
            .Single(candidate => candidate.ToString() == "Sekai.OutGameController.<CreateScreenCore>d__52.MoveNext");
        CheckBootResumes(decompiler.SourceForm(decompiler.Recover(moveNext, 0x4cdd3d0)).Body
            ?? throw new Exception("未恢复首页初始化状态机"));
        foreach (var (entry, name) in new (ulong, string)[]
        {
            (0xa47e474, "Spine.AnimationState.Apply"), (0xa47eb18, "Spine.AnimationState.ApplyMixingFrom"),
            (0xa47f830, "Spine.AnimationState.ApplyEventTimelinesOnly"),
            (0xa47fab4, "Spine.AnimationState.ApplyMixingFromEventTimelinesOnly"),
            (0xa480798, "Spine.AnimationState.ComputeHold"),
            (0xa47a564, "Spine.DeformTimeline.Apply"),
        })
            if (RecoverResult(entry, name).Holes.Count != 0) throw new Exception($"{name} 的内联类型判断未恢复");
        CheckCompletionOperation();
        return 0;

        ManagedBody Recover(ulong entry, string name)
            => RecoverResult(entry, name).Body ?? throw new Exception($"未恢复 {name}");

        MethodRecoveryResult RecoverResult(ulong entry, string name)
        {
            var method = facts.EntriesAt(entry).Select(candidate => candidate.Method).OfType<MethodRef>()
                .Single(candidate => candidate.ToString() == name);
            return decompiler.Recover(method, entry);
        }

        void CheckCompletionOperation()
        {
            const string baseName = "Google.Play.Common.PlayAsyncOperation";
            const string derivedName = "Google.Play.Common.Internal.PlayAsyncOperationImpl";
            var methods = facts.Model.Types.Where(type => type.FullName is baseName or derivedName)
                .SelectMany(type => type.Methods.Where(method => method.Name is ".ctor" or "add_Completed" or "remove_Completed"))
                .Select(method => decompiler.SourceForm(decompiler.RecoverDefinition(method))).ToArray();
            var bodies = methods.Select(method => method.Body ?? throw new Exception($"未恢复 {method.Method}")).ToArray();
            var skeleton = new Skeleton(facts.Model);
            skeleton.ApplyDeclarations(methods);
            if (new BodyEmitter(skeleton, types).Check(bodies, EmissionPolicy.Source).FirstOrDefault().Value is { } error)
                throw new Exception(error.ToString());
            foreach (var body in bodies.Where(body => body.Method.Method.Owner.FullName == derivedName && body.Method.Method.Name != ".ctor"))
            {
                var call = body.Blocks.SelectMany(block => block.Statements).OfType<ManagedCall>()
                    .Single(call => call.Invocation.Method.Method.Name == body.Method.Method.Name);
                if (call.Invocation.Virtual || call.Invocation.Method.Method.Owner.FullName != baseName)
                    throw new Exception("经 MethodInfo 的事件访问器调用应保留基类分派");
            }
            Console.WriteLine("Spine 类型判断、异步操作构造函数和基类事件调用通过恢复检查");
        }
    }

    static void CheckNumber(ManagedBody body)
    {
        var statements = body.Blocks.SelectMany(block => block.Statements).ToLookup(statement => statement.Address);
        foreach (ulong address in new ulong[] { 0x63f2590, 0x63f25b4, 0x63f25e8, 0x63f2610, 0x63f2618, 0x63f261c })
        {
            bool indexed = statements[address].Select(statement => statement switch
            {
                ManagedAssignment read => Element(read.Value),
                ManagedStore write => Element(write.Target),
                _ => null,
            }).Any(element => element?.Index is BinaryValue { Op: BinOp.Add, Right: LocalValue });
            if (!indexed) throw new Exception($"0x{address:x} 丢失逐位下标");
        }
        Console.WriteLine("NumberView 的六处数组读写均保留逐位下标");
    }

    static void CheckDispose(ManagedBody body)
    {
        var statements = body.Blocks.SelectMany(block => block.Statements).ToArray();
        var load = statements.OfType<ManagedAssignment>().Single(statement => statement.Address == 0xab70604
            && statement.Value is FieldValue { Field.Field.Name: "m_UIBuffer" });
        var call = statements.OfType<ManagedCall>().Single(statement => statement.Address == 0xab7060c);
        if (!Equals(call.Invocation.Receiver, load.Target) && !Equals(call.Invocation.Receiver, load.Value))
            throw new Exception("Dispose 调用丢失非空分支验证过的接收者");
        Console.WriteLine("Dispose 使用非空分支验证过的 m_UIBuffer");
    }

    static void CheckSelectedField(ManagedBody body)
    {
        var reads = body.Blocks.SelectMany(block => block.Statements).OfType<ManagedAssignment>()
            .Where(statement => statement.Address == 0x656ec94).ToArray();
        if (reads is not [ManagedAssignment { Value: ChoiceValue
            {
                True: FieldValue { Field.Field.Name: "costResourceId" },
                False: FieldValue { Field.Field.Name: "costResourceId" },
            }}])
            throw new Exception("抽卡费用的字段读取必须只发生在选中的方案上");
        Console.WriteLine("抽卡费用保留条件字段读取");
    }

    static void CheckResolutionDescriptor(ManagedBody body)
    {
        var statements = body.Blocks.SelectMany(block => block.Statements).ToArray();
        var msaa = Array.FindIndex(statements, statement => statement is ManagedStore
        {
            Target: FieldValue { Accessor.Method.Method.Name: "set_msaaSamples", Pending: null },
        });
        var srgb = Array.FindIndex(statements, statement => statement is ManagedCall
        {
            Address: 0xa468d38, Invocation.Method.Method.Name: "set_sRGB",
        });
        if (msaa < 0 || srgb <= msaa) throw new Exception("应先通过属性写入 MSAA，再设置 sRGB");
        Console.WriteLine("动态分辨率的描述符保留 MSAA 和 sRGB 写入顺序");
    }

    static void CheckBootResumes(ManagedBody body)
    {
        var blocks = body.Blocks.ToDictionary(block => block.Address);
        var main = body.Regions.MaxBy(region => region.Try.Count)!;
        var nested = body.Regions.Single(region => region != main);
        var seen = new HashSet<int>();
        var address = main.Try[0];
        while (!nested.Try.Contains(address))
        {
            if (blocks[address].Statements is not [ManagedBranch
                {
                    Condition: ComparisonValue { Condition: Cond.Eq, Right: ConstantValue state },
                    Taken.Target: var resume, Fallthrough.Target: var next
                }]
                || !seen.Add((int)state.Bits)
                || nested.Try.Contains(resume)
                || blocks[resume].Statements[0] is not ManagedAssignment { Value: FieldValue { Field.Field.Name: var field } }
                || !field.StartsWith("<>u__", StringComparison.Ordinal))
                throw new Exception("首页初始化的外层等待必须直接恢复已保存的等待器");
            address = next;
        }
        if (!seen.SetEquals(Enumerable.Range(2, 7))) throw new Exception("外层等待状态分派不完整");
        Console.WriteLine("首页初始化的七个外层恢复点绕过内层 try 的分派");
    }

    static ArrayElementValue? Element(ManagedValue value) => value switch
    {
        ArrayElementValue element => element,
        FieldValue field => Element(field.Receiver),
        AddressValue address => Element(address.Target),
        _ => null,
    };
}

static class AddressTakenArgumentFixture
{
    public static byte ReadAfterReplacement()
    {
        byte[] buffer = [];
        return ReadBuffer(length: ReplaceBuffer(ref buffer), buffer: buffer);
    }

    private static int ReplaceBuffer(ref byte[] buffer)
    {
        buffer = [1, 2];
        return buffer.Length;
    }

    private static byte ReadBuffer(byte[] buffer, int length) => buffer[length - 1];
}
