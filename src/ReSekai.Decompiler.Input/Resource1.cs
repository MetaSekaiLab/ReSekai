using System;
using System.IO;

namespace Il2CppDumper
{
    /// <summary>Dumper 源码引用 Resource1.Il2CppDummyDll；这里用嵌入资源替代其 resx 生成类。</summary>
    internal static class Resource1
    {
        internal static byte[] Il2CppDummyDll
        {
            get
            {
                using var source = typeof(Resource1).Assembly.GetManifestResourceStream("Il2CppDummyDll")
                    ?? throw new InvalidOperationException("缺少嵌入资源 Il2CppDummyDll");
                using var output = new MemoryStream();
                source.CopyTo(output);
                return output.ToArray();
            }
        }
    }
}
