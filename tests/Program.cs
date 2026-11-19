using ReSekai;
using ReSekai.Decompiler.Input;

if (args is ["shaders", var shaderApk, var shaderOutput]) return ResourceChecks.Shaders(shaderApk, shaderOutput);
if (args is ["textures", var textureApk, var textureOutput]) return ResourceChecks.Textures(textureApk, textureOutput);
if (args is ["controllers", var controllerApk, var controllerOutput]) return ResourceChecks.Controllers(controllerApk, controllerOutput);
if (args is ["animations", var animationApk, var metadata, var animationResources, var animationOutput]) return AnimationChecks.Run(animationApk, metadata, animationResources, animationOutput);
if (args is ["serial-sources", var stubAssemblies, var sourceOutput]) return SerialSourceChecks.Run(stubAssemblies, sourceOutput);
if (args is ["live-recovery", var binary, var liveMetadata]) return LiveRecoveryChecks.Run(binary, liveMetadata);

static void Assert(bool condition, string reason) { if (!condition) throw new Exception(reason); }
static void Reject(Action action)
{
    try { action(); } catch (Exception error) when (error is InvalidDataException or ArgumentException or IOException) { return; }
    throw new Exception("应拒绝无效输入");
}
AnimationChecks.Basic();
TerminalValueChecks.Run();
LiveRecoveryChecks.AddressTakenArgument();
SerialSourceChecks.Timeout();
Reject(() => Output.Under(Environment.CurrentDirectory, "../escape"));
Reject(() => new Binary([1, 2, 3]).U32());
Assert(Output.Guid("资源/按钮") == Output.Guid("资源/按钮") && Output.Guid("资源/按钮") != Output.Guid("资源/面板"), "稳定 GUID");
var reader = new Binary([0, 1, 0, 0, 0, 0, 0, 0], offset: 1);
Value.Read(new Schema("UInt8", "flag", 1, 0x4000), reader);
Assert(reader.Position == 4, "必须按文件位置对齐");
Assert(Output.Safe("CON.txt") == "_CON.txt", "Windows 保留名");
var settingsSchema = new Schema("GraphicsSettings", "Base");
Yaml settingsYaml = new(_ => "{fileID: 0}");
settingsYaml.Document(30, 7, settingsSchema, new Value(settingsSchema, null), settings: true);
settingsYaml.Document(30, 7, settingsSchema, new Value(settingsSchema, null));
Assert(settingsYaml.ToString().Contains("--- !u!30 &1\nGraphicsSettings:")
    && settingsYaml.ToString().Contains("--- !u!30 &7\nGraphicsSettings:"), "项目设置与普通资源使用各自的对象编号");
var prefab = new Document("player", 1, 1, Output.Guid("prefab"), [1, 2, 3, 4, long.MinValue]) { Prefab = true };
var reordered = new Document("player", 1, 1, prefab.Guid, prefab.Objects.Reverse().ToArray()) { Prefab = true };
prefab.MapPrefabFileIds();
reordered.MapPrefabFileIds();
Assert(prefab.Objects.Select(prefab.FileId).Distinct().Count() == prefab.Objects.Length
    && prefab.Objects.All(id => prefab.FileId(id) >= 1L << 62 && prefab.FileId(id) == reordered.FileId(id))
    && prefab.FileId(0) == 0, "Prefab fileID 稳定且不与保留 ID 重叠");
Console.WriteLine("基础检查通过");
if (args.Length == 0) return 0;
using Apk apk = new(args[0]);
string input = args[1];
var model = ModelBuilder.Load(Path.Join(input, "libil2cpp.so"), Path.Join(input, "global-metadata.dat")).Model;
Output output = new(Path.Join(Environment.CurrentDirectory, "reader.local.result"));
Packages packages = new();
Project project = new(output, new(model, packages, output), new(model), packages);
Resources resources = new(apk, args.Length > 2 ? args[2] : null);
int total = 0, failed = 0;
foreach (var unit in resources.Units)
{
    var files = resources.Open(unit);
    project.Scan(files);
    foreach (var file in files)
    foreach (var item in file.Objects)
    {
        total++;
        try { project.Read(file, item); }
        catch (Exception error)
        {
            failed++;
            output.Note("读取", $"{file.Name}/{item.Id}", error.Message, true);
            if (failed <= 20) Console.WriteLine($"{file.Name}/{item.Id} 类 {file.Types[item.Type].ClassId}: {error.Message}");
        }
    }
    Console.WriteLine($"{unit.Name}: 读取 {total}，失败 {failed}");
}
output.Report("read");
Assert(failed == 0, $"读取失败 {failed}/{total}");
return 0;
