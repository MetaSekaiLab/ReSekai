using System.Collections.Concurrent;
using System.Diagnostics;
using System.Xml.Linq;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ReSekai.Decompiler.Diagnostics;
using ReSekai.Decompiler.Managed;
using ReSekai.Decompiler.Output;
using ReSekai.Decompiler.Pipeline;
using ReSekai.Decompiler.Recovery;

static class SerialSourceChecks
{
    public static void Timeout()
    {
        bool finished = false;
        var guard = new MethodGuard(() => null!, TimeSpan.FromMilliseconds(50), _ => { });
        MethodDecompiler worker = null!;
        var timedOut = guard.Run(null!, 1, ref worker, _ =>
        {
            try
            {
                RecoveryCancellation.Token.WaitHandle.WaitOne();
                RecoveryCancellation.Check();
                throw new Exception("取消后仍在恢复");
            }
            finally { finished = true; }
        });
        if (!finished || timedOut.Holes.Single().Code != RecoveryHole.TimeoutCode || guard.Timeouts != 1 || guard.Crashes != 0)
            throw new Exception("超时恢复未退出或未标记");
        var next = new MethodRecoveryResult(null!, 2, null, []);
        if (guard.Run(null!, 2, ref worker, _ => next) != next)
            throw new Exception("超时后未继续恢复");
    }

    public static int Run(string assemblies, string output)
    {
        var watch = Stopwatch.StartNew();
        foreach (string name in new[] { "NaughtyAttributes.Core", "SekaiTool.Attribute" })
        {
            string assembly = Path.Join(assemblies, name + ".dll");
            Meter meter = new();
            int count = SourceProject.Write(assembly, assemblies, output, _ => true, meter);
            if (count == 0 || meter.Peak != 1 || meter.Threads.Count != 1 || !meter.Threads.ContainsKey(Environment.CurrentManagedThreadId))
                throw new Exception("源码生成没有严格串行执行");
            string folder = Path.Join(output, name + ".dll");
            var project = XDocument.Load(Path.Join(folder, name + ".csproj"));
            foreach (string option in new[] { "BuildInParallel", "UseSharedCompilation" })
                if (project.Descendants(option).Single().Value != "false") throw new Exception("编译并行设置：" + option);
            if (project.Descendants("CompilerResponseFile").Single().Value != "$(MSBuildThisFileDirectory)csc.rsp" || File.ReadAllText(Path.Join(folder, "csc.rsp")) != "-parallel-\n")
                throw new Exception("编译器并行设置未传入响应文件");
            string references = Path.Join(folder, "references");
            var comparison = Decompilation.Decompile(Path.Join(references, name + ".dll"), references, _ => true, true, groupByName: true, jobs: 2);
            foreach (var source in comparison)
            {
                string filename = source.TypeName is null ? "AssemblyInfo" : source.Namespace.Length == 0 ? source.TypeName : source.TypeName[(source.Namespace.Length + 1)..];
                filename = new string(filename.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
                string path = Path.Join(folder, source.TypeName is null ? "Properties" : source.Namespace, filename + ".cs");
                if (File.ReadAllText(path) != source.Source) throw new Exception("串行输出内容不同：" + path);
            }
            Console.WriteLine($"{name}: {count} 个类型，最大同时处理 {meter.Peak}，线程数 {meter.Threads.Count}，与并行输出逐字一致");
        }
        Console.WriteLine($"空桩检查通过：{watch.Elapsed.TotalSeconds:F2} s，进程峰值工作集 {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576d:F1} MiB");
        return 0;
    }

    sealed class Meter : IDecompileTransformProvider
    {
        public readonly ConcurrentDictionary<int, byte> Threads = new();
        int active;
        public int Peak;
        public void Register(CSharpDecompiler decompiler, DecompilePosition position)
        {
            if (position == DecompilePosition.BeforeLegality) decompiler.AstTransforms.Add(new Mark(() =>
            {
                Threads.TryAdd(Environment.CurrentManagedThreadId, 0);
                int current = Interlocked.Increment(ref active), previous;
                do { previous = Peak; } while (current > previous && Interlocked.CompareExchange(ref Peak, current, previous) != previous);
            }));
            if (position == DecompilePosition.AfterEvents) decompiler.AstTransforms.Add(new Mark(() => Interlocked.Decrement(ref active)));
        }
        sealed class Mark(Action action) : IAstTransform
        {
            public void Run(AstNode rootNode, TransformContext context) => action();
        }
    }
}
