using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

if (args.Length != 2) throw new ArgumentException("Usage: Patcher input.dll output.dll");
string input = Path.GetFullPath(args[0]);
string output = Path.GetFullPath(args[1]);
using var module = ModuleDefMD.Load(input);
var appType = module.Types.FirstOrDefault(t => t.Name == "App" && t.Methods.Any(m => m.Name == "OnStartup"))
    ?? throw new InvalidOperationException("App.OnStartup was not found.");
var startup = appType.Methods.First(m => m.Name == "OnStartup" && m.HasBody);
if (!startup.Body.Instructions.Any(i => i.Operand is IMethod m && m.DeclaringType.FullName == "AmrPDF.Updater.UpdateManager"))
{
    MethodInfo initialize = typeof(AmrPDF.Updater.UpdateManager).GetMethod(nameof(AmrPDF.Updater.UpdateManager.Initialize), BindingFlags.Public | BindingFlags.Static)!;
    var imported = new Importer(module).Import(initialize);
    startup.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Call, imported));
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var options = new ModuleWriterOptions(module);
options.MetadataOptions.Flags = MetadataFlags.PreserveAll;
module.Write(output, options);
Console.WriteLine($"Patched {appType.FullName}.{startup.Name}");
